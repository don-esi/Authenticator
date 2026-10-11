using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using Authenticator.Data.Models;
using Authenticator.Logging;
using Authenticator.Services;

namespace Authenticator;

public partial class MainWindow : Window
{
    private readonly DatabaseService _database = new();
    private readonly TotpService _totp = new();
    private readonly ObservableCollection<AccountRow> _rows = new();
    private readonly DispatcherTimer _timer;

    public MainWindow()
    {
        InitializeComponent();
        AccountList.ItemsSource = _rows;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => RefreshCodes();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await LoadAccountsAsync();
            RefreshCodes();
            _timer.Start();
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Failed to load accounts.", nameof(MainWindow));
            MessageBox.Show(this, ex.Message, "Authenticator", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _timer.Stop();
    }

    private async void AddButton_Click(object sender, RoutedEventArgs e)
    {
        LoggingConfiguration.LogInfo("Add account dialog requested.", nameof(MainWindow));
        var dialog = new AddAccountWindow { Owner = this };
        var result = dialog.ShowDialog();
        LoggingConfiguration.LogInfo($"Add account dialog closed. Result={result}.", nameof(MainWindow));
        if (result != true || dialog.AccountToSave is null)
            return;

        try
        {
            await _database.AddAccountAsync(dialog.AccountToSave);
            await LoadAccountsAsync();
            StatusText.Text = $"Added {dialog.AccountToSave.ServiceName}.";
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Failed to add account.", nameof(MainWindow));
            MessageBox.Show(this, ex.Message, "Add account", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (AccountList.SelectedItem is not AccountRow row)
        {
            StatusText.Text = "Select an account to delete.";
            return;
        }

        var confirm = MessageBox.Show(
            this,
            $"Delete {row.ServiceName} ({row.Username})?",
            "Delete account",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
            return;

        try
        {
            await _database.DeleteAccountAsync(row.Id);
            await LoadAccountsAsync();
            StatusText.Text = $"Deleted {row.ServiceName}.";
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, $"Failed to delete account id {row.Id}.", nameof(MainWindow));
            MessageBox.Show(this, ex.Message, "Delete account", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (AccountList.SelectedItem is not AccountRow row || string.IsNullOrWhiteSpace(row.Code) || row.Code == "------")
        {
            StatusText.Text = "Select an account with a code to copy.";
            return;
        }

        Clipboard.SetText(row.Code);
        StatusText.Text = $"Copied {row.ServiceName} code.";
    }

    private async Task LoadAccountsAsync()
    {
        var selectedId = (AccountList.SelectedItem as AccountRow)?.Id;
        var accounts = await _database.GetAllAccountsAsync();

        _rows.Clear();
        foreach (var account in accounts)
        {
            _rows.Add(new AccountRow
            {
                Id = account.Id,
                ServiceName = account.ServiceName,
                Username = account.Username,
                Account = account
            });
        }

        if (selectedId is int id)
            AccountList.SelectedItem = _rows.FirstOrDefault(row => row.Id == id);

        EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshCodes();
    }

    private void RefreshCodes()
    {
        foreach (var row in _rows)
        {
            try
            {
                row.Code = TotpService.GenerateCode(row.Account);
                row.SecondsRemaining = _totp.GetSecondsRemaining(row.Account);
            }
            catch (Exception ex)
            {
                row.Code = "------";
                row.SecondsRemaining = 0;
                LoggingConfiguration.LogException(ex, $"Failed to refresh code for account id {row.Id}.", nameof(MainWindow));
            }
        }
    }

    private sealed class AccountRow : INotifyPropertyChanged
    {
        public int Id { get; init; }
        public string ServiceName { get; init; } = string.Empty;
        public string Username { get; init; } = string.Empty;
        public required Account Account { get; init; }

        private string _code = string.Empty;
        public string Code
        {
            get => _code;
            set
            {
                if (_code == value)
                    return;
                _code = value;
                OnPropertyChanged();
            }
        }

        private int _secondsRemaining;
        public int SecondsRemaining
        {
            get => _secondsRemaining;
            set
            {
                if (_secondsRemaining == value)
                    return;
                _secondsRemaining = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
