using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Authenticator.Data.Models;
using Authenticator.Logging;
using Authenticator.Services;

namespace Authenticator;

public partial class CodesView : UserControl
{
    private readonly DatabaseService _database = new();
    private readonly TotpService _totp = new();
    private readonly CodesUiPreferences _uiPrefs = new();
    private readonly DispatcherTimer _codeTimer;
    private readonly DispatcherTimer _progressTimer;
    private readonly DispatcherTimer _toastTimer;
    private readonly ObservableCollection<AccountCardRow> _rows = new();
    private List<Account> _accounts = [];
    private List<Folder> _folders = [];
    private int? _folderId;
    private int _refreshSeq;
    private bool _refreshing;
    private CodesSortMode _sortMode = CodesSortMode.ServiceAsc;
    private CodesViewMode _viewMode = CodesViewMode.Condensed;
    private AppThemeMode _themeMode = AppThemeMode.System;
    private bool _codesMasked;

    private Border? _slideFront;
    private TranslateTransform? _slideTransform;
    private double _slideActionWidth;
    private Point _slideStart;
    private double _slideOriginX;
    private bool _slideMoved;

    private const double FolderActionWidth = 96;
    private const double AccountActionWidth = 140;
    private const double SlideOpenThreshold = 0.4;

    public CodesView()
    {
        InitializeComponent();
        AccountList.ItemsSource = _rows;
        LoadUiPrefs();
        ApplyViewMode();
        _codeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _codeTimer.Tick += (_, _) => RefreshCodes();
        _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _progressTimer.Tick += (_, _) => UpdateProgress();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1400) };
        _toastTimer.Tick += (_, _) => HideStatusToast();
    }

    private void LoadUiPrefs()
    {
        var prefs = _uiPrefs.Load();
        _sortMode = prefs.SortMode;
        _viewMode = prefs.ViewMode;
        _themeMode = prefs.ThemeMode;
    }

    public async Task ReloadAsync()
    {
        try
        {
            LoadUiPrefs();
            _accounts = await _database.GetAllAccountsAsync();
            _folders = await _database.GetAllFoldersAsync();
            if (_folderId is int selected && _folders.All(folder => folder.Id != selected))
                _folderId = null;
            ApplyFilter();
            RefreshCodes();
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Failed to load accounts.", nameof(CodesView));
            EmptyText.Text = ex.Message;
            EmptyHint.Visibility = Visibility.Collapsed;
            EmptyPanel.Visibility = Visibility.Visible;
        }
    }

    public void SetCodesMasked(bool masked)
    {
        _codesMasked = masked;
        foreach (var row in _rows)
            ApplyDisplayCode(row);
    }

    public void RefreshCodes()
    {
        _ = RefreshCodesAsync();
    }

    private async Task RefreshCodesAsync()
    {
        if (_refreshing)
            return;

        _refreshing = true;
        var seq = ++_refreshSeq;
        var rows = _rows.ToList();
        try
        {
            if (rows.Count == 0)
                return;

            // Client-side TOTP: SQL dbo.GetTotp cannot read AES-GCM secrets at rest.
            await Task.Yield();
            if (seq != _refreshSeq)
                return;

            foreach (var row in rows)
            {
                try
                {
                    var code = TotpService.GenerateCode(row.Account);
                    if (string.IsNullOrWhiteSpace(code))
                    {
                        row.RawCode = string.Empty;
                        row.DisplayCode = "------";
                        row.SecondsRemaining = 0;
                        continue;
                    }

                    row.RawCode = code;
                    ApplyDisplayCode(row);
                    row.SecondsRemaining = _totp.GetSecondsRemainingExact(row.Account);
                }
                catch (Exception ex)
                {
                    LoggingConfiguration.LogException(ex, $"Failed to generate TOTP for account id {row.Id}.", nameof(CodesView));
                    row.RawCode = string.Empty;
                    row.DisplayCode = "------";
                    row.SecondsRemaining = 0;
                }
            }
        }
        catch (Exception ex)
        {
            if (seq != _refreshSeq)
                return;

            foreach (var row in rows)
            {
                row.RawCode = string.Empty;
                row.DisplayCode = "------";
                row.SecondsRemaining = 0;
            }

            LoggingConfiguration.LogException(ex, "Failed to refresh TOTP codes.", nameof(CodesView));
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void UpdateProgress()
    {
        var wrapped = false;
        foreach (var row in _rows)
        {
            var remaining = _totp.GetSecondsRemainingExact(row.Account);
            if (remaining > row.SecondsRemaining + 0.75)
                wrapped = true;
            row.SecondsRemaining = remaining;
        }

        if (wrapped)
            RefreshCodes();
    }

    private async void CodesView_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            await ReloadAsync();
            _codeTimer.Start();
            _progressTimer.Start();
        }
        else
        {
            _codeTimer.Stop();
            _progressTimer.Stop();
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        ApplyFilter();
        RefreshCodes();
    }

    private void ApplyFilter()
    {
        if (SearchBox is null || ChipPanel is null || EmptyPanel is null || EmptyText is null)
            return;

        CloseOpenSlide();
        UpdateFolderChrome();

        var query = SearchBox.Text.Trim();
        var filtered = _accounts.Where(account =>
        {
            if (_folderId is int folderId)
            {
                if (account.FolderId != folderId)
                    return false;
            }
            else if (account.FolderId is not null)
            {
                // Main page: only accounts that are not already in a folder.
                return false;
            }

            if (query.Length == 0)
                return true;
            return account.ServiceName.Contains(query, StringComparison.OrdinalIgnoreCase)
                   || account.Username.Contains(query, StringComparison.OrdinalIgnoreCase);
        });

        filtered = ApplySort(filtered);

        _rows.Clear();
        foreach (var account in filtered)
            _rows.Add(AccountCardRow.From(account));

        var noSearch = query.Length == 0;
        var emptyAll = _accounts.Count == 0 && _folderId is null && noSearch;
        EmptyText.Text = emptyAll
            ? "No accounts yet."
            : _folderId is not null && noSearch
                ? "No accounts in this folder."
                : _folderId is null && noSearch
                    ? "No unfiled accounts."
                    : "No matching accounts.";
        EmptyHint.Text = emptyAll
            ? "Tap + to add an account"
            : _folderId is null && noSearch
                ? "Accounts in folders stay inside those folders"
                : "Try another search or folder";
        EmptyHint.Visibility = Visibility.Visible;
        EmptyPanel.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        BuildChips();
    }

    private void UpdateFolderChrome()
    {
        if (MainTitle is null || FolderHeader is null || FolderTitle is null || ChipPanel is null || AddFolderButton is null)
            return;

        var inFolder = _folderId is not null;
        var folder = inFolder ? _folders.FirstOrDefault(item => item.Id == _folderId) : null;

        MainTitle.Visibility = inFolder ? Visibility.Collapsed : Visibility.Visible;
        FolderHeader.Visibility = inFolder ? Visibility.Visible : Visibility.Collapsed;
        FolderTitle.Text = folder?.Name ?? "Folder";
        ChipPanel.Visibility = inFolder ? Visibility.Collapsed : Visibility.Visible;
        AddFolderButton.Visibility = inFolder ? Visibility.Collapsed : Visibility.Visible;
    }

    private void BuildChips()
    {
        ChipPanel.Children.Clear();
        if (_folderId is not null)
            return;

        foreach (var folder in _folders.OrderBy(folder => folder.Name, StringComparer.OrdinalIgnoreCase))
        {
            var count = _accounts.Count(account => account.FolderId == folder.Id);
            ChipPanel.Children.Add(MakeFolderRow(folder.Name, count, folder));
        }
    }

    private FrameworkElement MakeFolderRow(string label, int count, Folder folder)
    {
        var background = string.IsNullOrWhiteSpace(folder.Color) ? null : folder.Color;
        var content = BuildFolderContent(label, count);

        var host = new Grid
        {
            Margin = new Thickness(0, 0, 0, 6),
            ClipToBounds = true,
            Height = 40
        };

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        actions.Children.Add(MakeSlideActionButton(
            "\uE70F", "#9CA3AF", "Edit folder", folder, FolderEdit_Click, marginRight: 4));
        actions.Children.Add(MakeSlideActionButton(
            "\uE74D", "#EF4444", "Delete folder", folder, FolderDelete_Click, marginRight: 0));

        var front = new Border
        {
            Background = background is null
                ? (Brush)FindResource("ChipIdleBrush")
                : AccountEdits.BrushFromHex(background),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 8, 12, 8),
            Cursor = Cursors.Hand,
            Child = content,
            Tag = new SlideTarget(SlideKind.Folder, folder.Id, FolderActionWidth),
            RenderTransform = new TranslateTransform()
        };
        AutomationProperties.SetName(front, label);
        front.MouseLeftButtonDown += Slide_MouseLeftButtonDown;
        front.MouseMove += Slide_MouseMove;
        front.MouseLeftButtonUp += Slide_MouseLeftButtonUp;
        front.LostMouseCapture += Slide_LostMouseCapture;

        host.Children.Add(actions);
        host.Children.Add(front);
        return host;
    }

    private Grid BuildFolderContent(string label, int count)
    {
        var fgBrush = (Brush)FindResource("ChipIdleTextBrush");
        var mutedBrush = (Brush)FindResource("ChipIdleMutedBrush");

        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var folderIcon = new TextBlock
        {
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            Text = "\uE8B7",
            FontSize = 14,
            Foreground = fgBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        var name = new TextBlock
        {
            Text = label,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = fgBrush,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var meta = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        meta.Children.Add(new TextBlock
        {
            Text = count.ToString(),
            FontSize = 12,
            Foreground = mutedBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        });
        meta.Children.Add(new TextBlock
        {
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            Text = "\uE76C",
            FontSize = 12,
            Foreground = mutedBrush,
            VerticalAlignment = VerticalAlignment.Center
        });

        Grid.SetColumn(folderIcon, 0);
        Grid.SetColumn(name, 1);
        Grid.SetColumn(meta, 2);
        content.Children.Add(folderIcon);
        content.Children.Add(name);
        content.Children.Add(meta);
        return content;
    }

    private Button MakeSlideActionButton(
        string glyph,
        string background,
        string name,
        object tag,
        RoutedEventHandler click,
        double marginRight)
    {
        var button = new Button
        {
            Width = 44,
            Margin = new Thickness(0, 0, marginRight, 0),
            VerticalAlignment = VerticalAlignment.Stretch,
            Tag = tag,
            Background = AccountEdits.BrushFromHex(background),
            Style = (Style)FindResource("SlideActionButton"),
            ToolTip = name,
            Content = new TextBlock
            {
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                Text = glyph,
                FontSize = 16,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        AutomationProperties.SetName(button, name);
        button.Click += click;
        return button;
    }

    private void SelectFolder(int? folderId, bool animate = true)
    {
        if (_folderId == folderId)
            return;

        var entering = folderId is not null && _folderId is null;
        var leaving = folderId is null && _folderId is not null;
        _folderId = folderId;

        if (animate && (entering || leaving) && CodesPaneTransform is not null && ContentHost is not null)
        {
            AnimateFolderPane(forward: entering, after: () =>
            {
                ApplyFilter();
                RefreshCodes();
            });
            return;
        }

        ApplyFilter();
        RefreshCodes();
    }

    private void FolderBack_Click(object sender, RoutedEventArgs e)
        => SelectFolder(null);

    private void AnimateFolderPane(bool forward, Action after)
    {
        var width = Math.Max(ContentHost.ActualWidth, 1);
        // Enter folder: slide in from the right. Leave: slide in from the left.
        var from = forward ? width : -width * 0.45;

        CodesPaneTransform.BeginAnimation(TranslateTransform.XProperty, null);
        CodesPaneTransform.X = from;
        after();

        var enter = new DoubleAnimation(from, 0, TimeSpan.FromMilliseconds(240))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        CodesPaneTransform.BeginAnimation(TranslateTransform.XProperty, enter);
    }

    /// <summary>
    /// Favorites pin first; chosen sort applies within favorite and non-favorite groups.
    /// </summary>
    private IEnumerable<Account> ApplySort(IEnumerable<Account> accounts)
        => _sortMode switch
        {
            CodesSortMode.LatestFirst => accounts
                .OrderByDescending(account => account.IsFavorite)
                .ThenByDescending(account => account.CreatedAt)
                .ThenBy(account => account.ServiceName, StringComparer.OrdinalIgnoreCase),
            CodesSortMode.EarliestFirst => accounts
                .OrderByDescending(account => account.IsFavorite)
                .ThenBy(account => account.CreatedAt)
                .ThenBy(account => account.ServiceName, StringComparer.OrdinalIgnoreCase),
            CodesSortMode.ServiceDesc => accounts
                .OrderByDescending(account => account.IsFavorite)
                .ThenByDescending(account => account.ServiceName, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(account => account.Username, StringComparer.OrdinalIgnoreCase),
            CodesSortMode.AccountAsc => accounts
                .OrderByDescending(account => account.IsFavorite)
                .ThenBy(account => account.Username, StringComparer.OrdinalIgnoreCase)
                .ThenBy(account => account.ServiceName, StringComparer.OrdinalIgnoreCase),
            CodesSortMode.AccountDesc => accounts
                .OrderByDescending(account => account.IsFavorite)
                .ThenByDescending(account => account.Username, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(account => account.ServiceName, StringComparer.OrdinalIgnoreCase),
            _ => accounts
                .OrderByDescending(account => account.IsFavorite)
                .ThenBy(account => account.ServiceName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(account => account.Username, StringComparer.OrdinalIgnoreCase)
        };

    private void PersistUiPrefs()
    {
        var state = _uiPrefs.Load();
        state.SortMode = _sortMode;
        state.ViewMode = _viewMode;
        state.ThemeMode = _themeMode;
        _uiPrefs.Save(state);
    }

    private void OpenButtonMenu(Button button)
    {
        if (button.ContextMenu is null)
            return;
        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        button.ContextMenu.IsOpen = true;
    }

    private void ViewModeButton_Click(object sender, RoutedEventArgs e)
        => OpenButtonMenu(ViewModeButton);

    private void SortButton_Click(object sender, RoutedEventArgs e)
        => OpenButtonMenu(SortButton);

    private void ViewModeMenu_Opened(object sender, RoutedEventArgs e)
        => MarkCheckedMenuItems(sender, _viewMode.ToString());

    private void SortMenu_Opened(object sender, RoutedEventArgs e)
        => MarkCheckedMenuItems(sender, _sortMode.ToString());

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

    private void ViewModeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag }
            || !Enum.TryParse<CodesViewMode>(tag, out var mode)
            || mode == _viewMode)
            return;

        _viewMode = mode;
        PersistUiPrefs();
        ApplyViewMode();
    }

    private void SortMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag }
            || !Enum.TryParse<CodesSortMode>(tag, out var mode)
            || mode == _sortMode)
            return;

        _sortMode = mode;
        PersistUiPrefs();
        ApplyFilter();
        RefreshCodes();
    }

    private void ApplyViewMode()
    {
        if (AccountList is null || ViewModeIcon is null)
            return;

        CloseOpenSlide();

        var (templateKey, panelKey, glyph) = _viewMode switch
        {
            CodesViewMode.Large => ("LargeAccountTemplate", "ListAccountsPanel", "\uE8A5"),
            CodesViewMode.Squares => ("SquareAccountTemplate", "SquareAccountsPanel", "\uE8A9"),
            _ => ("CondensedAccountTemplate", "ListAccountsPanel", "\uE8C4")
        };

        AccountList.ItemTemplate = (DataTemplate)FindResource(templateKey);
        AccountList.ItemsPanel = (ItemsPanelTemplate)FindResource(panelKey);
        ViewModeIcon.Text = glyph;
        ViewModeButton.ToolTip = _viewMode switch
        {
            CodesViewMode.Large => "View size: Large",
            CodesViewMode.Squares => "View size: Squares",
            _ => "View size: Condensed"
        };
    }

    private void CopyAccountCode(AccountCardRow row)
    {
        if (_codesMasked)
        {
            ShowStatusToast("Move mouse or press a key to show codes");
            return;
        }

        if (string.IsNullOrWhiteSpace(row.RawCode))
            return;

        Clipboard.SetText(row.RawCode);
        ShowStatusToast("Copied");
    }

    private void ApplyDisplayCode(AccountCardRow row)
    {
        if (string.IsNullOrWhiteSpace(row.RawCode))
        {
            row.DisplayCode = "------";
            return;
        }

        row.DisplayCode = _codesMasked
            ? new string('•', Math.Max(6, row.RawCode.Length))
            : AccountEdits.FormatCode(row.RawCode);
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

    private async void Favorite_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: AccountCardRow row })
            return;

        e.Handled = true;
        CloseOpenSlide();

        var next = !row.IsFavorite;
        try
        {
            await _database.SetAccountFavoriteAsync(row.Id, next);
            row.IsFavorite = next;
            row.Account.IsFavorite = next;
            var stored = _accounts.FirstOrDefault(account => account.Id == row.Id);
            if (stored is not null)
                stored.IsFavorite = next;

            ApplyFilter();
            RefreshCodes();
            ShowStatusToast(next ? "Added to favorites" : "Removed from favorites");
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, $"Failed to update favorite for account id {row.Id}.", nameof(CodesView));
            MessageBox.Show(Window.GetWindow(this), ex.Message, "Favorite", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new FolderWindow { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true)
            await ReloadAsync();
    }

    private async void FolderEdit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Folder folder })
            return;

        CloseOpenSlide();
        var dialog = new FolderWindow(folder) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true)
            await ReloadAsync();
    }

    private async void FolderDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Folder folder })
            return;

        CloseOpenSlide();
        var confirm = MessageBox.Show(
            Window.GetWindow(this),
            $"Delete folder \"{folder.Name}\"? Accounts in it return to the main list.",
            "Delete folder",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
            return;

        try
        {
            await _database.DeleteFolderAsync(folder.Id);
            if (_folderId == folder.Id)
                _folderId = null;
            await ReloadAsync();
            ShowStatusToast("Folder deleted");
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, $"Failed to delete folder id {folder.Id}.", nameof(CodesView));
            MessageBox.Show(Window.GetWindow(this), ex.Message, "Delete folder", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void AddAccount_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AddAccountWindow(existing: null, defaultFolderId: _folderId) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true)
            await ReloadAsync();
    }

    private void SlideCopy_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AccountCardRow row })
            return;

        CloseOpenSlide();
        CopyAccountCode(row);
    }

    private async void SlideEdit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AccountCardRow row })
            return;

        CloseOpenSlide();
        var dialog = new AddAccountWindow(row.Account, row.Account.FolderId) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true)
            await ReloadAsync();
    }

    private async void SlideDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AccountCardRow row })
            return;

        CloseOpenSlide();
        var confirm = MessageBox.Show(
            Window.GetWindow(this),
            $"Delete \"{row.ServiceName}\"?",
            "Delete account",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
            return;

        try
        {
            await _database.DeleteAccountAsync(row.Id);
            await ReloadAsync();
            ShowStatusToast("Account deleted");
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, $"Failed to delete account id {row.Id}.", nameof(CodesView));
            MessageBox.Show(Window.GetWindow(this), ex.Message, "Delete account", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void AccountSlide_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Border border)
            return;

        // DataTemplate Freezables can be shared/frozen; give each row a mutable transform.
        border.RenderTransform = new TranslateTransform();
        border.Tag = new SlideTarget(SlideKind.Account, null, AccountActionWidth);
    }

    private void Slide_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border front)
            return;

        var transform = EnsureMutableTransform(front);
        EnsureSlideTarget(front);
        var width = front.Tag is SlideTarget target ? target.ActionWidth : AccountActionWidth;

        if (!ReferenceEquals(_slideFront, front))
            CloseOpenSlide();

        _slideFront = front;
        _slideTransform = transform;
        _slideActionWidth = width;
        _slideStart = e.GetPosition(this);
        _slideOriginX = transform.X;
        _slideMoved = false;
        front.CaptureMouse();
        e.Handled = true;
    }

    private static TranslateTransform EnsureMutableTransform(Border front)
    {
        if (front.RenderTransform is TranslateTransform { IsFrozen: false } existing)
            return existing;

        var transform = new TranslateTransform();
        front.RenderTransform = transform;
        return transform;
    }

    private static void EnsureSlideTarget(Border front)
    {
        if (front.Tag is SlideTarget)
            return;
        if (front.DataContext is AccountCardRow)
            front.Tag = new SlideTarget(SlideKind.Account, null, AccountActionWidth);
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
            _slideMoved = true;

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
        var target = front.Tag as SlideTarget;

        front.ReleaseMouseCapture();
        e.Handled = true;

        if (!moved)
        {
            CloseOpenSlide();
            if (target?.Kind == SlideKind.Folder)
                SelectFolder(target.FolderId);
            else if (target?.Kind == SlideKind.Account && front.DataContext is AccountCardRow row)
                CopyAccountCode(row);
            return;
        }

        var open = transform.X <= -width * SlideOpenThreshold;
        transform.X = open ? -width : 0;
        if (!open)
        {
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
            _slideFront = null;
            _slideTransform = null;
        }
    }

    private void CloseOpenSlide()
    {
        if (_slideTransform is { IsFrozen: false })
            _slideTransform.X = 0;
        _slideFront = null;
        _slideTransform = null;
        _slideMoved = false;
    }

    private sealed record SlideTarget(SlideKind Kind, int? FolderId, double ActionWidth);

    private enum SlideKind
    {
        Folder,
        Account
    }
}

