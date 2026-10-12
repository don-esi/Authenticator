using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using Authenticator.Data;
using Authenticator.Data.Models;
using Authenticator.Logging;
using Authenticator.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Win32;

namespace Authenticator;

public partial class SettingsView : UserControl
{
    private static readonly (int Seconds, string Label)[] LockOptions =
    [
        (30, "30 Seconds"),
        (60, "1 Minute"),
        (120, "2 Minutes"),
        (180, "3 Minutes"),
        (240, "4 Minutes"),
        (300, "5 Minutes"),
        (0, "Never")
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly DatabaseService _database = new();
    private readonly SettingsService _settings = new();
    private bool _loading = true;
    private int _timeoutSeconds;

    public event EventHandler<int>? AutoLockChanged;
    public event EventHandler? DataChanged;

    public SettingsView()
    {
        InitializeComponent();
    }

    public async Task ReloadAsync()
    {
        _loading = true;
        try
        {
            AccountCountText.Text = (await _database.GetAccountCountAsync()).ToString();
            FolderCountText.Text = (await _database.GetAllFoldersAsync()).Count.ToString();
            var settings = await _settings.GetSettingsAsync();
            _timeoutSeconds = settings.AutoLockTimeout;
            BuildLockOptions();
            TransferStatus.Text = string.Empty;
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Failed to load settings.", nameof(SettingsView));
            TransferStatus.Text = ex.Message;
        }
        finally
        {
            _loading = false;
        }
    }

    private void BuildLockOptions()
    {
        AutoLockPanel.Children.Clear();
        var known = LockOptions.Select(option => option.Seconds).ToHashSet();
        var options = LockOptions.ToList();
        if (!known.Contains(_timeoutSeconds))
            options.Insert(0, (_timeoutSeconds, $"{_timeoutSeconds} seconds"));

        foreach (var option in options)
        {
            var selected = option.Seconds == _timeoutSeconds;
            var button = new Button
            {
                Content = selected ? $"{option.Label}    ✓" : option.Label,
                Tag = option.Seconds,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 2, 0, 2),
                Style = (Style)FindResource("SecondaryButton"),
                Background = System.Windows.Media.Brushes.Transparent,
                FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal
            };
            button.Click += AutoLock_Click;
            AutoLockPanel.Children.Add(button);
        }
    }

