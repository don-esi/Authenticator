using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Authenticator.Crypto;
using Authenticator.Data.Models;
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

    private const double SlideActionWidth = 140;
    private const double SlideOpenThreshold = 0.45;

    private readonly VaultItemService _vault = new();
    private ObservableCollection<VaultRow> _rows = new();
    private ObservableCollection<VaultRow> _allRowsView = new();
    private List<VaultItem> _all = new();
    private Dictionary<Guid, VaultRow> _rowById = new();
    private VaultFilterMode _filter = VaultFilterMode.All;
    private string? _autoFilter;
    private bool _chipSync;
    private readonly DispatcherTimer _searchDebounce;

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
        ForegroundWindowMonitor.Instance.ForegroundChanged += (_, info) =>
        {
            var next = info.UrlHint ?? info.Title;
            if (string.Equals(_autoFilter, next, StringComparison.Ordinal))
                return;

            _autoFilter = next;
            if (FilterHint is not null)
                FilterHint.Text = string.IsNullOrWhiteSpace(_autoFilter) ? "" : $"Filter: {_autoFilter}";
            if (!string.IsNullOrWhiteSpace(_autoFilter) && _filter == VaultFilterMode.Relevant)
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
        if (reload)
            _ = ReloadAsync();
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
                .OrderByDescending(i => i.ModifiedAt ?? i.CreatedAt)
                .Take(40)
                .Select(i => _rowById[i.Id]),
            VaultFilterMode.Relevant when !string.IsNullOrWhiteSpace(_autoFilter) =>
                _allRowsView.Where(r => MatchesRow(_autoFilter!, r)),
            _ => _allRowsView
        };

        if (!string.IsNullOrWhiteSpace(q))
            rows = rows.Where(r => MatchesRow(q, r));

        if (_filter is not VaultFilterMode.Recents)
            rows = rows.OrderBy(r => r.SortTitle, StringComparer.OrdinalIgnoreCase);

        var nextList = rows.ToList();
        if (SameRowIds(_rows, nextList))
            return;

        _rows = new ObservableCollection<VaultRow>(nextList);
        ItemsList.ItemsSource = _rows;
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

    private static bool MatchesRow(string needle, VaultRow r) =>
        r.Subtitle.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
        r.Identity.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
        r.SortTitle.Contains(needle, StringComparison.OrdinalIgnoreCase);

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
                CopyPassword(row);
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

    private void SlideCopy_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: VaultRow row })
            return;
        CloseOpenSlide();
        ShowCopyMenu(row, sender as UIElement);
        e.Handled = true;
    }

    private async void SlideEdit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: VaultRow row })
            return;
        CloseOpenSlide();
        var item = await LoadItemAsync(row);
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
        await _vault.SetFavoriteAsync(row.Id, !row.IsFavorite);
        await ReloadAsync();
        e.Handled = true;
    }

    private async void ShowCopyMenu(VaultRow row, UIElement? placement)
    {
        var item = await LoadItemAsync(row);
        if (item is null) return;

        var menu = new ContextMenu();
        if (placement is not null)
        {
            menu.PlacementTarget = placement;
            menu.Placement = PlacementMode.Bottom;
        }
        menu.Items.Add(MakeMenu("Copy username", () => ClipboardHelper.Copy(item.Username)));
        menu.Items.Add(MakeMenu("Copy password", () => ClipboardHelper.Copy(item.Password)));
        if (!string.IsNullOrWhiteSpace(item.TotpSecret))
        {
            menu.Items.Add(MakeMenu("Copy TOTP", () =>
            {
                var code = TotpGenerator.TryGenerate(item.TotpSecret, "SHA1", 6, 30);
                ClipboardHelper.Copy(string.IsNullOrEmpty(code) ? item.TotpSecret : code);
            }));
        }
        menu.IsOpen = true;
    }

    private async void CopyPassword(VaultRow row)
    {
        var item = await LoadItemAsync(row);
        if (item is null) return;
        ClipboardHelper.Copy(item.Password);
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
        var win = new VaultItemEditWindow(new VaultItem { ItemType = VaultItemType.Login })
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

    public sealed class VaultRow
    {
        public Guid Id { get; set; }
        public string Subtitle { get; set; } = "";
        public string Identity { get; set; } = "";
        public string SortTitle { get; set; } = "";
        public bool IsFavorite { get; set; }
        public string Initial { get; set; } = "?";
        public Brush IconBrush { get; set; } = Brushes.Gray;
        public Visibility BadgeVisibility { get; set; } = Visibility.Collapsed;
        public string Star { get; set; } = "\uE734";
        public Brush StarBrush { get; set; } = Brushes.Gray;

        public static VaultRow From(VaultItem item)
        {
            var subtitle = HostOrTitle(item);
            var identity = string.IsNullOrWhiteSpace(item.Username) ? item.Title : item.Username!;
            var seed = string.IsNullOrWhiteSpace(subtitle) ? identity : subtitle;
            return new VaultRow
            {
                Id = item.Id,
                Subtitle = subtitle,
                Identity = identity,
                SortTitle = item.Title,
                IsFavorite = item.IsFavorite,
                Initial = string.IsNullOrWhiteSpace(seed) ? "?" : seed.Trim()[..1].ToUpperInvariant(),
                IconBrush = AccountEdits.InitialBrush(seed),
                BadgeVisibility = item.HasMismatch || item.ExternalLinks.Count > 0
                    ? Visibility.Visible
                    : Visibility.Collapsed,
                Star = item.IsFavorite ? "\uE735" : "\uE734",
                StarBrush = item.IsFavorite
                    ? AccountEdits.BrushFromHex("#F5B301")
                    : AccountEdits.BrushFromHex("#9CA3AF")
            };
        }

        private static string HostOrTitle(VaultItem item)
        {
            if (!string.IsNullOrWhiteSpace(item.Url) &&
                Uri.TryCreate(item.Url, UriKind.Absolute, out var uri) &&
                !string.IsNullOrWhiteSpace(uri.Host))
                return uri.Host;
            return item.Title;
        }
    }
}
