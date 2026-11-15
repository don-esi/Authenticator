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
        Recents
    }

    private const double SlideActionWidth = 92;
    private const double SlideOpenThreshold = 0.45;

    private readonly VaultItemService _vault = new();
    private ObservableCollection<VaultRow> _rows = new();
    private ObservableCollection<VaultRow> _allRowsView = new();
    private List<VaultItem> _all = new();
    private Dictionary<Guid, VaultRow> _rowById = new();
    private VaultFilterMode _filter = VaultFilterMode.All;
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
        ItemsList.ItemsSource = _rows;
        _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            ApplyFilter();
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

    public async Task ReloadAsync(bool favoritesOnly = false)
    {
        if (favoritesOnly)
            SelectFilter(VaultFilterMode.Favorites, reload: false);

        CloseOpenSlide();
        try
        {
            // List path: no secret decrypt — filter/tab switches must stay cheap.
            _all = await _vault.GetItemsAsync(favoritesOnly: false, revealSecrets: false);
            var rows = _all.Select(VaultRow.From).ToList();
            _rowById = rows.ToDictionary(r => r.Id);
            _allRowsView = new ObservableCollection<VaultRow>(rows);
            ApplyFilter();
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
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    private void FilterChip_Checked(object sender, RoutedEventArgs e)
    {
        // IsChecked="True" in XAML fires before later chips exist — wait until loaded.
        if (_chipSync || !IsLoaded || sender is not ToggleButton chip)
            return;

        var mode = chip == ChipRelevant ? VaultFilterMode.Relevant
            : chip == ChipFavorites ? VaultFilterMode.Favorites
            : chip == ChipRecents ? VaultFilterMode.Recents
            : VaultFilterMode.All;
        SelectFilter(mode, reload: false);
        ApplyFilter();
    }

    private void FilterChip_Unchecked(object sender, RoutedEventArgs e)
    {
        if (_chipSync || !IsLoaded)
            return;
        if (ChipRelevant?.IsChecked != true && ChipAll?.IsChecked != true &&
            ChipFavorites?.IsChecked != true && ChipRecents?.IsChecked != true)
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
        _chipSync = false;
        UpdateFilterHint();
        if (reload)
            _ = ReloadAsync();
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

    private void ApplyFilter()
    {
        if (SearchBox is null || ItemsList is null)
            return;

        CloseOpenSlide();
        var q = SearchBox.Text?.Trim() ?? "";

        // Fast path: All items, no search — rebind the cached collection (no rebuild, no decrypt).
        if (_filter == VaultFilterMode.All && q.Length == 0)
        {
            if (ReferenceEquals(ItemsList.ItemsSource, _allRowsView))
                return;
            _rows = _allRowsView;
            ItemsList.ItemsSource = _allRowsView;
            return;
        }

        IEnumerable<VaultRow> rows = _filter switch
        {
            VaultFilterMode.Favorites => _allRowsView.Where(r => r.IsFavorite),
            VaultFilterMode.Recents => _all
                .Where(i => i.LastUsedAt is not null)
                .OrderByDescending(i => i.LastUsedAt)
                .Take(10)
                .Select(i => _rowById[i.Id]),
            VaultFilterMode.Relevant => FilterRelevantRows(),
            _ => _allRowsView
        };

        if (!string.IsNullOrWhiteSpace(q))
            rows = rows.Where(r => MatchesSearch(q, r));

        if (_filter is not VaultFilterMode.Recents)
            rows = rows.OrderBy(r => r.SortTitle, StringComparer.OrdinalIgnoreCase);

        var nextList = rows.ToList();
        if (SameRowIds(_rows, nextList))
            return;

        _rows = new ObservableCollection<VaultRow>(nextList);
        ItemsList.ItemsSource = _rows;
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

    private static bool MatchesSearch(string needle, VaultRow r) =>
        r.Subtitle.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
        r.Identity.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
        r.SortTitle.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
        r.MatchBlob.Contains(needle, StringComparison.OrdinalIgnoreCase);

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
            var count = await ProviderSessionService.Instance.SyncProviderAsync(providerId);
            MessageBox.Show($"Synced {count} entries from {providerId}.", "Sync");
            await ReloadAsync();
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
            var count = await ProviderSessionService.Instance.SyncAllConnectedAsync();
            MessageBox.Show($"Synced {count} entries from connected providers.", "Sync");
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Sync failed", MessageBoxButton.OK, MessageBoxImage.Warning);
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
                sub.Items.Add(MakeMenu("Disconnect", () => DisconnectProvider(providerId)));
                menu.Items.Add(sub);
            }
        }

        menu.IsOpen = true;
    }

    public sealed class VaultRow : INotifyPropertyChanged
    {
        private bool _isFavorite;

        public Guid Id { get; set; }
        public string Subtitle { get; set; } = "";
        public string Identity { get; set; } = "";
        public string SortTitle { get; set; } = "";
        public string Host { get; set; } = "";
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
        public string BadgeGlyph { get; set; } = "\uE77B";
        public Brush BadgeBrush { get; set; } = AccountEdits.BrushFromHex("#1A73E8");
        public string Star => IsFavorite ? "★" : "☆";
        public Brush StarBrush => IsFavorite
            ? AccountEdits.BrushFromHex("#F5B301")
            : AccountEdits.BrushFromHex("#9CA3AF");

        public event PropertyChangedEventHandler? PropertyChanged;

        public static VaultRow From(VaultItem item)
        {
            var host = TryHost(item.Url);
            var subtitle = !string.IsNullOrWhiteSpace(host) ? host : item.Title;
            // Secondary line: username when present, otherwise host (never duplicate the title).
            var identity = !string.IsNullOrWhiteSpace(item.Username)
                ? item.Username!
                : host;
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
                Subtitle = subtitle,
                Identity = identity,
                SortTitle = item.Title,
                Host = host,
                MatchBlob = blob,
                MatchTokens = tokens.ToArray(),
                IsFavorite = item.IsFavorite,
                Initial = string.IsNullOrWhiteSpace(seed) ? "?" : seed.Trim()[..1].ToUpperInvariant(),
                IconBrush = AccountEdits.InitialBrush(seed),
                BadgeGlyph = GlyphFor(item.ItemType),
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
