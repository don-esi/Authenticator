using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Authenticator.Crypto;
using Authenticator.Data.Models;
using Authenticator.Logging;
using Authenticator.Services;
using Authenticator.Services.Providers;

namespace Authenticator;

public partial class VaultView : UserControl
{
    private enum VaultFilterMode
    {
        Relevant,
        All,
        Favorites,
        Recents,
        Conflicts
    }

    private const double SlideActionWidth = 184; // Edit + Delete + Fill + Login
    private const double SlideOpenThreshold = 0.45;

    private readonly VaultItemService _vault = new();
    private readonly VaultUiPreferences _uiPrefs = new();
    private ObservableCollection<VaultRow> _rows = new();
    private ObservableCollection<VaultRow> _allRowsView = new();
    private List<VaultItem> _all = new();
    private Dictionary<Guid, VaultRow> _rowById = new();
    private Dictionary<Guid, string>? _passwordIndex;
    private VaultFilterMode _filter = VaultFilterMode.All;
    private VaultSortMode _sortMode = VaultSortMode.TitleAsc;
    private VaultGroupMode _groupMode = VaultGroupMode.None;
    /// <summary>Groups start collapsed; only keys the user expands are kept here.</summary>
    private readonly HashSet<string> _expandedGroups = new(StringComparer.OrdinalIgnoreCase);
    private string? _autoFilter;
    private IReadOnlyList<string> _autoMatchTerms = Array.Empty<string>();
    private string? _capturedHost;
    private DomainEquivalenceSnapshot? _domainEquivalence;
    private IntPtr _foregroundHwnd;
    private bool _autoFillInFlight;
    private bool _chipSync;
    private bool _conflictScanRunning;
    private bool _skipConflictScanOnReload;
    private CancellationTokenSource? _conflictScanCts;
    private readonly DispatcherTimer _searchDebounce;
    private readonly DispatcherTimer _toastTimer;

    private Border? _slideFront;
    private TranslateTransform? _slideTransform;
    private double _slideActionWidth = SlideActionWidth;
    private Point _slideStart;
    private double _slideOriginX;
    private bool _slideMoved;
    private Guid? _lastRowClickId;
    private DateTime _lastRowClickUtc;

