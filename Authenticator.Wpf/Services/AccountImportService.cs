using Authenticator.Data;
using Authenticator.Data.Models;
using OtpNet;

namespace Authenticator.Services;

public sealed class AuthenticatorExport
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; set; } = CurrentFormatVersion;
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

/// <summary>Shared JSON / Google migration import used by Settings and first-run.</summary>
public sealed class AccountImportService
{
    private readonly DatabaseService _database = new();

    public async Task<(int Accounts, int Folders, int Duplicates, int Invalid)> ImportAsync(AuthenticatorExport file)
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
        var duplicates = 0;
        var invalid = 0;
        foreach (var account in file.Accounts)
        {
            var secret = AccountEdits.NormalizeSecret(account.SecretKey ?? string.Empty);
            var service = account.ServiceName?.Trim() ?? string.Empty;
            var username = account.Username?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(service) || string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(secret)
                || !IsValidBase32(secret))
            {
                invalid++;
                continue;
            }

            if (!known.Add(Key(service, username, secret)))
            {
                duplicates++;
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

        return (accountsAdded, foldersAdded, duplicates, invalid);
    }

    public async Task<(int Accounts, int Duplicates, int Unsupported)> ImportGoogleAccountsAsync(GoogleMigrationResult payload)
    {
        var existing = await _database.GetAllAccountsAsync();
        var known = existing
            .Select(account => Key(account.ServiceName, account.Username, account.SecretKey))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var added = 0;
        var duplicates = 0;
        foreach (var account in payload.Accounts)
        {
            var service = account.ServiceName.Trim();
            var username = account.Username.Trim();
            var secret = AccountEdits.NormalizeSecret(account.SecretKey);
            if (string.IsNullOrWhiteSpace(service) || string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(secret)
                || !IsValidBase32(secret))
            {
                continue;
            }

            if (!known.Add(Key(service, username, secret)))
            {
                duplicates++;
                continue;
            }

            await _database.AddAccountAsync(new Account
            {
                ServiceName = service,
                Username = username,
                SecretKey = secret,
                Algorithm = NormalizeAlgorithm(account.Algorithm),
                Digits = account.Digits == 8 ? 8 : 6,
                Period = NormalizePeriod(account.Period)
            });
            added++;
        }

        var rejected = payload.Accounts.Count - added - duplicates;
        return (added, duplicates, payload.UnsupportedCount + Math.Max(0, rejected));
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

    private static bool IsValidBase32(string secret)
    {
        try
        {
            _ = Base32Encoding.ToBytes(secret);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static string NormalizeAlgorithm(string? algorithm)
    {
        return algorithm?.Trim().ToUpperInvariant() switch
        {
            "SHA256" => "SHA256",
            "SHA512" => "SHA512",
            _ => "SHA1"
        };
    }

    public static int NormalizePeriod(int period)
        => period is 15 or 30 or 60 or 120 ? period : 30;
}
