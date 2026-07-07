using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Authenticator.Data.Models;
using Authenticator.Logging;
using Authenticator.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Authenticator.ViewModels;

public partial class AddAccountViewModel : ObservableObject
{
    private readonly DatabaseService _databaseService;
#pragma warning disable S4487
    private readonly TotpService _totpService;
#pragma warning restore S4487

    public string ServiceName
    {
        get;
        set => SetProperty(ref field, value);
    } = string.Empty;

    public string ServiceUrl
    {
        get;
        set => SetProperty(ref field, value);
    } = string.Empty;

    public string Username
    {
        get;
        set => SetProperty(ref field, value);
    } = string.Empty;

    public string SecretKey
    {
        get;
        set => SetProperty(ref field, value);
    } = string.Empty;

    public string SelectedAlgorithm
    {
        get;
        set => SetProperty(ref field, value);
    } = "SHA1";

    public int SelectedDigits
    {
        get;
        set => SetProperty(ref field, value);
    } = 6;

    public int SelectedPeriod
    {
        get;
        set => SetProperty(ref field, value);
    } = 30;

    public bool ShowAdvancedOptions
    {
        get;
        set => SetProperty(ref field, value);
    }

    public bool EnableBackupCodes
    {
        get;
        set => SetProperty(ref field, value);
    }

    public string BackupCodesText
    {
        get;
        set => SetProperty(ref field, value);
    } = string.Empty;

    public bool IsSaving
    {
        get;
        set => SetProperty(ref field, value);
    }

    public List<Folder> FolderOptions
    {
        get;
        private set => SetProperty(ref field, value);
    } = [];

    public Folder? SelectedFolder
    {
        get;
        set => SetProperty(ref field, value);
    }

    public List<string> AlgorithmOptions { get; } = ["SHA1", "SHA256", "SHA512"];
    public List<int> DigitOptions { get; } = [6, 8];
    public List<int> PeriodOptions { get; } = [15, 30, 60, 120];

    public AddAccountViewModel(DatabaseService databaseService, TotpService totpService)
    {
        _databaseService = databaseService;
        _totpService = totpService;

        LoggingConfiguration.LogInfo("AddAccountViewModel initialized.", nameof(AddAccountViewModel));
    }

    public async Task LoadFoldersAsync()
    {
        FolderOptions = await _databaseService.GetAllFoldersAsync();
        LoggingConfiguration.LogInfo($"Loaded {FolderOptions.Count} folder option(s) for AddAccount.", nameof(AddAccountViewModel));
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        LoggingConfiguration.LogInfo("Save account workflow started.", nameof(AddAccountViewModel));

        if (!ValidateInput())
        {
            LoggingConfiguration.LogWarning("Save account aborted due to validation failure.", nameof(AddAccountViewModel));
            return;
        }

        IsSaving = true;
        try
        {
            var account = new Account
            {
                ServiceName = ServiceName.Trim(),
                ServiceUrl = string.IsNullOrWhiteSpace(ServiceUrl) ? null : ServiceUrl.Trim(),
                Username = Username.Trim(),
                SecretKey = SecretKey.Trim().Replace(" ", "").ToUpperInvariant(),
                Algorithm = SelectedAlgorithm,
                Digits = SelectedDigits,
                Period = SelectedPeriod,
                FolderId = SelectedFolder?.Id
            };

            await _databaseService.AddAccountAsync(account);
            LoggingConfiguration.LogInfo($"Account saved successfully with id {account.Id}.", nameof(AddAccountViewModel));

            // Add backup codes if enabled
            if (EnableBackupCodes && !string.IsNullOrWhiteSpace(BackupCodesText))
            {
                var codes = BackupCodesText
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(c => c.Trim())
                    .Where(c => !string.IsNullOrWhiteSpace(c))
                    .ToList();

                if (codes.Any())
                {
                    await _databaseService.AddBackupCodesAsync(account.Id, codes);
                    LoggingConfiguration.LogInfo($"Added {codes.Count} backup code(s) for account id {account.Id}.", nameof(AddAccountViewModel));
                }
            }

            // Navigate back (will be handled by view)
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Error while saving account.", nameof(AddAccountViewModel));
        }
        finally
        {
            IsSaving = false;
            LoggingConfiguration.LogDebug("Save account workflow completed.", nameof(AddAccountViewModel));
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        LoggingConfiguration.LogInfo("Cancel account creation requested.", nameof(AddAccountViewModel));
        // Navigate back (will be handled by view)
    }

    [RelayCommand]
    private async Task ReadBackupCodesFromFileAsync()
    {
        LoggingConfiguration.LogInfo("Reading backup codes from file requested.", nameof(AddAccountViewModel));

        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        picker.FileTypeFilter.Add(".txt");
        picker.FileTypeFilter.Add("*");

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSingleFileAsync();
        if (file != null)
        {
            BackupCodesText = await Windows.Storage.FileIO.ReadTextAsync(file);
            LoggingConfiguration.LogInfo($"Loaded backup codes from file '{file.Name}'.", nameof(AddAccountViewModel));
        }
        else
        {
            LoggingConfiguration.LogDebug("Backup code file selection canceled.", nameof(AddAccountViewModel));
        }
    }

    public void LoadFromQrResult(QrCodeResult qrResult)
    {
        if (qrResult is null)
            throw new ArgumentNullException(nameof(qrResult));

        ServiceName = string.Empty;

        if (qrResult.Issuer is not null)
            ServiceName = qrResult.Issuer;

        Username = qrResult.Account;
        SecretKey = qrResult.Secret;
        SelectedAlgorithm = qrResult.Algorithm;
        SelectedDigits = qrResult.Digits;
        SelectedPeriod = qrResult.Period;

        LoggingConfiguration.LogInfo($"Populated add-account form from QR result. Issuer='{ServiceName}', Account='{Username}', Algorithm='{SelectedAlgorithm}', Digits={SelectedDigits}, Period={SelectedPeriod}.", nameof(AddAccountViewModel));
    }

    private bool ValidateInput()
    {
        if (string.IsNullOrWhiteSpace(ServiceName))
        {
            LoggingConfiguration.LogWarning("Validation failed: ServiceName is required.", nameof(AddAccountViewModel));
            return false;
        }

        if (string.IsNullOrWhiteSpace(Username))
        {
            LoggingConfiguration.LogWarning("Validation failed: Username is required.", nameof(AddAccountViewModel));
            return false;
        }

        if (string.IsNullOrWhiteSpace(SecretKey))
        {
            LoggingConfiguration.LogWarning("Validation failed: SecretKey is required.", nameof(AddAccountViewModel));
            return false;
        }

        return true;
    }
}