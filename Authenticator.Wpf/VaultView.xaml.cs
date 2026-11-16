using System.Collections.ObjectModel;
using System.ComponentModel;
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

    private const double SlideActionWidth = 92;
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
    private bool _chipSync;
    private readonly DispatcherTimer _searchDebounce;
    private readonly DispatcherTimer _toastTimer;

    private Border? _slideFront;
    private TranslateTransform? _slideTransform;
    private double _slideActionWidth = SlideActionWidth;
    private Point _slideStart;
    private double _slideOriginX;
    private bool _slideMoved;

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
            _autoFilter = info.UrlHint;
            _autoMatchTerms = info.MatchTerms.Count > 0
                ? info.MatchTerms
                : string.IsNullOrWhiteSpace(info.UrlHint)
                    ? Array.Empty<string>()
                    : new[] { info.UrlHint };

            UpdateFilterHint();
            // Auto-filter only drives the Relevant chip — never All/Favorites/Recents.
            if (_filter == VaultFilterMode.Relevant)
                ApplyFilter();
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
        try
        {
            LoadUiPrefs();
            _passwordIndex = null;
            // List path: no secret decrypt — filter/tab switches must stay cheap.
            _all = await _vault.GetItemsAsync(favoritesOnly: false, revealSecrets: false);
            var rows = _all.Select(VaultRow.From).ToList();
            _rowById = rows.ToDictionary(r => r.Id);
            _allRowsView = new ObservableCollection<VaultRow>(rows);
            await ApplyFilterAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not load vault: {ex.Message}", "Vault", MessageBoxButton.OK, MessageBoxImage.Warning);
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

        // Recents keep last-used order; other chips honor the sort menu.
        var nextList = _filter == VaultFilterMode.Recents
            ? rows.ToList()
            : SortRows(rows).ToList();

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
            if (SameRowIds(_rows, nextList) && ReferenceEquals(ItemsList.ItemsSource, _rows))
                return;

            _rows = new ObservableCollection<VaultRow>(nextList);
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
            _autoMatchTerms = current.MatchTerms;
            UpdateFilterHint();
        }

        if (_autoMatchTerms.Count == 0 && string.IsNullOrWhiteSpace(_autoFilter))
            return _allRowsView;

        var raw = _autoMatchTerms.Count > 0
            ? _autoMatchTerms
            : string.IsNullOrWhiteSpace(_autoFilter) ? Array.Empty<string>() : new[] { _autoFilter! };
        var terms = ForegroundWindowMonitor.StrongMatchTerms(raw);
        if (terms.Count == 0)
            return Array.Empty<VaultRow>();

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
    /// Relevance requires a distinctive term: shared domain label, or a strong token
    /// as a whole word in title/username/host/URL. Weak words like "tool" never match alone.
    /// </summary>
    private static bool MatchesRelevant(IReadOnlyList<string> terms, VaultRow r)
    {
        var vaultTokens = r.MatchTokens;
        foreach (var term in terms)
        {
            if (ForegroundWindowMonitor.IsWeakToken(term) && !term.Contains('.'))
                continue;

            if (!string.IsNullOrEmpty(r.Host) && DomainSharesLabel(r.Host, term))
                return true;

            if (HasWholeWord(r.SortTitle, term) ||
                HasWholeWord(r.Subtitle, term) ||
                HasWholeWord(r.Identity, term) ||
                HasWholeWord(r.Host, term))
                return true;

            // Longer distinctive tokens may match as a substring of a host/title compound
            // (e.g. "ynab" in "app.ynab.com"), but never short/generic words.
            if (term.Length >= 5 &&
                !term.Contains(' ') &&
                (r.Host.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                 r.SortTitle.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                 r.MatchBlob.Contains(term, StringComparison.OrdinalIgnoreCase)))
                return true;

            foreach (var vaultToken in vaultTokens)
            {
                if (vaultToken.Equals(term, StringComparison.OrdinalIgnoreCase))
                    return true;
                if (term.Length >= 5 && vaultToken.Length >= 5 &&
                    (vaultToken.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                     term.Contains(vaultToken, StringComparison.OrdinalIgnoreCase)))
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

        // Tokenize skips weak words — still allow exact phrase containment for multi-word terms.
        if (term.Contains(' ') && text.Contains(term, StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static bool DomainSharesLabel(string host, string term)
    {
        var needle = term.Trim().Trim('.').ToLowerInvariant();
        if (needle.StartsWith("www.", StringComparison.Ordinal))
            needle = needle[4..];
        if (needle.Length < 4 || ForegroundWindowMonitor.IsWeakToken(needle))
            return false;

        host = host.Trim().Trim('.').ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal))
            host = host[4..];

        if (host.Equals(needle, StringComparison.Ordinal) ||
            host.EndsWith("." + needle, StringComparison.Ordinal) ||
            needle.EndsWith("." + host, StringComparison.Ordinal))
            return true;

        // Exact DNS label only — no substring ("tool" must not hit random hosts).
        foreach (var label in host.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (label.Length < 4 || ForegroundWindowMonitor.IsWeakToken(label))
                continue;
            if (label.Equals(needle, StringComparison.OrdinalIgnoreCase))
                return true;
            // "ynab" ↔ "app.ynab.com"; "github" ↔ "github.com"
            if (needle.Length >= 5 &&
                (label.Equals(needle, StringComparison.OrdinalIgnoreCase) ||
                 (label.StartsWith(needle, StringComparison.OrdinalIgnoreCase) && label.Length - needle.Length <= 2) ||
                 (needle.StartsWith(label, StringComparison.OrdinalIgnoreCase) && needle.Length - label.Length <= 2)))
                return true;
        }

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
                CopyUsername(row);
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

    private async void SlideEdit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: VaultRow row })
            return;
        CloseOpenSlide();
        // Load metadata/notes without decrypting password — Reveal in the editor fetches it.
        var item = await _vault.GetByIdAsync(row.Id, revealSecrets: true, revealPassword: false);
        if (item is null) return;
        EditItem(item);
        e.Handled = true;
    }

    private async void SlideDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: VaultRow row })
            return;
        CloseOpenSlide();
        var confirm = MessageBox.Show(
            Window.GetWindow(this),
            $"Delete “{row.SortTitle}”?",
            "Delete",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
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
            MessageBox.Show(Window.GetWindow(this), ex.Message, "Favorite", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CopyPassword_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: VaultRow row })
            return;
        CopyPassword(row);
        e.Handled = true;
    }

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

    private void EditItem(VaultItem item)
    {
        var win = new VaultItemEditWindow(item) { Owner = Window.GetWindow(this) };
        if (win.ShowDialog() == true)
            _ = ReloadAsync();
    }

    private static MenuItem MakeMenu(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
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

        MessageBox.Show(
            $"Imported {win.ImportedCount ?? 0} entries from {win.ResultProvider.DisplayName}.",
            "Import");
        await ReloadAsync();
    }

    public async Task ConnectToProviderAsync()
    {
        var win = new ProviderConnectWindow(ProviderDialogMode.Connect) { Owner = Window.GetWindow(this) };
        if (win.ShowDialog() != true || win.ResultProvider is null)
            return;

        var msg = win.ImportedCount is int n
            ? $"Connected to {win.ResultProvider.DisplayName} and synced {n} entries."
            : $"Connected to {win.ResultProvider.DisplayName}.";
        MessageBox.Show(msg, "Connect");
        await ReloadAsync();
    }

    public async Task SyncConnectedProviderAsync(string providerId)
    {
        try
        {
            var result = await ProviderSessionService.Instance.SyncProviderAsync(providerId);
            await ReloadAsync();
            ReportSyncResult(providerId, result);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Sync failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public async Task SyncAllConnectedAsync()
    {
        try
        {
            var result = await ProviderSessionService.Instance.SyncAllConnectedAsync();
            await ReloadAsync();
            ReportSyncResult("connected providers", result);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Sync failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ReportSyncResult(string source, VaultSyncResult result)
    {
        var summary =
            $"Imported {result.Imported} new, {result.Unchanged} unchanged, {result.Conflicts} conflict(s) from {source}.";
        if (result.Conflicts > 0)
        {
            MessageBox.Show(
                Window.GetWindow(this),
                summary + "\n\nOpen the Conflicts chip (or click a red badge) to resolve in Diff/match.",
                "Sync",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            SelectFilter(VaultFilterMode.Conflicts, reload: false);
            ApplyFilter();
        }
        else
        {
            MessageBox.Show(Window.GetWindow(this), summary, "Sync", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    public void DisconnectProvider(string providerId)
    {
        ProviderSessionService.Instance.Disconnect(providerId);
        MessageBox.Show($"Disconnected {providerId}.", "Disconnect");
    }

    public void ShowProvidersMenu(FrameworkElement placementTarget)
    {
        var menu = new ContextMenu { PlacementTarget = placementTarget, Placement = PlacementMode.Top };
        menu.Items.Add(MakeMenu("Connect to…", () => _ = ConnectToProviderAsync()));
        menu.Items.Add(MakeMenu("Import from…", () => _ = ImportFromProviderAsync()));

        var conflictCount = _allRowsView.Count(r => r.HasMismatch);
        if (conflictCount > 0)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(MakeMenu($"Review conflicts ({conflictCount})…", () =>
            {
                SelectFilter(VaultFilterMode.Conflicts, reload: false);
                ApplyFilter();
            }));
        }

        var saved = ProviderSessionService.Instance.SavedConnections.ToList();
        if (saved.Count > 0)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(MakeMenu("Sync all connected", () => _ = SyncAllConnectedAsync()));
            foreach (var conn in saved)
            {
                var providerId = conn.ProviderId;
                var label = string.IsNullOrWhiteSpace(conn.DisplayName) ? providerId : conn.DisplayName;
                var sub = new MenuItem { Header = label };
                sub.Items.Add(MakeMenu("Sync now", () => _ = SyncConnectedProviderAsync(providerId)));
                sub.Items.Add(MakeMenu("Review conflicts…", () => _ = OpenFirstConflictForProviderAsync(providerId)));
                sub.Items.Add(MakeMenu("Disconnect", () => DisconnectProvider(providerId)));
                menu.Items.Add(sub);
            }
        }

        menu.IsOpen = true;
    }

    private void Badge_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: VaultRow row } || !row.HasMismatch)
            return;
        e.Handled = true;
        _ = OpenDiffAsync(row);
    }

    private async Task OpenFirstConflictForProviderAsync(string providerId)
    {
        SelectFilter(VaultFilterMode.Conflicts, reload: false);
        ApplyFilter();
        var row = _allRowsView.FirstOrDefault(r => r.HasMismatch);
        if (row is not null)
            await OpenDiffAsync(row, preferredProviderId: providerId);
    }

    private async Task OpenDiffAsync(VaultRow row, string? preferredProviderId = null)
    {
        try
        {
            var item = await _vault.GetByIdAsync(row.Id);
            if (item is null)
                return;

            var link = item.ExternalLinks
                .Where(l => l.IsMismatch)
                .OrderByDescending(l => string.Equals(l.Provider, preferredProviderId, StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault()
                ?? item.ExternalLinks.FirstOrDefault();
            if (link is null)
            {
                MessageBox.Show(Window.GetWindow(this), "This item has no linked provider.", "Diff",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            ProviderEntry? remote = null;
            IVaultProvider? provider = null;
            try
            {
                provider = await ProviderSessionService.Instance.EnsureConnectedAsync(link.Provider);
            }
            catch
            {
                // Diff can still open with Shared-only values.
            }

            if (provider is not null)
            {
                var entries = await provider.ListEntriesAsync();
                remote = entries.FirstOrDefault(e =>
                    string.Equals(e.ExternalId, link.ExternalId, StringComparison.OrdinalIgnoreCase));
            }

            var win = new DiffMatchWindow(item, remote, link.Provider) { Owner = Window.GetWindow(this) };
            if (win.ShowDialog() == true)
                await ReloadAsync();
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, $"Failed to open Diff for {row.Id}.", nameof(VaultView));
            MessageBox.Show(Window.GetWindow(this), ex.Message, "Diff", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public sealed class VaultRow : INotifyPropertyChanged
    {
        private bool _isFavorite;

        public Guid Id { get; set; }
        public VaultItemType ItemType { get; set; }
        public string Subtitle { get; set; } = "";
        public string Identity { get; set; } = "";
        public string Username { get; set; } = "";
        public string SortTitle { get; set; } = "";
        public string Host { get; set; } = "";
        public string GroupKey { get; set; } = "";
        /// <summary>Lowercased title/username/host/url blob for partial relevance matching.</summary>
        public string MatchBlob { get; set; } = "";
        public string[] MatchTokens { get; set; } = Array.Empty<string>();
        public bool IsFavorite
        {
            get => _isFavorite;
            set
            {
                if (_isFavorite == value)
                    return;
                _isFavorite = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsFavorite)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Star)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StarBrush)));
            }
        }
        public string Initial { get; set; } = "?";
        public Brush IconBrush { get; set; } = Brushes.Gray;
        public bool HasMismatch { get; set; }
        public string BadgeGlyph { get; set; } = "\uE77B";
        public Brush BadgeBrush { get; set; } = AccountEdits.BrushFromHex("#1A73E8");
        public Cursor BadgeCursor => HasMismatch ? Cursors.Hand : Cursors.Arrow;
        public string? BadgeToolTip => HasMismatch ? "Conflict — open Diff/match" : null;
        public string Star => IsFavorite ? "★" : "☆";
        public Brush StarBrush => IsFavorite
            ? AccountEdits.BrushFromHex("#F5B301")
            : AccountEdits.BrushFromHex("#9CA3AF");

        public event PropertyChangedEventHandler? PropertyChanged;

        public static VaultRow From(VaultItem item)
        {
            var host = TryHost(item.Url);
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
                host.Contains('.') ? host.Split('.')[0] : "").ToLowerInvariant();
            var tokens = ForegroundWindowMonitor.StrongMatchTerms(
                new[] { item.Title, item.Username ?? "", item.Url ?? "", host });
            return new VaultRow
            {
                Id = item.Id,
                ItemType = item.ItemType,
                Subtitle = subtitle,
                Identity = identity,
                Username = username,
                SortTitle = item.Title,
                Host = host,
                MatchBlob = blob,
                MatchTokens = tokens.ToArray(),
                IsFavorite = item.IsFavorite,
                HasMismatch = item.HasMismatch,
                Initial = string.IsNullOrWhiteSpace(seed) ? "?" : seed.Trim()[..1].ToUpperInvariant(),
                IconBrush = AccountEdits.InitialBrush(seed),
                BadgeGlyph = item.HasMismatch ? "\uE7BA" : GlyphFor(item.ItemType), // Warning / type
                BadgeBrush = item.HasMismatch
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

        private static string TryHost(string? url)
        {
            if (!string.IsNullOrWhiteSpace(url) &&
                Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                !string.IsNullOrWhiteSpace(uri.Host))
                return uri.Host;
            return "";
        }
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
    public DataTemplate? HeaderTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
        => item is VaultGroupHeader ? HeaderTemplate : ItemTemplate;
}