    public VaultView()
    {
        InitializeComponent();
        LoadUiPrefs();
        ItemsList.ItemsSource = _rows;
        _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            _ = ApplyFilterAsync();
        };
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1400) };
        _toastTimer.Tick += (_, _) => HideStatusToast();
        ForegroundWindowMonitor.Instance.ForegroundChanged += (_, info) =>
        {
            // Extension tab host wins when present; UIA capture is fallback for Relevant only.
            if (string.IsNullOrWhiteSpace(VaultNativePipeServer.Instance.LastExtensionHost))
            {
                _autoFilter = info.UrlHint;
                _capturedHost = info.CapturedHost;
            }

            _foregroundHwnd = info.Hwnd;
            _autoMatchTerms = info.MatchTerms.Count > 0
                ? info.MatchTerms
                : string.IsNullOrWhiteSpace(info.UrlHint)
                    ? Array.Empty<string>()
                    : new[] { info.UrlHint };

            UpdateFilterHint();
            // Auto-filter only drives the Relevant chip — never All/Favorites/Recents.
            // Browser autofill is handled by the extension (not UIA AutoFillService).
            if (_filter == VaultFilterMode.Relevant)
                ApplyFilter();
        };

        VaultNativePipeServer.Instance.TabHostChanged += (_, host) =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                _capturedHost = host;
                _autoFilter = host;
                if (!string.IsNullOrWhiteSpace(host))
                    _autoMatchTerms = new[] { host };
                UpdateFilterHint();
                if (_filter == VaultFilterMode.Relevant)
                    ApplyFilter();
            });
        };
    }

    private void LoadUiPrefs()
    {
        var prefs = _uiPrefs.Load();
        _sortMode = prefs.SortMode;
        _groupMode = prefs.GroupMode;
    }

    private void PersistUiPrefs()
    {
        var prefs = _uiPrefs.Load();
        prefs.SortMode = _sortMode;
        prefs.GroupMode = _groupMode;
        _uiPrefs.Save(prefs);
    }

    public async Task ReloadAsync(bool favoritesOnly = false)
    {
        if (favoritesOnly)
            SelectFilter(VaultFilterMode.Favorites, reload: false);

        CloseOpenSlide();
        _conflictScanCts?.Cancel();
        try
        {
            LoadUiPrefs();
            _passwordIndex = null;
            _domainEquivalence = await DomainEquivalenceService.LoadSnapshotAsync();
            // List path: no secret decrypt — filter/tab switches must stay cheap.
            _all = await _vault.GetItemsAsync(favoritesOnly: false, revealSecrets: false);
            var rows = _all.Select(VaultRow.From).ToList();
            _rowById = rows.ToDictionary(r => r.Id);
            _allRowsView = new ObservableCollection<VaultRow>(rows);
            await ApplyFilterAsync();
            // Conflict hash compare is expensive — kick a lazy background scan when providers are live.
            // Skip when vault is empty or only one provider is linked (clean single-store import).
            if (!_skipConflictScanOnReload && _all.Count > 0 && GetSyncedProviderIds().Count > 1)
                ScheduleConflictScan(announce: false);
        }
        catch (Exception ex)
        {
            AppDialog.Alert(Window.GetWindow(this), $"Could not load vault: {ex.Message}", "Vault", AppDialogKind.Warning);
        }
    }

    /// <summary>
    /// After Settings developer purge: wipe in-memory vault + Conflicts chip, skip conflict re-scan, reload from DB.
    /// </summary>
    public async Task ReloadAfterDeveloperPurgeAsync()
    {
        _conflictScanCts?.Cancel();
        _skipConflictScanOnReload = true;
        try
        {
            CloseOpenSlide();
            _passwordIndex = null;
            _all = new List<VaultItem>();
            _rowById = new Dictionary<Guid, VaultRow>();
            _allRowsView = new ObservableCollection<VaultRow>();
            SelectFilter(VaultFilterMode.All, reload: false);
            UpdateConflictsChip();
            ItemsList.ItemsSource = Array.Empty<object>();
            await ReloadAsync();
        }
        finally
        {
            _skipConflictScanOnReload = false;
        }
    }

    private void ScheduleConflictScan(string? providerId = null, bool announce = true)
    {
        if (ProviderSessionService.Instance.LiveConnected.Count == 0 &&
            string.IsNullOrWhiteSpace(providerId))
            return;

        _conflictScanCts?.Cancel();
        _conflictScanCts = new CancellationTokenSource();
        var ct = _conflictScanCts.Token;
        _ = RunConflictScanAsync(providerId, announce, ct);
    }

    private async Task RunConflictScanAsync(string? providerId, bool announce, CancellationToken ct)
    {
        if (_conflictScanRunning)
            return;

        _conflictScanRunning = true;
        try
        {
            if (announce)
                await Dispatcher.InvokeAsync(() => ShowStatusToast("Checking for conflicts…"));

            var scanProviderId = providerId;
            var result = await Task.Run(async () =>
            {
                if (!string.IsNullOrWhiteSpace(scanProviderId))
                    return await ProviderSessionService.Instance.ScanConflictsAsync(scanProviderId, ct);
                return await ProviderSessionService.Instance.ScanAllConflictsAsync(ct);
            }, ct);

            ct.ThrowIfCancellationRequested();

            _skipConflictScanOnReload = true;
            try
            {
                await ReloadAsync();
            }
            finally
            {
                _skipConflictScanOnReload = false;
            }

            if (announce || result.Conflicts > 0)
            {
                await Dispatcher.InvokeAsync(() =>
                    ShowStatusToast(result.Conflicts > 0
                        ? $"{result.Conflicts} conflict(s) found"
                        : "No conflicts"));
            }
        }
        catch (OperationCanceledException)
        {
            // superseded by a newer scan
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Background conflict scan failed.", nameof(VaultView));
        }
        finally
        {
            _conflictScanRunning = false;
        }
    }

    public void ShowAllItems()
    {
        SelectFilter(VaultFilterMode.All, reload: false);
        ApplyFilter();
    }

    public void ShowFavorites()
    {
        SelectFilter(VaultFilterMode.Favorites, reload: false);
        ApplyFilter();
    }

    public void SetFavoritesFilter(bool favoritesOnly)
    {
        if (favoritesOnly)
            ShowFavorites();
        else
            ShowAllItems();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (SearchClearButton is not null)
            SearchClearButton.Visibility = string.IsNullOrEmpty(SearchBox.Text)
                ? Visibility.Collapsed
                : Visibility.Visible;
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    private void SearchClear_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";
        SearchBox.Focus();
        e.Handled = true;
    }

    private void SortButton_Click(object sender, RoutedEventArgs e)
        => OpenButtonMenu(SortButton);

    private void GroupButton_Click(object sender, RoutedEventArgs e)
        => OpenButtonMenu(GroupButton);

    private static void OpenButtonMenu(Button button)
    {
        if (button.ContextMenu is null)
            return;
        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.Placement = PlacementMode.Bottom;
        button.ContextMenu.IsOpen = true;
    }

    private void SortMenu_Opened(object sender, RoutedEventArgs e)
        => MarkCheckedMenuItems(sender, _sortMode.ToString());

    private void GroupMenu_Opened(object sender, RoutedEventArgs e)
        => MarkCheckedMenuItems(sender, _groupMode.ToString());

    private static void MarkCheckedMenuItems(object sender, string selectedTag)
    {
        if (sender is not ContextMenu menu)
            return;
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            var tag = item.Tag?.ToString();
            item.IsCheckable = tag is not null;
            item.IsChecked = string.Equals(tag, selectedTag, StringComparison.Ordinal);
        }
    }

    private void SortMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag }
            || !Enum.TryParse<VaultSortMode>(tag, out var mode)
            || mode == _sortMode)
            return;

        _sortMode = mode;
        PersistUiPrefs();
        ApplyFilter();
    }

    private void GroupMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag }
            || !Enum.TryParse<VaultGroupMode>(tag, out var mode)
            || mode == _groupMode)
            return;

        _groupMode = mode;
        _expandedGroups.Clear();
        PersistUiPrefs();
        ApplyFilter();
    }

    private void GroupHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: VaultGroupHeader header })
            return;

        if (!_expandedGroups.Remove(header.Title))
            _expandedGroups.Add(header.Title);

        CloseOpenSlide();
        ItemsList.ItemsSource = BuildGroupedFlatList(_rows);
        e.Handled = true;
    }

    private void FilterChip_Checked(object sender, RoutedEventArgs e)
    {
        // IsChecked="True" in XAML fires before later chips exist — wait until loaded.
        if (_chipSync || !IsLoaded || sender is not ToggleButton chip)
            return;

        var mode = chip == ChipRelevant ? VaultFilterMode.Relevant
            : chip == ChipFavorites ? VaultFilterMode.Favorites
            : chip == ChipRecents ? VaultFilterMode.Recents
            : chip == ChipConflicts ? VaultFilterMode.Conflicts
            : VaultFilterMode.All;
        SelectFilter(mode, reload: false);
        ApplyFilter();
    }

    private void FilterChip_Unchecked(object sender, RoutedEventArgs e)
    {
        if (_chipSync || !IsLoaded)
            return;
        if (ChipRelevant?.IsChecked != true && ChipAll?.IsChecked != true &&
            ChipFavorites?.IsChecked != true && ChipRecents?.IsChecked != true &&
            ChipConflicts?.IsChecked != true)
            SelectFilter(_filter, reload: false);
    }

    private void SelectFilter(VaultFilterMode mode, bool reload)
    {
        _filter = mode;
        if (ChipRelevant is null || ChipAll is null || ChipFavorites is null || ChipRecents is null)
            return;

        _chipSync = true;
        ChipRelevant.IsChecked = mode == VaultFilterMode.Relevant;
        ChipAll.IsChecked = mode == VaultFilterMode.All;
        ChipFavorites.IsChecked = mode == VaultFilterMode.Favorites;
        ChipRecents.IsChecked = mode == VaultFilterMode.Recents;
        if (ChipConflicts is not null)
            ChipConflicts.IsChecked = mode == VaultFilterMode.Conflicts;
        _chipSync = false;
        if (TryFindResource("VaultListTemplateSelector") is VaultListTemplateSelector selector)
            selector.UseConflictTemplate = mode == VaultFilterMode.Conflicts;
        UpdateFilterHint();
        if (reload)
            _ = ReloadAsync();
    }

    private void UpdateConflictsChip()
    {
        if (ChipConflicts is null)
            return;

        var count = _allRowsView.Count(r => r.HasMismatch);
        ChipConflicts.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ChipConflicts.Content = count > 0 ? $"Conflicts ({count})" : "Conflicts";
        if (count == 0 && _filter == VaultFilterMode.Conflicts)
            SelectFilter(VaultFilterMode.All, reload: false);
    }

    private void UpdateFilterHint()
    {
        if (FilterHint is null)
            return;

        if (_filter != VaultFilterMode.Relevant || string.IsNullOrWhiteSpace(_autoFilter))
        {
            FilterHint.Text = "";
            return;
        }

        FilterHint.Text = $"Filter: {_autoFilter}";
    }

    private void ApplyFilter() => _ = ApplyFilterAsync();

    private async Task ApplyFilterAsync()
    {
        if (SearchBox is null || ItemsList is null)
            return;

        CloseOpenSlide();
        var q = SearchBox.Text?.Trim() ?? "";

        if (q.Length > 0)
            await EnsurePasswordIndexAsync();

        UpdateConflictsChip();

        IEnumerable<VaultRow> rows = _filter switch
        {
            VaultFilterMode.Favorites => _allRowsView.Where(r => r.IsFavorite),
            VaultFilterMode.Recents => _all
                .Where(i => i.LastUsedAt is not null)
                .OrderByDescending(i => i.LastUsedAt)
                .Take(10)
                .Select(i => _rowById[i.Id]),
            VaultFilterMode.Conflicts => _allRowsView.Where(r => r.HasMismatch),
            VaultFilterMode.Relevant => FilterRelevantRows(),
            _ => _allRowsView
        };

        if (!string.IsNullOrWhiteSpace(q))
            rows = rows.Where(r => MatchesSearch(q, r));

        // Recents keep last-used order; Relevant with captured host is already score-sorted.
        List<VaultRow> nextList;
        if (_filter == VaultFilterMode.Recents ||
            (_filter == VaultFilterMode.Relevant && !string.IsNullOrWhiteSpace(_capturedHost)))
            nextList = rows.ToList();
        else
            nextList = SortRows(rows).ToList();

        BindRows(nextList);
    }

    private async Task EnsurePasswordIndexAsync()
    {
        if (_passwordIndex is not null)
            return;
        try
        {
            _passwordIndex = await _vault.GetPasswordIndexAsync();
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Failed to build vault password search index.", nameof(VaultView));
            _passwordIndex = new Dictionary<Guid, string>();
        }
    }

    private IEnumerable<VaultRow> SortRows(IEnumerable<VaultRow> rows) => _sortMode switch
    {
        VaultSortMode.TitleDesc => rows
            .OrderByDescending(r => r.SortTitle, StringComparer.OrdinalIgnoreCase),
        VaultSortMode.UsernameAsc => rows
            .OrderBy(r => r.Username, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.SortTitle, StringComparer.OrdinalIgnoreCase),
        VaultSortMode.UsernameDesc => rows
            .OrderByDescending(r => r.Username, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.SortTitle, StringComparer.OrdinalIgnoreCase),
        _ => rows.OrderBy(r => r.SortTitle, StringComparer.OrdinalIgnoreCase)
    };

    private void BindRows(List<VaultRow> nextList)
    {
        if (_groupMode == VaultGroupMode.None)
        {
            // Conflicts uses a different DataTemplate — always replace ItemsSource so the selector re-runs.
            if (_filter != VaultFilterMode.Conflicts &&
                SameRowIds(_rows, nextList) &&
                ReferenceEquals(ItemsList.ItemsSource, _rows))
            {
                for (var i = 0; i < _rows.Count; i++)
                    _rows[i].ApplyFrom(nextList[i]);
                return;
            }

            _rows = new ObservableCollection<VaultRow>(nextList);
            ItemsList.ItemsSource = null;
            ItemsList.ItemsSource = _rows;
            return;
        }

        foreach (var row in nextList)
            row.GroupKey = GroupKeyFor(row);

        // Recents: keep last-used order inside each group; other chips use the sort menu.
        nextList = GroupThenSort(nextList, preserveItemOrder: _filter == VaultFilterMode.Recents);
        _rows = new ObservableCollection<VaultRow>(nextList);

        // Flat headers + rows — CollectionView GroupStyle breaks virtualized ScrollViewer extent.
        ItemsList.ItemsSource = BuildGroupedFlatList(nextList);
    }

    private ObservableCollection<object> BuildGroupedFlatList(IReadOnlyList<VaultRow> rows)
    {
        var flat = new ObservableCollection<object>();
        foreach (var group in rows.GroupBy(r => r.GroupKey, StringComparer.OrdinalIgnoreCase))
        {
            var key = group.Key;
            var items = group.ToList();
            var expanded = _expandedGroups.Contains(key);
            flat.Add(new VaultGroupHeader
            {
                Title = key,
                Count = items.Count,
                IsExpanded = expanded
            });
            if (!expanded)
                continue;
            foreach (var row in items)
                flat.Add(row);
        }
        return flat;
    }

    private List<VaultRow> GroupThenSort(List<VaultRow> rows, bool preserveItemOrder)
    {
        IEnumerable<IGrouping<string, VaultRow>> groups = rows
            .GroupBy(r => r.GroupKey)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        var ordered = new List<VaultRow>(rows.Count);
        foreach (var group in groups)
            ordered.AddRange(preserveItemOrder ? group : SortRows(group));
        return ordered;
    }

    private string GroupKeyFor(VaultRow row) => _groupMode switch
    {
        VaultGroupMode.EntryType => FormatEntryType(row.ItemType),
        VaultGroupMode.UrlDomain => string.IsNullOrWhiteSpace(row.Host) ? "(No domain)" : NormalizeDomain(row.Host),
        VaultGroupMode.Username => string.IsNullOrWhiteSpace(row.Username) ? "(No username)" : row.Username,
        _ => ""
    };

    private static string FormatEntryType(VaultItemType type) => type switch
    {
        VaultItemType.SecureNote => "Secure note",
        VaultItemType.CreditCard => "Credit card",
        VaultItemType.BankAccount => "Bank account",
        VaultItemType.EmailAccount => "Email account",
        VaultItemType.InstantMessenger => "Instant messenger",
        VaultItemType.SshKey => "SSH key",
        VaultItemType.DriverLicense => "Driver license",
        VaultItemType.HealthInsurance => "Health insurance",
        VaultItemType.Other => "Other",
        _ => type.ToString()
    };

    private static string NormalizeDomain(string host)
    {
        host = host.Trim().Trim('.').ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal))
            host = host[4..];
        return host;
    }

    private IEnumerable<VaultRow> FilterRelevantRows()
    {
        // Seed from current foreground if we haven't received an event yet.
        if (_autoMatchTerms.Count == 0 && ForegroundWindowMonitor.Instance.Current is { } current)
        {
            _autoFilter = current.UrlHint;
            _capturedHost = current.CapturedHost;
            _foregroundHwnd = current.Hwnd;
            _autoMatchTerms = current.MatchTerms;
            UpdateFilterHint();
        }

        // Domain path: scored host match only (no title-token false positives).
        if (!string.IsNullOrWhiteSpace(_capturedHost))
        {
            var equivalence = _domainEquivalence ?? DomainEquivalenceSnapshot.Empty;
            foreach (var row in _allRowsView)
                row.DomainScore = VaultDomain.ScoreItemMatch(
                    _capturedHost, row.Url, row.EquivalentDomains, equivalence);

            return _allRowsView
                .Where(r => r.DomainScore > 0)
                .OrderByDescending(r => r.DomainScore)
                .ThenBy(r => r.SortTitle, StringComparer.OrdinalIgnoreCase);
        }

        if (_autoMatchTerms.Count == 0 && string.IsNullOrWhiteSpace(_autoFilter))
            return _allRowsView;

        var raw = _autoMatchTerms.Count > 0
            ? _autoMatchTerms
            : string.IsNullOrWhiteSpace(_autoFilter) ? Array.Empty<string>() : new[] { _autoFilter! };
        var terms = ForegroundWindowMonitor.StrongMatchTerms(raw);
        if (terms.Count == 0)
            return Array.Empty<VaultRow>();

        foreach (var row in _allRowsView)
            row.DomainScore = 0;

        return _allRowsView.Where(r => MatchesRelevant(terms, r));
    }

    private static bool SameRowIds(IReadOnlyList<VaultRow> current, IReadOnlyList<VaultRow> next)
    {
        if (current.Count != next.Count)
            return false;
        for (var i = 0; i < current.Count; i++)
        {
            if (current[i].Id != next[i].Id)
                return false;
        }
        return true;
    }

    private bool MatchesSearch(string needle, VaultRow r)
    {
        if (r.Subtitle.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
            r.Identity.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
            r.Username.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
            r.SortTitle.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
            r.MatchBlob.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
            FormatEntryType(r.ItemType).Contains(needle, StringComparison.OrdinalIgnoreCase))
            return true;

        return _passwordIndex is not null
               && _passwordIndex.TryGetValue(r.Id, out var password)
               && !string.IsNullOrEmpty(password)
               && password.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Title-heuristic fallback when no captured host: distinctive whole-word / domain-label hits only.
    /// </summary>
    private static bool MatchesRelevant(IReadOnlyList<string> terms, VaultRow r)
    {
        var vaultTokens = r.MatchTokens;
        foreach (var term in terms)
        {
            if (ForegroundWindowMonitor.IsWeakToken(term) && !term.Contains('.'))
                continue;

            if (VaultDomain.ScoreItemMatch(term, r.Url, r.EquivalentDomains) > 0)
                return true;

            if (HasWholeWord(r.SortTitle, term) ||
                HasWholeWord(r.Subtitle, term) ||
                HasWholeWord(r.Identity, term) ||
                HasWholeWord(r.Host, term))
                return true;

            if (term.Length >= 5 &&
                !term.Contains(' ') &&
                (r.Host.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                 r.SortTitle.Contains(term, StringComparison.OrdinalIgnoreCase)))
                return true;

            foreach (var vaultToken in vaultTokens)
            {
                if (vaultToken.Equals(term, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    private static bool HasWholeWord(string? text, string term)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(term))
            return false;
        foreach (var token in ForegroundWindowMonitor.Tokenize(text))
        {
            if (token.Equals(term, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        if (term.Contains(' ') && text.Contains(term, StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private async Task<VaultItem?> LoadItemAsync(VaultRow? row)
    {
        if (row is null)
            return null;
        return await _vault.GetByIdAsync(row.Id);
    }

    private void VaultSlide_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Border border)
            return;
        border.RenderTransform = new TranslateTransform();
        border.Tag = SlideActionWidth;
        SetSlideActionsVisible(border, visible: false);
    }

    private void Slide_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border front)
            return;

        var transform = EnsureMutableTransform(front);
        if (!ReferenceEquals(_slideFront, front))
            CloseOpenSlide();

        _slideFront = front;
        _slideTransform = transform;
        _slideActionWidth = front.Tag is double w ? w : SlideActionWidth;
        _slideStart = e.GetPosition(this);
        _slideOriginX = transform.X;
        _slideMoved = false;
        front.CaptureMouse();
        e.Handled = true;
    }

    private void Slide_MouseMove(object sender, MouseEventArgs e)
    {
        if (_slideFront is null || _slideTransform is null || e.LeftButton != MouseButtonState.Pressed)
            return;
        if (!ReferenceEquals(sender, _slideFront))
            return;

        var current = e.GetPosition(this);
        var dx = current.X - _slideStart.X;
        if (Math.Abs(dx) > 4)
        {
            _slideMoved = true;
            SetSlideActionsVisible(_slideFront, visible: true);
        }

        _slideTransform.X = Math.Clamp(_slideOriginX + dx, -_slideActionWidth, 0);
        e.Handled = true;
    }

    private void Slide_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_slideFront is null || _slideTransform is null)
            return;
        if (!ReferenceEquals(sender, _slideFront))
            return;

        var front = _slideFront;
        var transform = _slideTransform;
        var moved = _slideMoved;
        var width = _slideActionWidth;

        front.ReleaseMouseCapture();
        e.Handled = true;

        if (!moved)
        {
            CloseOpenSlide();
            if (front.DataContext is VaultRow row)
                HandleRowClick(row);
            return;
        }

        var open = transform.X <= -width * SlideOpenThreshold;
        transform.X = open ? -width : 0;
        if (!open)
        {
            SetSlideActionsVisible(front, visible: false);
            _slideFront = null;
            _slideTransform = null;
        }
    }

    private void Slide_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_slideFront is null || _slideTransform is null)
            return;
        if (!ReferenceEquals(sender, _slideFront))
            return;

        var open = _slideTransform.X <= -_slideActionWidth * SlideOpenThreshold;
        _slideTransform.X = open ? -_slideActionWidth : 0;
        if (!open)
        {
            SetSlideActionsVisible(_slideFront, visible: false);
            _slideFront = null;
            _slideTransform = null;
        }
    }

    private void CloseOpenSlide()
    {
        if (_slideFront is not null)
            SetSlideActionsVisible(_slideFront, visible: false);
        if (_slideTransform is { IsFrozen: false })
            _slideTransform.X = 0;
        _slideFront = null;
        _slideTransform = null;
        _slideMoved = false;
    }

    private static TranslateTransform EnsureMutableTransform(Border front)
    {
        if (front.RenderTransform is TranslateTransform { IsFrozen: false } existing)
            return existing;
        var transform = new TranslateTransform();
        front.RenderTransform = transform;
        return transform;
    }

    private static void SetSlideActionsVisible(Border front, bool visible)
    {
        if (FindSlideActions(front) is not { } actions)
            return;
        actions.Opacity = visible ? 1 : 0;
        actions.IsHitTestVisible = visible;
    }

    private static FrameworkElement? FindSlideActions(Border front)
    {
        if (VisualTreeHelper.GetParent(front) is not Panel host)
            return null;

        FrameworkElement? fallback = null;
        foreach (UIElement child in host.Children)
        {
            if (ReferenceEquals(child, front) || child is not FrameworkElement fe)
                continue;
            if (fe.Tag is "SlideActions")
                return fe;
            fallback ??= fe;
        }

        return fallback;
    }

    private async void SlideFill_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: VaultRow row })
            return;
        CloseOpenSlide();
        await FillRowAsync(row, forceLogin: false);
        e.Handled = true;
    }

    private async void SlideLogin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: VaultRow row })
            return;
        CloseOpenSlide();
        await FillRowAsync(row, forceLogin: true);
        e.Handled = true;
    }

    private async void SlideEdit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: VaultRow row })
            return;
        CloseOpenSlide();
        // Load metadata/notes without decrypting password — Reveal in the editor fetches it.
        var item = await _vault.GetByIdAsync(row.Id, revealSecrets: true, revealPassword: false);
        if (item is null) return;
        await EditItemAsync(item);
        e.Handled = true;
    }

    private async Task FillRowAsync(VaultRow row, bool forceLogin, IntPtr? targetHwnd = null)
    {
        if (_autoFillInFlight)
            return;

        // Browser pages must use the extension — UIA only fills email and breaks two-step.
        var fg = ForegroundWindowMonitor.Instance.Current;
        var process = fg?.ProcessName ?? "";
        if (IsBrowserProcess(process))
        {
            ShowStatusToast("Use browser extension for web logins");
            AppDialog.Alert(
                Window.GetWindow(this),
                "Web autofill is handled by the Authenticator browser extension (Chrome / Edge / Firefox), not desktop typing.\n\n" +
                "Settings → Browser autofill → Open extension folder, then Load unpacked on chrome://extensions and pin the icon.",
                "Browser extension",
                AppDialogKind.Info);
            return;
        }

        _autoFillInFlight = true;
        try
        {
            var item = await _vault.GetByIdAsync(row.Id, revealSecrets: true, revealPassword: true);
            if (item is null)
            {
                ShowStatusToast("Item not found");
                return;
            }

            var hwnd = targetHwnd ?? _foregroundHwnd;
            if (hwnd == IntPtr.Zero && fg is not null)
                hwnd = fg.Hwnd;

            var autoLogin = forceLogin || item.AllowAutoLogin;
            await AutoFillService.Instance.FillLoginAsync(
                item,
                hwnd,
                autoLogin: autoLogin,
                includeTotp: autoLogin && (!string.IsNullOrWhiteSpace(item.TotpSecret) || item.LinkedAccountId is not null));

            if (_all.FirstOrDefault(i => i.Id == row.Id) is { } cached)
                cached.LastUsedAt = DateTime.UtcNow;

            ShowStatusToast(autoLogin ? "Auto-login sent" : "Credentials filled");
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, $"Autofill failed for vault item {row.Id}.", nameof(VaultView));
            AppDialog.Alert(Window.GetWindow(this), ex.Message, "Auto-fill", AppDialogKind.Warning);
        }
        finally
        {
            _autoFillInFlight = false;
        }
    }

    private static bool IsBrowserProcess(string processName) =>
        processName.Equals("chrome", StringComparison.OrdinalIgnoreCase) ||
        processName.Equals("msedge", StringComparison.OrdinalIgnoreCase) ||
        processName.Equals("msedgewebview2", StringComparison.OrdinalIgnoreCase) ||
        processName.Equals("firefox", StringComparison.OrdinalIgnoreCase) ||
        processName.Equals("brave", StringComparison.OrdinalIgnoreCase) ||
        processName.Equals("opera", StringComparison.OrdinalIgnoreCase) ||
        processName.Equals("vivaldi", StringComparison.OrdinalIgnoreCase);

    private async void SlideDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: VaultRow row })
            return;
        CloseOpenSlide();
        if (!AppDialog.Confirm(
                Window.GetWindow(this),
                $"Delete “{row.SortTitle}”?",
                "Delete",
                confirmText: "Delete",
                kind: AppDialogKind.Warning))
            return;
        await _vault.DeleteAsync(row.Id);
        await ReloadAsync();
        e.Handled = true;
    }

    private async void Favorite_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: VaultRow row })
            return;

        e.Handled = true;
        CloseOpenSlide();

        var next = !row.IsFavorite;
        try
        {
            await _vault.SetFavoriteAsync(row.Id, next);
            row.IsFavorite = next;
            var cached = _all.FirstOrDefault(i => i.Id == row.Id);
            if (cached is not null)
                cached.IsFavorite = next;

            // Re-apply so Favorites chip drops unfavorited rows (and picks up new favorites).
            ApplyFilter();
            ShowStatusToast(next ? "Added to favorites" : "Removed from favorites");
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, $"Failed to update favorite for vault item {row.Id}.", nameof(VaultView));
            AppDialog.Alert(Window.GetWindow(this), ex.Message, "Favorite", AppDialogKind.Error);
        }
    }

    private void CopyPassword_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: VaultRow row })
            return;
        CopyPassword(row);
        e.Handled = true;
    }

    /// <summary>
    /// Single click copies username; rapid second click on the same row opens URL.
    /// Uses system double-click time — ClickCount is unreliable after CaptureMouse.
    /// </summary>
    private void HandleRowClick(VaultRow row)
    {
        var now = DateTime.UtcNow;
        var isDouble = _lastRowClickId == row.Id &&
                       (now - _lastRowClickUtc).TotalMilliseconds <= GetDoubleClickTime();
        _lastRowClickId = row.Id;
        _lastRowClickUtc = now;

        if (isDouble)
        {
            _lastRowClickId = null;
            LaunchItemUrl(row);
            return;
        }

        CopyUsername(row);
    }

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    private async void CopyUsername(VaultRow row)
    {
        // Username is on the list row — no secret decrypt.
        var cached = _all.FirstOrDefault(i => i.Id == row.Id);
        var username = cached?.Username;
        if (username is null)
        {
            var item = await _vault.GetByIdAsync(row.Id, revealSecrets: true, revealPassword: false);
            username = item?.Username;
        }
        ClipboardHelper.Copy(username);
        ShowStatusToast("Username copied");
        await MarkUsedAsync(row.Id);
    }

    private void LaunchItemUrl(VaultRow row)
    {
        var url = row.Url;
        if (string.IsNullOrWhiteSpace(url))
            url = _all.FirstOrDefault(i => i.Id == row.Id)?.Url;
        if (string.IsNullOrWhiteSpace(url) || !LooksLikeUrl(url))
        {
            ShowStatusToast("No URL");
            return;
        }

        try
        {
            var launch = url.Contains("://", StringComparison.Ordinal) ? url.Trim() : "https://" + url.Trim();
            Process.Start(new ProcessStartInfo { FileName = launch, UseShellExecute = true });
            ShowStatusToast("Opened site");
            _ = MarkUsedAsync(row.Id);
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, $"Failed to open URL for vault item {row.Id}.", nameof(VaultView));
            AppDialog.Alert(Window.GetWindow(this), ex.Message, "Open site", AppDialogKind.Warning);
        }
    }

    private static bool LooksLikeUrl(string? value)
    {
        var raw = (value ?? "").Trim();
        if (string.IsNullOrEmpty(raw))
            return false;
        if (raw.Contains("://", StringComparison.OrdinalIgnoreCase))
            return Uri.TryCreate(raw, UriKind.Absolute, out var abs) &&
                   (abs.Scheme == Uri.UriSchemeHttp || abs.Scheme == Uri.UriSchemeHttps);
        return raw.Contains('.', StringComparison.Ordinal) || raw.Contains('/', StringComparison.Ordinal);
    }

    private async void CopyPassword(VaultRow row)
    {
        var password = await _vault.GetPasswordAsync(row.Id);
        if (password is null) return;
        ClipboardHelper.Copy(password);
        ShowStatusToast("Password copied");
        await MarkUsedAsync(row.Id);
    }

    private void ShowStatusToast(string message)
    {
        ToastText.Text = message;
        _toastTimer.Stop();
        StatusToast.Visibility = Visibility.Visible;
        _toastTimer.Start();
    }

    private void HideStatusToast()
    {
        _toastTimer.Stop();
        StatusToast.Visibility = Visibility.Collapsed;
    }

    private async Task MarkUsedAsync(Guid id)
    {
        try
        {
            await _vault.TouchLastUsedAsync(id);
            var cached = _all.FirstOrDefault(i => i.Id == id);
            if (cached is not null)
                cached.LastUsedAt = DateTime.UtcNow;
            if (_filter == VaultFilterMode.Recents)
                ApplyFilter();
        }
        catch
        {
            // Recents bookkeeping must not interrupt copy/fill.
        }
    }

    private async Task EditItemAsync(VaultItem item)
    {
        var win = new VaultItemEditWindow(item) { Owner = Window.GetWindow(this) };
        if (win.ShowDialog() == true)
            await ReloadAsync();
    }

    private void OpenReconcileHistory()
    {
        var win = new ReconcileHistoryWindow { Owner = Window.GetWindow(this) };
        if (win.ShowDialog() == true)
            _ = ReloadAfterReconcileAsync();
    }

    private static MenuItem MakeMenu(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        // Defer until the ContextMenu has closed — ShowDialog from a menu Click is a no-op.
        item.Click += (_, _) =>
        {
            var dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, action);
        };
        return item;
    }

    public async Task AddNewAsync()
    {
        var win = new VaultItemEditWindow(new VaultItem { ItemType = VaultItemType.Login }, isNew: true)
        {
            Owner = Window.GetWindow(this)
        };
        if (win.ShowDialog() == true)
            await ReloadAsync();
    }

    public async Task ImportFromProviderAsync()
    {
        var win = new ProviderConnectWindow(ProviderDialogMode.Import) { Owner = Window.GetWindow(this) };
        if (win.ShowDialog() != true || win.ResultProvider is null)
            return;

        AppDialog.Alert(
            Window.GetWindow(this),
            $"Imported {win.ImportedCount ?? 0} entries from {win.ResultProvider.DisplayName}.",
            "Import");
        await ReloadAsync();
    }

    public async Task ConnectToProviderAsync()
    {
        var win = new ProviderConnectWindow(ProviderDialogMode.Connect) { Owner = Window.GetWindow(this) };
        if (win.ShowDialog() != true || win.ResultProvider is null)
            return;

        var provider = win.ResultProvider;
        var owner = Window.GetWindow(this);
        AppDialog.Alert(owner, $"Connected to {provider.DisplayName}.", "Connect");

        var syncNow = AppDialog.Confirm(
            owner,
            $"Sync entries from {provider.DisplayName} into Shared now?\n\nThis can take a while for large vaults.",
            "Sync now?",
            confirmText: "Sync now",
            cancelText: "Not now");

        if (syncNow)
            await RunProviderSyncAsync(provider.ProviderId, bulkFirstSync: true);
        else
            await ReloadAsync();
    }

    public async Task SyncConnectedProviderAsync(string providerId) =>
        await RunProviderSyncAsync(providerId, bulkFirstSync: false);

    private async Task RunProviderSyncAsync(string providerId, bool bulkFirstSync)
    {
        SetBusy(true, "Syncing…");
        try
        {
            int imported;
            if (bulkFirstSync)
            {
                if (!ProviderSessionService.Instance.TryGetLive(providerId, out var live) || live is null)
                    throw new InvalidOperationException($"{providerId} is not connected.");

                // Full import on first sync after Connect; off UI thread for spinner.
                imported = await Task.Run(
                    async () => await new VaultImportService().BulkImportAsync(live).ConfigureAwait(false));
            }
            else
            {
                var result = await Task.Run(
                    async () => await ProviderSessionService.Instance
                        .SyncProviderImportAsync(providerId)
                        .ConfigureAwait(false));
                imported = result.Imported;
            }

            await ReloadAsync();
            ShowStatusToast(
                imported > 0
                    ? $"Imported {imported} from {providerId}"
                    : $"Synced {providerId}");
            // Clean slate + one provider: vault matches that store — skip auto conflict scan.
            // Diff/reconcile still available if an edge-case conflict exists. Scan when 2+ providers linked.
            MaybeScheduleConflictScanAfterSync(providerId);
        }
        catch (Exception ex)
        {
            AppDialog.Alert(Window.GetWindow(this), FormatSyncError(ex), "Sync failed", AppDialogKind.Warning);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void MaybeScheduleConflictScanAfterSync(string? providerId = null)
    {
        var synced = GetSyncedProviderIds();
        if (synced.Count <= 1)
            return;
        ScheduleConflictScan(providerId, announce: true);
    }

    private void SetBusy(bool busy, string? header = null)
    {
        if (header is not null)
            BusyIndicator.Header = header;
        BusyOverlay.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        BusyIndicator.IsBusy = busy;
    }

    private static string FormatSyncError(Exception ex)
    {
        var cur = ex;
        while (cur.InnerException is not null)
            cur = cur.InnerException;
        return string.IsNullOrWhiteSpace(cur.Message) ? ex.Message : cur.Message;
    }

    public async Task SyncAllConnectedAsync()
    {
        SetBusy(true, "Syncing…");
        try
        {
            var result = await Task.Run(
                async () => await ProviderSessionService.Instance.SyncAllImportAsync().ConfigureAwait(false));
            await ReloadAsync();
            ShowStatusToast(
                result.Imported > 0
                    ? $"Imported {result.Imported} new from connected providers"
                    : "Synced connected providers");
            MaybeScheduleConflictScanAfterSync();
        }
        catch (Exception ex)
        {
            AppDialog.Alert(Window.GetWindow(this), FormatSyncError(ex), "Sync failed", AppDialogKind.Warning);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>Import missing + conflict scan for every saved provider, then jump to Conflicts if any.</summary>
    public async Task ReconcileAllProvidersAsync()
    {
        if (ProviderSessionService.Instance.SavedConnections.Count == 0)
        {
            AppDialog.Alert(Window.GetWindow(this), "No providers are connected.", "Diff all");
            return;
        }

        SetBusy(true, "Reconciling…");
        try
        {
            // Reconnect on UI thread first (LastPass MFA needs a dialog if trust expired).
            foreach (var conn in ProviderSessionService.Instance.SavedConnections.ToList())
                await ProviderSessionService.Instance.EnsureConnectedAsync(conn.ProviderId);

            var result = await Task.Run(
                async () => await ProviderSessionService.Instance.SyncAllConnectedAsync().ConfigureAwait(false));

            _skipConflictScanOnReload = true;
            try
            {
                await ReloadAsync();
            }
            finally
            {
                _skipConflictScanOnReload = false;
            }

            var summary =
                $"Imported {result.Imported} new · {result.Unchanged} matched · {result.Conflicts} conflict(s).";
            ShowStatusToast(summary);

            if (result.Conflicts > 0)
            {
                AppDialog.Alert(Window.GetWindow(this), summary + "\n\nOpening the Conflicts filter.", "Diff all");
                await OpenFirstConflictForProviderAsync(null);
            }
            else
            {
                AppDialog.Alert(Window.GetWindow(this), summary, "Diff all");
            }
        }
        catch (Exception ex)
        {
            AppDialog.Alert(Window.GetWindow(this), FormatSyncError(ex), "Diff all failed", AppDialogKind.Warning);
        }
        finally
        {
            SetBusy(false);
        }
    }

    public async Task ReconcileProviderAsync(string providerId)
    {
        SetBusy(true, "Reconciling…");
        try
        {
            await ProviderSessionService.Instance.EnsureConnectedAsync(providerId);
            var result = await Task.Run(
                async () => await ProviderSessionService.Instance
                    .SyncProviderAsync(providerId)
                    .ConfigureAwait(false));

            _skipConflictScanOnReload = true;
            try
            {
                await ReloadAsync();
            }
            finally
            {
                _skipConflictScanOnReload = false;
            }

            ShowStatusToast(
                result.Conflicts > 0
                    ? $"{providerId}: {result.Conflicts} conflict(s)"
                    : $"{providerId}: reconciled ({result.Imported} new)");

            if (result.Conflicts > 0)
                await OpenFirstConflictForProviderAsync(providerId);
        }
        catch (Exception ex)
        {
            AppDialog.Alert(Window.GetWindow(this), FormatSyncError(ex), "Diff failed", AppDialogKind.Warning);
        }
        finally
        {
            SetBusy(false);
        }
    }

    public void DisconnectProvider(string providerId)
    {
        ProviderSessionService.Instance.Disconnect(providerId);
        AppDialog.Alert(Window.GetWindow(this), $"Disconnected {providerId}.", "Disconnect");
    }

    public void ShowProvidersMenu(FrameworkElement placementTarget)
    {
        var menu = new ContextMenu { PlacementTarget = placementTarget, Placement = PlacementMode.Top };
        menu.Items.Add(MakeMenu("Connect to…", () => _ = ConnectToProviderAsync()));
        menu.Items.Add(MakeMenu("Import from…", () => _ = ImportFromProviderAsync()));
        menu.Items.Add(MakeMenu("Reconcile history…", () => OpenReconcileHistory()));

        var conflictCount = _allRowsView.Count(r => r.HasMismatch);
        if (conflictCount > 0)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(MakeMenu($"Review conflicts ({conflictCount})…", () =>
                _ = OpenFirstConflictForProviderAsync(null)));
            menu.Items.Add(MakeMenu($"Clear conflict links ({conflictCount})…", () =>
                _ = ClearConflictLinksAsync()));
        }

        var saved = ProviderSessionService.Instance.SavedConnections.ToList();
        var liveIds = new HashSet<string>(
            ProviderSessionService.Instance.LiveConnected.Select(p => p.ProviderId),
            StringComparer.OrdinalIgnoreCase);
        // Synced = vault already has ExternalLink rows from this provider (empty after debug purge).
        var syncedIds = GetSyncedProviderIds();

        if (saved.Count > 0)
        {
            menu.Items.Add(new Separator());
            var liveSynced = liveIds.Where(syncedIds.Contains).ToList();
            if (liveIds.Count > 0)
                menu.Items.Add(MakeMenu("Sync all connected", () => _ = SyncAllConnectedAsync()));
            // Diff/reconcile needs prior sync data in the vault — not after a purge.
            if (liveSynced.Count > 0)
                menu.Items.Add(MakeMenu("Diff / reconcile all…", () => _ = ReconcileAllProvidersAsync()));

            foreach (var conn in saved)
            {
                var providerId = conn.ProviderId;
                var name = string.IsNullOrWhiteSpace(conn.DisplayName) ? providerId : conn.DisplayName;
                var live = liveIds.Contains(providerId);
                var synced = syncedIds.Contains(providerId);
                var sub = new MenuItem { Header = ProviderMenuHeader(name, live, synced) };

                if (live)
                {
                    // First sync after connect/purge — or incremental sync when already linked.
                    sub.Items.Add(MakeMenu(
                        synced ? "Sync now" : "Sync now (import)…",
                        () => _ = SyncConnectedProviderAsync(providerId)));

                    if (synced)
                    {
                        sub.Items.Add(MakeMenu("Diff / reconcile…", () => _ = ReconcileProviderAsync(providerId)));
                        var providerConflicts = _allRowsView.Count(r =>
                            r.HasMismatch && r.HasMismatchFor(providerId));
                        if (providerConflicts > 0)
                        {
                            sub.Items.Add(MakeMenu(
                                $"Review conflicts ({providerConflicts})…",
                                () => _ = OpenFirstConflictForProviderAsync(providerId)));
                        }
                    }
                }
                else
                {
                    sub.Items.Add(MakeMenu("Reconnect…", () => _ = ReconnectProviderAsync(providerId)));
                }

                sub.Items.Add(MakeMenu("Disconnect", () => DisconnectProvider(providerId)));
                menu.Items.Add(sub);
            }
        }

        menu.IsOpen = true;
    }

    private HashSet<string> GetSyncedProviderIds()
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in _all)
        {
            if (item.ExternalLinks is null)
                continue;
            foreach (var link in item.ExternalLinks)
            {
                if (string.IsNullOrWhiteSpace(link.Provider) ||
                    string.Equals(link.Provider, "Shared", StringComparison.OrdinalIgnoreCase))
                    continue;
                ids.Add(link.Provider);
            }
        }

        return ids;
    }

    private static string ProviderMenuHeader(string name, bool live, bool synced)
    {
        if (!live)
            return synced ? $"{name} (not connected)" : $"{name} (not connected · not synced)";
        return synced ? name : $"{name} (not synced)";
    }

    private async Task ReconnectProviderAsync(string providerId)
    {
        SetBusy(true, $"Connecting {providerId}…");
        try
        {
            await ProviderSessionService.Instance.EnsureConnectedAsync(providerId);
            AppDialog.Alert(Window.GetWindow(this), $"{providerId} connected.", "Providers");
        }
        catch (Exception ex)
        {
            AppDialog.Alert(Window.GetWindow(this), FormatSyncError(ex), "Reconnect failed", AppDialogKind.Warning);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ClearConflictLinksAsync()
    {
        var count = _allRowsView.Count(r => r.HasMismatch);
        if (count == 0)
            return;

        if (!AppDialog.Confirm(
                Window.GetWindow(this),
                $"Remove {count} conflicted provider link(s)?\n\n" +
                "Vault items stay. Conflicts clear immediately. " +
                "Next Sync can re-import those provider entries with current match rules — no full purge needed.",
                "Clear conflict links",
                confirmText: "Clear links",
                kind: AppDialogKind.Warning))
            return;

        SetBusy(true, "Clearing conflicts…");
        try
        {
            var removed = await new VaultImportService().ClearAllConflictLinksAsync();
            await ReloadAsync();
            ShowStatusToast(removed > 0 ? $"Cleared {removed} conflict link(s)" : "No conflict links found");
        }
        catch (Exception ex)
        {
            AppDialog.Alert(Window.GetWindow(this), ex.Message, "Clear conflicts", AppDialogKind.Warning);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void Badge_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: VaultRow row } || !row.HasMismatch)
            return;
        e.Handled = true;
        _ = OpenDiffAsync(row);
    }

    private void ConflictRow_Click(object sender, MouseButtonEventArgs e)
    {
        // Ignore clicks on the Sync button (it has its own handler).
        if (e.OriginalSource is DependencyObject source &&
            FindAncestor<Button>(source) is not null)
            return;

        if (sender is not FrameworkElement { Tag: VaultRow row })
            return;
        e.Handled = true;
        _ = OpenDiffAsync(row);
    }

    private void ConflictSync_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: VaultRow row })
            return;
        e.Handled = true;
        _ = OpenDiffAsync(row);
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
                return match;
            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private async Task OpenFirstConflictForProviderAsync(string? providerId)
    {
        SelectFilter(VaultFilterMode.Conflicts, reload: false);
        ApplyFilter();

        VaultRow? row = null;
        if (!string.IsNullOrWhiteSpace(providerId))
        {
            row = _allRowsView.FirstOrDefault(r => r.HasMismatch && r.HasMismatchFor(providerId));
            if (row is null)
            {
                AppDialog.Alert(
                    Window.GetWindow(this),
                    $"No conflicts for {providerId}. Run Sync now to compare Shared with the connected vault.",
                    "Conflicts");
                return;
            }
        }
        else
        {
            row = _allRowsView.FirstOrDefault(r => r.HasMismatch);
            if (row is null)
            {
                AppDialog.Alert(
                    Window.GetWindow(this),
                    "No conflicts right now. Sync a connected provider to detect mismatches.",
                    "Conflicts");
                return;
            }
        }

        await OpenDiffAsync(row, preferredProviderId: providerId);
    }

    private async Task OpenDiffAsync(VaultRow row, string? preferredProviderId = null)
    {
        try
        {
            var item = await _vault.GetByIdAsync(row.Id);
            if (item is null)
                return;

            var links = (item.ExternalLinks ?? Array.Empty<ExternalLink>())
                .Where(l => !string.IsNullOrWhiteSpace(l.Provider) &&
                            !string.Equals(l.Provider, "Shared", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(l => l.IsMismatch)
                .ThenByDescending(l => string.Equals(l.Provider, preferredProviderId, StringComparison.OrdinalIgnoreCase))
                .ThenBy(l => l.Provider, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (links.Count == 0)
            {
                AppDialog.Alert(Window.GetWindow(this), "This item has no linked provider.", "Diff");
                return;
            }

            var sources = new List<(string ProviderId, string DisplayName, ProviderEntry? Remote, bool IsMismatch)>();
            foreach (var link in links)
            {
                var remote = await ProviderSessionService.Instance.FindRemoteEntryAsync(
                    link.Provider,
                    link.ExternalId,
                    item.Title,
                    item.Username);
                var display = VaultProviderCatalog.CreateAll()
                    .FirstOrDefault(p => string.Equals(p.ProviderId, link.Provider, StringComparison.OrdinalIgnoreCase))
                    ?.DisplayName ?? link.Provider;
                sources.Add((link.Provider, display, remote, link.IsMismatch));
            }

            var win = new DiffMatchWindow(item, sources) { Owner = Window.GetWindow(this) };
            if (win.ShowDialog() == true)
                await ReloadAfterReconcileAsync();
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, $"Failed to open Diff for {row.Id}.", nameof(VaultView));
            AppDialog.Alert(Window.GetWindow(this), ex.Message, "Diff", AppDialogKind.Warning);
        }
    }

    /// <summary>
    /// After Diff Apply / history undo: refresh the list without immediately re-scanning
    /// providers (that would re-flag the just-cleared conflict when vault-only was chosen).
    /// </summary>
    private async Task ReloadAfterReconcileAsync()
    {
        _skipConflictScanOnReload = true;
        try
        {
            await ReloadAsync();
            var remaining = _allRowsView.Count(r => r.HasMismatch);
            ShowStatusToast(remaining > 0
                ? $"Reconcile applied · {remaining} conflict(s) left"
                : "Reconcile applied");
        }
        finally
        {
            _skipConflictScanOnReload = false;
        }
    }

    public sealed class VaultRow : INotifyPropertyChanged
    {
        private bool _isFavorite;
        private VaultItemType _itemType;
        private string _subtitle = "";
        private string _identity = "";
        private string _username = "";
        private string _sortTitle = "";
        private string _host = "";
        private string? _url;
        private string _initial = "?";
        private Brush _iconBrush = Brushes.Gray;
        private bool _hasMismatch;
        private string _badgeGlyph = "\uE77B";
        private Brush _badgeBrush = AccountEdits.BrushFromHex("#1A73E8");

        public Guid Id { get; set; }
        public VaultItemType ItemType
        {
            get => _itemType;
            set { if (_itemType == value) return; _itemType = value; Notify(nameof(ItemType)); }
        }
        public string Subtitle
        {
            get => _subtitle;
            set { if (_subtitle == value) return; _subtitle = value; Notify(nameof(Subtitle)); }
        }
        public string Identity
        {
            get => _identity;
            set { if (_identity == value) return; _identity = value; Notify(nameof(Identity)); }
        }
        public string Username
        {
            get => _username;
            set { if (_username == value) return; _username = value; Notify(nameof(Username)); }
        }
        public string SortTitle
        {
            get => _sortTitle;
            set { if (_sortTitle == value) return; _sortTitle = value; Notify(nameof(SortTitle)); }
        }
        public string Host
        {
            get => _host;
            set { if (_host == value) return; _host = value; Notify(nameof(Host)); }
        }
        public string? Url
        {
            get => _url;
            set { if (_url == value) return; _url = value; Notify(nameof(Url)); }
        }
        public string? EquivalentDomains { get; set; }
        public string GroupKey { get; set; } = "";
        /// <summary>Lowercased title/username/host/url blob for partial relevance matching.</summary>
        public string MatchBlob { get; set; } = "";
        public string[] MatchTokens { get; set; } = Array.Empty<string>();
        /// <summary>Provider ids with <c>ExternalLink.IsMismatch</c> for this row.</summary>
        public string[] MismatchProviders { get; set; } = Array.Empty<string>();
        public bool AllowAutoFill { get; set; }
        public bool AllowAutoLogin { get; set; }
        /// <summary>Transient domain match score for Relevant filter (0–100).</summary>
        public int DomainScore { get; set; }
        public bool IsFavorite
        {
            get => _isFavorite;
            set
            {
                if (_isFavorite == value)
                    return;
                _isFavorite = value;
                Notify(nameof(IsFavorite));
                Notify(nameof(Star));
                Notify(nameof(StarBrush));
            }
        }

        public bool HasMismatchFor(string providerId) =>
            MismatchProviders.Any(p => string.Equals(p, providerId, StringComparison.OrdinalIgnoreCase));
        public string Initial
        {
            get => _initial;
            set { if (_initial == value) return; _initial = value; Notify(nameof(Initial)); }
        }
        public Brush IconBrush
        {
            get => _iconBrush;
            set { if (ReferenceEquals(_iconBrush, value)) return; _iconBrush = value; Notify(nameof(IconBrush)); }
        }
        public bool HasMismatch
        {
            get => _hasMismatch;
            set
            {
                if (_hasMismatch == value)
                    return;
                _hasMismatch = value;
                Notify(nameof(HasMismatch));
                Notify(nameof(BadgeCursor));
                Notify(nameof(BadgeToolTip));
            }
        }
        public string BadgeGlyph
        {
            get => _badgeGlyph;
            set { if (_badgeGlyph == value) return; _badgeGlyph = value; Notify(nameof(BadgeGlyph)); }
        }
        public Brush BadgeBrush
        {
            get => _badgeBrush;
            set { if (ReferenceEquals(_badgeBrush, value)) return; _badgeBrush = value; Notify(nameof(BadgeBrush)); }
        }
        public Cursor BadgeCursor => HasMismatch ? Cursors.Hand : Cursors.Arrow;
        public string? BadgeToolTip => HasMismatch ? "Conflict — open Diff/match" : null;
        public string Star => IsFavorite ? "★" : "☆";
        public Brush StarBrush => IsFavorite
            ? AccountEdits.BrushFromHex("#F5B301")
            : AccountEdits.BrushFromHex("#9CA3AF");

        public event PropertyChangedEventHandler? PropertyChanged;

        public void ApplyFrom(VaultRow other)
        {
            Id = other.Id;
            ItemType = other.ItemType;
            Subtitle = other.Subtitle;
            Identity = other.Identity;
            Username = other.Username;
            SortTitle = other.SortTitle;
            Host = other.Host;
            Url = other.Url;
            EquivalentDomains = other.EquivalentDomains;
            GroupKey = other.GroupKey;
            MatchBlob = other.MatchBlob;
            MatchTokens = other.MatchTokens;
            MismatchProviders = other.MismatchProviders;
            AllowAutoFill = other.AllowAutoFill;
            AllowAutoLogin = other.AllowAutoLogin;
            DomainScore = other.DomainScore;
            IsFavorite = other.IsFavorite;
            Initial = other.Initial;
            IconBrush = other.IconBrush;
            HasMismatch = other.HasMismatch;
            BadgeGlyph = other.BadgeGlyph;
            BadgeBrush = other.BadgeBrush;
        }

        private void Notify(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public static VaultRow From(VaultItem item)
        {
            var host = VaultDomain.NormalizeDomain(item.Url) ?? "";
            var alts = VaultDomain.ParseDomainList(item.EquivalentDomains);
            var username = item.Username?.Trim() ?? "";
            var subtitle = !string.IsNullOrWhiteSpace(host) ? host : item.Title;
            // Secondary line: username when present, otherwise host (never duplicate the title).
            var identity = !string.IsNullOrWhiteSpace(username) ? username : host;
            var seed = string.IsNullOrWhiteSpace(item.Title) ? identity : item.Title;
            var blob = string.Join(' ',
                item.Title,
                item.Username,
                item.Url,
                host,
                string.Join(' ', alts),
                host.Contains('.') ? host.Split('.')[0] : "").ToLowerInvariant();
            var tokens = ForegroundWindowMonitor.StrongMatchTerms(
                new[] { item.Title, item.Username ?? "", item.Url ?? "", host }.Concat(alts));
            var mismatchProviders = item.ExternalLinks?
                .Where(l => l.IsMismatch && !string.IsNullOrWhiteSpace(l.Provider))
                .Select(l => l.Provider)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray() ?? Array.Empty<string>();
            var hasMismatch = item.HasMismatch || mismatchProviders.Length > 0;
            return new VaultRow
            {
                Id = item.Id,
                ItemType = item.ItemType,
                Subtitle = subtitle,
                Identity = identity,
                Username = username,
                SortTitle = item.Title,
                Host = host,
                Url = item.Url,
                EquivalentDomains = item.EquivalentDomains,
                MatchBlob = blob,
                MatchTokens = tokens.ToArray(),
                MismatchProviders = mismatchProviders,
                IsFavorite = item.IsFavorite,
                AllowAutoFill = item.AllowAutoFill,
                AllowAutoLogin = item.AllowAutoLogin,
                HasMismatch = hasMismatch,
                Initial = string.IsNullOrWhiteSpace(seed) ? "?" : seed.Trim()[..1].ToUpperInvariant(),
                IconBrush = AccountEdits.InitialBrush(seed),
                BadgeGlyph = hasMismatch ? "\uE7BA" : GlyphFor(item.ItemType), // Warning / type
                BadgeBrush = hasMismatch
                    ? AccountEdits.BrushFromHex("#B00020")
                    : AccountEdits.BrushFromHex("#1A73E8")
            };
        }

        private static string GlyphFor(VaultItemType type) => type switch
        {
            VaultItemType.Login => "\uE77B",             // Contact
            VaultItemType.SecureNote => "\uE70B",        // Page
            VaultItemType.CreditCard => "\uE8C7",        // PaymentCard
            VaultItemType.Address => "\uE707",           // Home
            VaultItemType.BankAccount => "\uE825",       // Bank
            VaultItemType.Server => "\uE968",            // Server
            VaultItemType.EmailAccount => "\uE715",      // Mail
            VaultItemType.Database => "\uE81E",          // ScrollMode / data list
            VaultItemType.InstantMessenger => "\uE8BD",  // Message
            VaultItemType.SshKey => "\uE8D7",            // Permissions (key)
            VaultItemType.Wifi => "\uE701",              // Wifi
            VaultItemType.Membership => "\uE8F2",        // People
            VaultItemType.Passport => "\uE734",          // Globe
            VaultItemType.DriverLicense => "\uE8EC",     // ContactCard
            VaultItemType.Insurance => "\uE72E",         // Admin / shield
            VaultItemType.HealthInsurance => "\uE95E",   // Medical
            _ => "\uE8B7"                                // More
        };
    }
}

public sealed class VaultGroupHeader
{
    public string Title { get; init; } = "";
    public int Count { get; init; }
    public bool IsExpanded { get; init; }
    public string Chevron => IsExpanded ? "\uE70D" : "\uE76C"; // ChevronDown / ChevronRight
    public string CountLabel => $"({Count})";
}

public sealed class VaultListTemplateSelector : DataTemplateSelector
{
    public DataTemplate? ItemTemplate { get; set; }
    public DataTemplate? ConflictItemTemplate { get; set; }
    public DataTemplate? HeaderTemplate { get; set; }
    public bool UseConflictTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
    {
        if (item is VaultGroupHeader)
            return HeaderTemplate;
        if (UseConflictTemplate)
            return ConflictItemTemplate ?? ItemTemplate;
        return ItemTemplate;
    }
}
