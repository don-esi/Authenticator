using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Authenticator.Data.Models;
using Authenticator.Logging;
using Authenticator.Services;

namespace Authenticator;

public partial class CodesView : UserControl
{
    private readonly DatabaseService _database = new();
    private readonly TotpService _totp = new();
    private readonly DispatcherTimer _timer;
    private readonly ObservableCollection<AccountCardRow> _rows = new();
    private List<Account> _accounts = [];
    private List<Folder> _folders = [];
    private int? _folderId;
    private int _refreshSeq;
    private bool _refreshing;

    public CodesView()
    {
        InitializeComponent();
        AccountList.ItemsSource = _rows;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => RefreshCodes();
    }

    public async Task ReloadAsync()
    {
        try
        {
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
            EmptyText.Visibility = Visibility.Visible;
        }
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

            var codes = await _database.GetTotpCodesAsync(rows.Select(row => row.Id).ToList());
            if (seq != _refreshSeq)
                return;

            foreach (var row in rows)
            {
                if (!codes.TryGetValue(row.Id, out var code) || string.IsNullOrWhiteSpace(code))
                {
                    row.RawCode = string.Empty;
                    row.DisplayCode = "------";
                    row.SecondsRemaining = 0;
                    continue;
                }

                row.RawCode = code;
                row.DisplayCode = AccountEdits.FormatCode(code);
                row.SecondsRemaining = _totp.GetSecondsRemaining(row.Account);
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

    private async void CodesView_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            await ReloadAsync();
            _timer.Start();
        }
        else
        {
            _timer.Stop();
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
        if (SearchBox is null || ChipPanel is null || EmptyText is null || DeleteFolderButton is null)
            return;

        var query = SearchBox.Text.Trim();
        var filtered = _accounts.Where(account =>
        {
            if (_folderId is int folderId && account.FolderId != folderId)
                return false;
            if (query.Length == 0)
                return true;
            return account.ServiceName.Contains(query, StringComparison.OrdinalIgnoreCase)
                   || account.Username.Contains(query, StringComparison.OrdinalIgnoreCase);
        })
        .OrderByDescending(account => account.IsFavorite)
        .ThenBy(account => account.ServiceName, StringComparer.OrdinalIgnoreCase)
        .ThenBy(account => account.Username, StringComparer.OrdinalIgnoreCase)
        .ToList();

        _rows.Clear();
        foreach (var account in filtered)
            _rows.Add(AccountCardRow.From(account));

        var noFilters = query.Length == 0 && _folderId is null;
        EmptyText.Text = _accounts.Count == 0 && noFilters ? "No accounts yet." : "No matching accounts.";
        EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DeleteFolderButton.Visibility = _folderId is null ? Visibility.Collapsed : Visibility.Visible;
        BuildChips();
    }

    private void BuildChips()
    {
        ChipPanel.Children.Clear();
        ChipPanel.Children.Add(MakeChip("All", _accounts.Count, _folderId is null, null, null));
        foreach (var folder in _folders.OrderBy(folder => folder.Name, StringComparer.OrdinalIgnoreCase))
        {
            var count = _accounts.Count(account => account.FolderId == folder.Id);
            ChipPanel.Children.Add(MakeChip(folder.Name, count, _folderId == folder.Id, folder.Id, folder.Color));
        }
    }

    private Button MakeChip(string label, int count, bool selected, int? folderId, string? color)
    {
        var background = selected
            ? "#0078D4"
            : string.IsNullOrWhiteSpace(color) ? "#F3F4F6" : color;
        var foreground = selected ? "White" : "#1F2937";
        var button = new Button
        {
            Content = $"{label}  {count}",
            Tag = folderId,
            Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(14, 8, 14, 8),
            Foreground = AccountEdits.BrushFromHex(foreground == "White" ? "#FFFFFF" : foreground),
            Background = AccountEdits.BrushFromHex(background),
            BorderThickness = new Thickness(selected && color is not null ? 2 : 0),
            BorderBrush = AccountEdits.BrushFromHex("#1F2937"),
            Cursor = Cursors.Hand,
            FontWeight = FontWeights.SemiBold,
            Style = (Style)FindResource("ChipButton")
        };
        AutomationProperties.SetName(button, label);
        button.Click += FolderChip_Click;
        return button;
    }

    private void FolderChip_Click(object sender, RoutedEventArgs e)
    {
        _folderId = sender is Button { Tag: int id } ? id : null;
        ApplyFilter();
        RefreshCodes();
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new FolderWindow { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true)
            await ReloadAsync();
    }

    private async void DeleteFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_folderId is not int folderId)
            return;

        var folder = _folders.FirstOrDefault(item => item.Id == folderId);
        if (folder is null)
            return;

        var confirm = MessageBox.Show(
            Window.GetWindow(this),
            $"Delete folder \"{folder.Name}\"? Accounts in it stay in All.",
            "Delete folder",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
            return;

        try
        {
            await _database.DeleteFolderAsync(folderId);
            _folderId = null;
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, $"Failed to delete folder id {folderId}.", nameof(CodesView));
            MessageBox.Show(Window.GetWindow(this), ex.Message, "Delete folder", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void AddAccount_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AddAccountWindow(existing: null, defaultFolderId: _folderId) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true)
            await ReloadAsync();
    }

    private async void Card_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (IsInsideButton(e.OriginalSource as DependencyObject))
            return;
        if (sender is not FrameworkElement { DataContext: AccountCardRow row })
            return;

        var dialog = new AddAccountWindow(row.Account, row.Account.FolderId) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true)
            await ReloadAsync();
    }

    private async void Star_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AccountCardRow row })
            return;

        try
        {
            var updated = AccountEdits.CopyScalars(row.Account);
            updated.IsFavorite = !row.Account.IsFavorite;
            await _database.UpdateAccountAsync(updated);
            row.Account.IsFavorite = updated.IsFavorite;
            row.IsFavorite = updated.IsFavorite;
            ApplyFilter();
            RefreshCodes();
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, $"Failed to update favorite for account id {row.Id}.", nameof(CodesView));
            MessageBox.Show(Window.GetWindow(this), ex.Message, "Favorite", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AccountCardRow row } || string.IsNullOrWhiteSpace(row.RawCode))
        {
            StatusText.Text = "That account has no code to copy.";
            return;
        }

        Clipboard.SetText(row.RawCode);
        StatusText.Text = $"Copied {row.ServiceName}.";
    }

    private static bool IsInsideButton(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is Button)
                return true;
            source = VisualTreeHelper.GetParent(source);
        }

        return false;
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

    private int _secondsRemaining;
    public int SecondsRemaining
    {
        get => _secondsRemaining;
        set => Set(ref _secondsRemaining, value);
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
