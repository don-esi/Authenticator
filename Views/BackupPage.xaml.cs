using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Authenticator.Data.Models;
using Authenticator.Logging;
using Authenticator.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;

namespace Authenticator.Views;

public sealed partial class BackupPage : Page
{
    private readonly DatabaseService _databaseService = new();
    private List<BackupAccountDisplay> _accounts = [];
    private int? _requestedAccountId;

    public BackupPage()
    {
        LoggingConfiguration.LogInfo("BackupPage initialization started.", nameof(BackupPage));
        InitializeComponent();
        Loaded += BackupPage_Loaded;
        LoggingConfiguration.LogInfo("BackupPage initialization completed.", nameof(BackupPage));
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is int accountId)
        {
            _requestedAccountId = accountId;
            LoggingConfiguration.LogInfo($"BackupPage received navigation context for account id {accountId}.", nameof(BackupPage));
        }
        else
        {
            _requestedAccountId = null;
        }
    }

    private async void BackupPage_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        var accounts = await _databaseService.GetAllAccountsAsync();
        _accounts = accounts
            .Where(a => a.BackupCodes.Count > 0)
            .Select(a => new BackupAccountDisplay(
                a.Id,
                a.ServiceName,
                a.Username,
                a.BackupCodes.Count(bc => !bc.IsDeleted && !bc.IsUsed),
                a.BackupCodes.Count(bc => !bc.IsDeleted && bc.IsUsed),
                a.BackupCodes
                    .Where(bc => !bc.IsDeleted)
                    .OrderBy(bc => bc.CreatedAt)
                    .Select(bc => new BackupCodeDisplay(bc.Id, bc.Code, bc.IsUsed, bc.UsedAt))
                    .ToList()))
            .OrderBy(a => a.DisplayName)
            .ToList();

        AccountsListView.ItemsSource = _accounts;
        AccountsCountTextBlock.Text = _accounts.Count.ToString();
        AvailableCodesTextBlock.Text = _accounts.Sum(a => a.AvailableCount).ToString();
        UsedCodesTextBlock.Text = _accounts.Sum(a => a.UsedCount).ToString();

        if (_accounts.Count > 0)
        {
            if (_requestedAccountId.HasValue)
            {
                var matchedAccount = _accounts.FirstOrDefault(a => a.AccountId == _requestedAccountId.Value);
                if (matchedAccount is not null)
                {
                    AccountsListView.SelectedItem = matchedAccount;
                }
                else
                {
                    AccountsListView.SelectedIndex = 0;
                }
            }
            else
            {
                AccountsListView.SelectedIndex = 0;
            }
        }
        else
        {
            BackupCodesListView.ItemsSource = Array.Empty<BackupCodeDisplay>();
            SelectedAccountTitleTextBlock.Text = "Backup Codes";
            SelectedAccountSubtitleTextBlock.Text = "No backup codes have been saved yet.";
        }
    }

    private void AccountsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AccountsListView.SelectedItem is not BackupAccountDisplay selectedAccount)
        {
            return;
        }

        SelectedAccountTitleTextBlock.Text = selectedAccount.DisplayName;
        SelectedAccountSubtitleTextBlock.Text = selectedAccount.Subtitle;
        BackupCodesListView.ItemsSource = selectedAccount.Codes;
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await LoadAsync();
    }

    private void CopyCodeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string code } && !string.IsNullOrWhiteSpace(code))
        {
            var dataPackage = new DataPackage();
            dataPackage.SetText(code);
            Clipboard.SetContent(dataPackage);
        }
    }

    private async void MarkUsedButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int backupCodeId })
        {
            return;
        }

        await _databaseService.MarkBackupCodeAsUsedAsync(backupCodeId);
        await LoadAsync();
    }

    private sealed record BackupAccountDisplay(int AccountId, string DisplayName, string Subtitle, int AvailableCount, int UsedCount, List<BackupCodeDisplay> Codes)
    {
        public string Summary => $"{AvailableCount} available • {UsedCount} used";
    }

    private sealed record BackupCodeDisplay(int Id, string Code, bool IsUsed, DateTime? UsedAt)
    {
        public string StatusText => IsUsed
            ? $"Used{(UsedAt.HasValue ? $" on {UsedAt.Value:g}" : string.Empty)}"
            : "Available";

        public bool CanMarkUsed => !IsUsed;
    }
}
