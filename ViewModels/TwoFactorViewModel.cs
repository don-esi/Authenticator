using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Authenticator.Data.Models;
using Authenticator.Logging;
using Authenticator.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;

namespace Authenticator.ViewModels;

/// <summary>
/// ViewModel for the main 2FA accounts page with live TOTP updates
/// </summary>
public partial class TwoFactorViewModel : ObservableObject
{
    private readonly DatabaseService _databaseService;
    private readonly TotpService _totpService;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly DispatcherQueueTimer _updateTimer;

    public ObservableCollection<AccountItemViewModel> Accounts { get; set; } = [];

    public ObservableCollection<AccountItemViewModel> FilteredAccounts { get; set; } = [];

    public ObservableCollection<Folder> Folders { get; set; } = [];

    public Folder? SelectedFolder
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                ApplyFilterAndSort();
            }
        }
    }

    public string SearchText
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                ApplyFilterAndSort();
            }
        }
    } = string.Empty;

    public string SortOption
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                ApplyFilterAndSort();
            }
        }
    } = "Latest First";

    public bool IsGridView
    {
        get;
        set => SetProperty(ref field, value);
    }

    public TwoFactorViewModel(DatabaseService databaseService, TotpService totpService, DispatcherQueue dispatcherQueue)
    {
        _databaseService = databaseService;
        _totpService = totpService;
        _dispatcherQueue = dispatcherQueue;

        // Create timer for live TOTP updates (1 second interval)
        _updateTimer = _dispatcherQueue.CreateTimer();
        _updateTimer.Interval = TimeSpan.FromSeconds(1);
        _updateTimer.Tick += UpdateTimer_Tick;

        LoggingConfiguration.LogInfo("TwoFactorViewModel initialized.", nameof(TwoFactorViewModel));
    }

    public async Task LoadAccountsAsync()
    {
        LoggingConfiguration.LogInfo("Loading accounts and folders for TwoFactor view.", nameof(TwoFactorViewModel));

        var accounts = await _databaseService.GetAllAccountsAsync();
        var folders = await _databaseService.GetAllFoldersAsync();

        Accounts.Clear();
        foreach (var account in accounts)
        {
            Accounts.Add(new AccountItemViewModel(account));
        }

        Folders.Clear();
        foreach (var folder in folders)
        {
            Folders.Add(folder);
        }

        ApplyFilterAndSort();
        UpdateAllCodes();

        // Start timer if not running
        if (!_updateTimer.IsRunning)
        {
            _updateTimer.Start();
            LoggingConfiguration.LogDebug("TwoFactor update timer started from LoadAccountsAsync.", nameof(TwoFactorViewModel));
        }

        LoggingConfiguration.LogInfo($"TwoFactor data loaded. Accounts={Accounts.Count}, Folders={Folders.Count}, Filtered={FilteredAccounts.Count}.", nameof(TwoFactorViewModel));
    }

    private void ApplyFilterAndSort()
    {
        var filtered = Accounts.AsEnumerable();

        // Apply search filter
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var search = SearchText.ToLowerInvariant();
            filtered = filtered.Where(a =>
                a.ServiceName.ToLowerInvariant().Contains(search) ||
                a.Username.ToLowerInvariant().Contains(search));
        }

        // Apply folder filter
        if (SelectedFolder != null)
        {
            filtered = filtered.Where(a => a.Account.FolderId == SelectedFolder.Id);
        }

        // Apply sort
        filtered = SortOption switch
        {
            "Latest First" => filtered.OrderByDescending(a => a.Account.CreatedAt),
            "Earliest First" => filtered.OrderBy(a => a.Account.CreatedAt),
            "Service A→Z" => filtered.OrderBy(a => a.ServiceName),
            "Service Z→A" => filtered.OrderByDescending(a => a.ServiceName),
            "Account A→Z" => filtered.OrderBy(a => a.Username),
            "Account Z→A" => filtered.OrderByDescending(a => a.Username),
            _ => filtered
        };

        FilteredAccounts.Clear();
        foreach (var account in filtered)
        {
            FilteredAccounts.Add(account);
        }

        LoggingConfiguration.LogDebug($"Applied filter/sort. Search='{SearchText}', FolderId={(SelectedFolder?.Id.ToString() ?? "None")}, Sort='{SortOption}', FilteredCount={FilteredAccounts.Count}.", nameof(TwoFactorViewModel));
    }

    private void UpdateTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        UpdateAllCodes();
    }

    private void UpdateAllCodes()
    {
        foreach (var accountVM in Accounts)
        {
            try
            {
                var code = _totpService.GenerateCode(accountVM.Account, accountVM.Account.Digits);
                var progress = _totpService.GetProgress(accountVM.Account);
                var secondsRemaining = _totpService.GetSecondsRemaining(accountVM.Account);

                accountVM.UpdateCode(code, progress, secondsRemaining);
            }
            catch (Exception ex)
            {
                LoggingConfiguration.LogException(ex, $"Error updating TOTP code for account id {accountVM.Id}.", nameof(TwoFactorViewModel));
                accountVM.UpdateCode("ERROR", 0, 0);
            }
        }
    }

    [RelayCommand]
    private void ToggleViewMode()
    {
        IsGridView = !IsGridView;
        LoggingConfiguration.LogInfo($"View mode changed. IsGridView={IsGridView}.", nameof(TwoFactorViewModel));
    }

    [RelayCommand]
    private async Task AddAccountAsync()
    {
        // Navigate to Add Account page (will be implemented in navigation)
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task EditAccountAsync(AccountItemViewModel? account)
    {
        if (account == null) return;
        // Navigate to Edit Account page
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task DeleteAccountAsync(AccountItemViewModel? account)
    {
        if (account == null) return;

        LoggingConfiguration.LogInfo($"Soft deleting account id {account.Id} from TwoFactor view.", nameof(TwoFactorViewModel));
        await _databaseService.DeleteAccountAsync(account.Id, softDelete: true);
        await LoadAccountsAsync();
    }

    [RelayCommand]
    private async Task CopyCodeAsync(AccountItemViewModel? account)
    {
        if (account == null || string.IsNullOrEmpty(account.CurrentCode)) return;

        var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
        dataPackage.SetText(account.CurrentCode);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);

        LoggingConfiguration.LogDebug($"Copied TOTP code to clipboard for account id {account.Id}.", nameof(TwoFactorViewModel));
        await Task.CompletedTask;
    }

    [RelayCommand]
#pragma warning disable S4144
    private async Task ShowBackupCodesAsync(AccountItemViewModel? account)
#pragma warning restore S4144
    {
        if (account == null) return;
        // Navigate to Backup Codes page
        await Task.CompletedTask;
    }

    [RelayCommand]
    private void SelectFolder(Folder? folder)
    {
        SelectedFolder = folder;
        LoggingConfiguration.LogDebug($"Folder filter selected: {(folder == null ? "None" : $"{folder.Name} (Id={folder.Id})")}.", nameof(TwoFactorViewModel));
    }

    [RelayCommand]
    private void ClearFolderFilter()
    {
        SelectedFolder = null;
        LoggingConfiguration.LogDebug("Folder filter cleared.", nameof(TwoFactorViewModel));
    }

    [RelayCommand]
    private async Task CreateFolderAsync()
    {
        // Navigate to Create Folder page
        await Task.CompletedTask;
    }

    public void StopTimer()
    {
        if (_updateTimer.IsRunning)
        {
            _updateTimer.Stop();
            LoggingConfiguration.LogDebug("TwoFactor update timer stopped.", nameof(TwoFactorViewModel));
        }
    }

    public void StartTimer()
    {
        if (!_updateTimer.IsRunning)
        {
            _updateTimer.Start();
            LoggingConfiguration.LogDebug("TwoFactor update timer started.", nameof(TwoFactorViewModel));
        }
    }
}