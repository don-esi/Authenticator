using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Authenticator.Data.Models;
using Authenticator.Logging;
using Authenticator.Services;

namespace Authenticator;

public partial class FolderWindow : Window
{
    private readonly DatabaseService _database = new();
    private readonly Folder? _existing;
    private readonly List<FolderAccountPickRow> _allRows = [];
    private readonly ObservableCollection<FolderAccountPickRow> _visibleRows = new();
    private string _color = AccountEdits.FolderColors[0];
    private bool _busy;
    private bool _sortAscending = true;

    public FolderWindow(Folder? existing = null)
    {
        _existing = existing;
        InitializeComponent();
        SyncfusionBootstrap.ApplyTo(this);
        AccountList.ItemsSource = _visibleRows;

        if (existing is null)
        {
            NameBox.Text = "New Folder";
            return;
        }

        Title = "Edit Folder";
        TitleText.Text = "Edit Folder";
        NameBox.Text = existing.Name;
        _color = string.IsNullOrWhiteSpace(existing.Color) ? AccountEdits.FolderColors[0] : existing.Color;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateFolderPreview();
        BuildColors();
        await LoadAccountsAsync();
        NameBox.Focus();
        NameBox.SelectAll();
    }

    private async Task LoadAccountsAsync()
    {
        try
        {
            var accounts = await _database.GetAllAccountsAsync();
            _allRows.Clear();
            foreach (var account in accounts
                         .OrderBy(account => account.ServiceName, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(account => account.Username, StringComparer.OrdinalIgnoreCase))
            {
                _allRows.Add(new FolderAccountPickRow
                {
                    Account = account,
                    IsSelected = _existing is not null && account.FolderId == _existing.Id
                });
            }

            ApplyAccountFilter();
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Failed to load accounts for folder picker.", nameof(FolderWindow));
            ErrorText.Text = ex.Message;
        }
    }

    private void BuildColors()
    {
        ColorPanel.Children.Clear();
        foreach (var color in AccountEdits.FolderColors.Take(6))
        {
            var selected = string.Equals(color, _color, StringComparison.OrdinalIgnoreCase);
            var swatch = new Grid { Width = 24, Height = 24 };
            swatch.Children.Add(new Ellipse
            {
                Width = 24,
                Height = 24,
                Fill = AccountEdits.BrushFromHex(color)
            });
            if (selected)
            {
                swatch.Children.Add(new Ellipse
                {
                    Width = 16,
                    Height = 16,
                    Fill = Brushes.Transparent,
                    Stroke = Brushes.White,
                    StrokeThickness = 2,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

            var button = new Button
            {
                Width = 30,
                Height = 30,
                Margin = new Thickness(0, 0, 6, 0),
                Tag = color,
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                Style = (Style)FindResource("IconButton"),
                Content = swatch
            };
            AutomationProperties.SetName(button, $"Folder color {color}");
            button.Click += (_, _) =>
            {
                _color = color;
                UpdateFolderPreview();
                BuildColors();
            };
            ColorPanel.Children.Add(button);
        }
    }

    private void UpdateFolderPreview()
    {
        FolderPreviewIcon.Foreground = AccountEdits.BrushFromHex(_color);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var empty = string.IsNullOrEmpty(SearchBox.Text);
        SearchHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (SearchClearButton is not null)
            SearchClearButton.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        ApplyAccountFilter();
    }

    private void SearchClear_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";
        SearchBox.Focus();
        e.Handled = true;
    }

    private void Sort_Click(object sender, RoutedEventArgs e)
    {
        _sortAscending = !_sortAscending;
        ApplyAccountFilter();
    }

    private void ApplyAccountFilter()
    {
        var query = SearchBox?.Text.Trim() ?? string.Empty;
        IEnumerable<FolderAccountPickRow> rows = _allRows;
        if (query.Length > 0)
        {
            rows = rows.Where(row =>
                row.ServiceName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || row.Username.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        rows = _sortAscending
            ? rows.OrderBy(row => row.ServiceName, StringComparer.OrdinalIgnoreCase)
                  .ThenBy(row => row.Username, StringComparer.OrdinalIgnoreCase)
            : rows.OrderByDescending(row => row.ServiceName, StringComparer.OrdinalIgnoreCase)
                  .ThenByDescending(row => row.Username, StringComparer.OrdinalIgnoreCase);

        _visibleRows.Clear();
        foreach (var row in rows)
            _visibleRows.Add(row);

        EmptyPickText.Visibility = _allRows.Count == 0
            ? Visibility.Visible
            : _visibleRows.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        EmptyPickText.Text = _allRows.Count == 0 ? "No accounts yet." : "No matching accounts.";
    }

    private void AccountRow_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FolderAccountPickRow row })
            row.IsSelected = !row.IsSelected;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;

        var name = NameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            ErrorText.Text = "Folder name is required.";
            return;
        }

        _busy = true;
        SaveButton.IsEnabled = false;
        try
        {
            Folder folder;
            if (_existing is null)
            {
                folder = await _database.AddFolderAsync(new Folder
                {
                    Name = name,
                    Color = _color
                });
                LoggingConfiguration.LogInfo("Folder saved.", nameof(FolderWindow));
            }
            else
            {
                _existing.Name = name;
                _existing.Color = _color;
                await _database.UpdateFolderAsync(_existing);
                folder = _existing;
                LoggingConfiguration.LogInfo($"Folder id {_existing.Id} updated.", nameof(FolderWindow));
            }

            foreach (var row in _allRows)
            {
                var shouldBelong = row.IsSelected;
                var belongs = row.Account.FolderId == folder.Id;
                if (shouldBelong == belongs)
                    continue;

                await _database.MoveAccountToFolderAsync(row.Account.Id, shouldBelong ? folder.Id : null);
            }

            DialogResult = true;
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Failed to save folder.", nameof(FolderWindow));
            ErrorText.Text = ex.Message;
            _busy = false;
            SaveButton.IsEnabled = true;
        }
    }
}

public sealed class FolderAccountPickRow : INotifyPropertyChanged
{
    public required Account Account { get; init; }
    public string ServiceName => Account.ServiceName;
    public string Username => Account.Username;
    public string Initial => AccountEdits.Initial(Account.ServiceName);
    public Brush InitialBrush => AccountEdits.InitialBrush(Account.ServiceName);

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
                return;
            _isSelected = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CheckedVisibility));
            OnPropertyChanged(nameof(UncheckedVisibility));
        }
    }

    public Visibility CheckedVisibility => IsSelected ? Visibility.Visible : Visibility.Collapsed;
    public Visibility UncheckedVisibility => IsSelected ? Visibility.Collapsed : Visibility.Visible;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