    private async void AutoLock_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not Button { Tag: int seconds } || seconds == _timeoutSeconds)
            return;

        try
        {
            await _settings.SetAutoLockTimeoutAsync(seconds);
            _timeoutSeconds = seconds;
            BuildLockOptions();
            AutoLockChanged?.Invoke(this, seconds);
            LoggingConfiguration.LogInfo($"Auto-lock timeout set to {seconds} second(s).", nameof(SettingsView));
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Failed to save auto-lock timeout.", nameof(SettingsView));
            TransferStatus.Text = ex.Message;
        }
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "JSON files (*.json)|*.json",
            FileName = "authenticator-export.json"
        };
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var export = await BuildExportAsync();
            await File.WriteAllTextAsync(dialog.FileName, JsonSerializer.Serialize(export, JsonOptions));
            TransferStatus.Text = $"Exported {export.Accounts.Count} accounts and {export.Folders.Count} folders.";
            LoggingConfiguration.LogInfo($"Exported {export.Accounts.Count} account(s) and {export.Folders.Count} folder(s).", nameof(SettingsView));
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Export failed.", nameof(SettingsView));
            TransferStatus.Text = ex.Message;
        }
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "JSON files (*.json)|*.json"
        };
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var json = await File.ReadAllTextAsync(dialog.FileName);
            var file = JsonSerializer.Deserialize<AuthenticatorExport>(json, JsonOptions);
            if (file?.Accounts is null)
            {
                TransferStatus.Text = "The file has no accounts.";
                return;
            }

            var result = await ImportAsync(file);
            TransferStatus.Text = $"Imported {result.Accounts} accounts and {result.Folders} folders. Skipped {result.Skipped} duplicates.";
            LoggingConfiguration.LogInfo($"Imported {result.Accounts} account(s) and {result.Folders} folder(s). Skipped {result.Skipped} duplicate(s).", nameof(SettingsView));
            await ReloadAsync();
            DataChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Import failed.", nameof(SettingsView));
            TransferStatus.Text = "Could not read that file.";
        }
    }

    private async Task<AuthenticatorExport> BuildExportAsync()
    {
        var folders = await _database.GetAllFoldersAsync();
        var accounts = await _database.GetAllAccountsAsync();
        return new AuthenticatorExport
        {
            Folders = folders.Select(folder => new FolderExport
            {
                Name = folder.Name,
                Color = folder.Color
            }).ToList(),
            Accounts = accounts.Select(account => new AccountExport
            {
                ServiceName = account.ServiceName,
                ServiceUrl = account.ServiceUrl,
                Username = account.Username,
                SecretKey = account.SecretKey,
                Algorithm = account.Algorithm,
                Digits = account.Digits,
                Period = account.Period,
                FolderName = account.Folder?.Name,
                IsFavorite = account.IsFavorite,
                BackupCodes = account.BackupCodes
                    .Where(code => !code.IsDeleted)
                    .Select(code => new BackupCodeExport { Code = code.Code, IsUsed = code.IsUsed })
                    .ToList()
            }).ToList()
        };
    }

    private async Task<(int Accounts, int Folders, int Skipped)> ImportAsync(AuthenticatorExport file)
    {
        var existingFolders = await _database.GetAllFoldersAsync();
        var folderIds = existingFolders.ToDictionary(folder => folder.Name, folder => folder.Id, StringComparer.OrdinalIgnoreCase);
        var foldersAdded = 0;

        foreach (var folder in file.Folders ?? [])
        {
            if (string.IsNullOrWhiteSpace(folder.Name) || folderIds.ContainsKey(folder.Name))
                continue;

            var created = await _database.AddFolderAsync(new Folder
            {
                Name = folder.Name.Trim(),
                Color = string.IsNullOrWhiteSpace(folder.Color) ? "#4ECDC4" : folder.Color.Trim()
            });
            folderIds[created.Name] = created.Id;
            foldersAdded++;
        }

        var existing = await _database.GetAllAccountsAsync();
        var known = existing
            .Select(account => Key(account.ServiceName, account.Username, account.SecretKey))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var accountsAdded = 0;
        var skipped = 0;
        foreach (var account in file.Accounts)
        {
            var secret = AccountEdits.NormalizeSecret(account.SecretKey ?? string.Empty);
            var service = account.ServiceName?.Trim() ?? string.Empty;
            var username = account.Username?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(service) || string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(secret))
            {
                skipped++;
                continue;
            }

            if (!known.Add(Key(service, username, secret)))
            {
                skipped++;
                continue;
            }

            int? folderId = null;
            if (!string.IsNullOrWhiteSpace(account.FolderName) && folderIds.TryGetValue(account.FolderName, out var id))
                folderId = id;

            var saved = await _database.AddAccountAsync(new Account
            {
                ServiceName = service,
                ServiceUrl = string.IsNullOrWhiteSpace(account.ServiceUrl) ? null : account.ServiceUrl.Trim(),
                Username = username,
                SecretKey = secret,
                Algorithm = NormalizeAlgorithm(account.Algorithm),
                Digits = account.Digits == 8 ? 8 : 6,
                Period = NormalizePeriod(account.Period),
                FolderId = folderId,
                IsFavorite = account.IsFavorite
            });

            var codes = account.BackupCodes?
                .Where(code => !string.IsNullOrWhiteSpace(code.Code))
                .ToList();
            if (codes is { Count: > 0 })
                await AddImportedCodesAsync(saved.Id, codes);

            accountsAdded++;
        }

        return (accountsAdded, foldersAdded, skipped);
    }

    private static async Task AddImportedCodesAsync(int accountId, List<BackupCodeExport> codes)
    {
        await using var context = new AuthenticatorDbContext();
        foreach (var code in codes)
        {
            context.BackupCodes.Add(new BackupCode
            {
                AccountId = accountId,
                Code = code.Code.Trim(),
                IsUsed = code.IsUsed,
                CreatedAt = DateTime.UtcNow,
                UsedAt = code.IsUsed ? DateTime.UtcNow : null
            });
        }

        await context.SaveChangesAsync();
    }

    private static string Key(string service, string username, string secret)
        => $"{service.Trim()}|{username.Trim()}|{AccountEdits.NormalizeSecret(secret)}";

    private static string NormalizeAlgorithm(string? algorithm)
    {
        return algorithm?.Trim().ToUpperInvariant() switch
        {
            "SHA256" => "SHA256",
            "SHA512" => "SHA512",
            _ => "SHA1"
        };
    }

    private static int NormalizePeriod(int period)
        => period is 15 or 30 or 60 or 120 ? period : 30;
}

public sealed class AuthenticatorExport
{
    public List<FolderExport> Folders { get; set; } = [];
    public List<AccountExport> Accounts { get; set; } = [];
}

public sealed class FolderExport
{
    public string Name { get; set; } = string.Empty;
    public string Color { get; set; } = "#4ECDC4";
}

public sealed class AccountExport
{
    public string ServiceName { get; set; } = string.Empty;
    public string? ServiceUrl { get; set; }
    public string Username { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;
    public string Algorithm { get; set; } = "SHA1";
    public int Digits { get; set; } = 6;
    public int Period { get; set; } = 30;
    public string? FolderName { get; set; }
    public bool IsFavorite { get; set; }
    public List<BackupCodeExport> BackupCodes { get; set; } = [];
}

public sealed class BackupCodeExport
{
    public string Code { get; set; } = string.Empty;
    public bool IsUsed { get; set; }
}