public sealed class AccountCardRow : INotifyPropertyChanged
{
    public required Account Account { get; set; }
    public int Id { get; init; }
    public string ServiceName { get; init; } = string.Empty;
    public string Username { get; init; } = string.Empty;
    public string Initial { get; init; } = "?";
    public Brush InitialBrush { get; init; } = Brushes.Gray;
    public int Period { get; init; } = 30;

    private string _displayCode = string.Empty;
    public string DisplayCode
    {
        get => _displayCode;
        set => Set(ref _displayCode, value);
    }

    public string RawCode { get; set; } = string.Empty;

    private double _secondsRemaining;
    public double SecondsRemaining
    {
        get => _secondsRemaining;
        set
        {
            if (Math.Abs(_secondsRemaining - value) < 0.001)
                return;
            _secondsRemaining = value;
            OnPropertyChanged(nameof(SecondsRemaining));
        }
    }

    private bool _isFavorite;
    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (!Set(ref _isFavorite, value))
                return;
            OnPropertyChanged(nameof(Star));
            OnPropertyChanged(nameof(StarBrush));
        }
    }

    public string Star => IsFavorite ? "★" : "☆";
    public Brush StarBrush => IsFavorite ? AccountEdits.BrushFromHex("#F5B301") : AccountEdits.BrushFromHex("#9CA3AF");

    public static AccountCardRow From(Account account)
    {
        return new AccountCardRow
        {
            Account = account,
            Id = account.Id,
            ServiceName = account.ServiceName,
            Username = account.Username,
            Initial = AccountEdits.Initial(account.ServiceName),
            InitialBrush = AccountEdits.InitialBrush(account.ServiceName),
            Period = account.Period <= 0 ? 30 : account.Period,
            IsFavorite = account.IsFavorite
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged(string? name)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
