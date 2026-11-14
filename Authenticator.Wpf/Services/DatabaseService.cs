using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Authenticator.Data;
using Authenticator.Data.Models;
using Authenticator.Logging;
using Microsoft.EntityFrameworkCore;

namespace Authenticator.Services;

/// <summary>
/// Service for database operations on accounts, folders, and backup codes
/// </summary>
public class DatabaseService
{
    private readonly ISecretProtector _secrets;

    public DatabaseService(ISecretProtector? secrets = null)
    {
        _secrets = secrets ?? new SecretProtector();
    }

    // Accounts

    public async Task<List<Account>> GetAllAccountsAsync(bool includeDeleted = false)
    {
        LoggingConfiguration.LogDebug($"Loading accounts. IncludeDeleted={includeDeleted}.", nameof(DatabaseService));

        using var context = new AuthenticatorDbContext();
        var query = context.Accounts
            .Include(a => a.Folder)
            .Include(a => a.BackupCodes)
            .AsQueryable();

        if (!includeDeleted)
            query = query.Where(a => !a.IsDeleted);

        var accounts = await query.OrderBy(a => a.ServiceName).ToListAsync();
        foreach (var account in accounts)
            RevealAccountSecrets(account);

        LoggingConfiguration.LogInfo($"Loaded {accounts.Count} account(s).", nameof(DatabaseService));
        return accounts;
    }

    public async Task<Account?> GetAccountByIdAsync(int id)
    {
        LoggingConfiguration.LogDebug($"Loading account by id: {id}.", nameof(DatabaseService));

        using var context = new AuthenticatorDbContext();
        var account = await context.Accounts
            .Include(a => a.Folder)
            .Include(a => a.BackupCodes)
            .FirstOrDefaultAsync(a => a.Id == id);

        if (account is not null)
            RevealAccountSecrets(account);

        LoggingConfiguration.LogDebug(account is null ? $"Account {id} was not found." : $"Account {id} loaded.", nameof(DatabaseService));
        return account;
    }

    public async Task<Account> AddAccountAsync(Account account)
    {
        ArgumentNullException.ThrowIfNull(account);
        LoggingConfiguration.LogInfo($"Adding account for service '{account.ServiceName}'.", nameof(DatabaseService));

        using var context = new AuthenticatorDbContext();
        account.CreatedAt = DateTime.UtcNow;
        SealAccountSecrets(account);
        context.Accounts.Add(account);
        await context.SaveChangesAsync();
        RevealAccountSecrets(account);

        LoggingConfiguration.LogInfo($"Account added with id {account.Id}.", nameof(DatabaseService));
        return account;
    }

    public async Task UpdateAccountAsync(Account account)
    {
        ArgumentNullException.ThrowIfNull(account);
        LoggingConfiguration.LogInfo($"Updating account id {account.Id}.", nameof(DatabaseService));

        using var context = new AuthenticatorDbContext();
        account.ModifiedAt = DateTime.UtcNow;
        SealAccountSecrets(account);
        context.Accounts.Update(account);
        await context.SaveChangesAsync();
        RevealAccountSecrets(account);

        LoggingConfiguration.LogDebug($"Account id {account.Id} updated.", nameof(DatabaseService));
    }

    public async Task SetAccountFavoriteAsync(int accountId, bool isFavorite)
    {
        LoggingConfiguration.LogInfo(
            $"Setting favorite for account id {accountId} to {isFavorite}.",
            nameof(DatabaseService));

        using var context = new AuthenticatorDbContext();
        var account = await context.Accounts.FirstOrDefaultAsync(a => a.Id == accountId && !a.IsDeleted);
        if (account is null)
            throw new InvalidOperationException($"Account id {accountId} was not found.");

        account.IsFavorite = isFavorite;
        account.ModifiedAt = DateTime.UtcNow;
        await context.SaveChangesAsync();
    }

    public async Task MoveAccountToFolderAsync(int accountId, int? folderId)
    {
        LoggingConfiguration.LogInfo($"Moving account id {accountId} to folder {(folderId.HasValue ? folderId.Value.ToString() : "None")}.", nameof(DatabaseService));

        using var context = new AuthenticatorDbContext();
        var account = await context.Accounts.FirstOrDefaultAsync(a => a.Id == accountId && !a.IsDeleted);
        if (account is null)
        {
            throw new InvalidOperationException($"Account id {accountId} was not found.");
        }

        if (folderId.HasValue)
        {
            var folderExists = await context.Folders.AnyAsync(folder => folder.Id == folderId.Value);
            if (!folderExists)
            {
                throw new ArgumentException($"Folder id {folderId.Value} was not found.", nameof(folderId));
            }
        }

        account.FolderId = folderId;
        account.ModifiedAt = DateTime.UtcNow;
        await context.SaveChangesAsync();

        LoggingConfiguration.LogInfo($"Account id {accountId} moved to folder {(folderId.HasValue ? folderId.Value.ToString() : "None")}.", nameof(DatabaseService));
    }

    public async Task DeleteAccountAsync(int id, bool softDelete = true)
    {
        LoggingConfiguration.LogInfo($"Deleting account id {id}. SoftDelete={softDelete}.", nameof(DatabaseService));

        using var context = new AuthenticatorDbContext();
        var account = await context.Accounts.FindAsync(id);
        if (account != null)
        {
            if (softDelete)
            {
                account.IsDeleted = true;
                account.ModifiedAt = DateTime.UtcNow;
                context.Accounts.Update(account);
            }
            else
            {
                context.Accounts.Remove(account);
            }

            await context.SaveChangesAsync();
            LoggingConfiguration.LogDebug($"Account id {id} deletion operation completed.", nameof(DatabaseService));
        }
        else
        {
            LoggingConfiguration.LogWarning($"Delete requested for missing account id {id}.", nameof(DatabaseService));
        }
    }

    public async Task<int> GetAccountCountAsync(bool excludeDeleted = true)
    {
        LoggingConfiguration.LogDebug($"Counting accounts. ExcludeDeleted={excludeDeleted}.", nameof(DatabaseService));

        using var context = new AuthenticatorDbContext();
        var query = context.Accounts.AsQueryable();
        if (excludeDeleted)
            query = query.Where(a => !a.IsDeleted);

        var count = await query.CountAsync();
        LoggingConfiguration.LogDebug($"Account count result: {count}.", nameof(DatabaseService));
        return count;
    }

    // Folders

    public async Task<List<Folder>> GetAllFoldersAsync()
    {
        LoggingConfiguration.LogDebug("Loading all folders.", nameof(DatabaseService));

        using var context = new AuthenticatorDbContext();
        var folders = await context.Folders
            .Include(f => f.Accounts)
            .OrderBy(f => f.Name)
            .ToListAsync();

        LoggingConfiguration.LogInfo($"Loaded {folders.Count} folder(s).", nameof(DatabaseService));
        return folders;
    }

    public async Task<Folder?> GetFolderByIdAsync(int id)
    {
        LoggingConfiguration.LogDebug($"Loading folder by id: {id}.", nameof(DatabaseService));

        using var context = new AuthenticatorDbContext();
        var folder = await context.Folders
            .Include(f => f.Accounts)
            .FirstOrDefaultAsync(f => f.Id == id);

        LoggingConfiguration.LogDebug(folder is null ? $"Folder {id} was not found." : $"Folder {id} loaded.", nameof(DatabaseService));
        return folder;
    }

    public async Task<Folder> AddFolderAsync(Folder folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        LoggingConfiguration.LogInfo($"Adding folder '{folder.Name}'.", nameof(DatabaseService));

        using var context = new AuthenticatorDbContext();
        folder.CreatedAt = DateTime.UtcNow;
        context.Folders.Add(folder);
        await context.SaveChangesAsync();

        LoggingConfiguration.LogInfo($"Folder added with id {folder.Id}.", nameof(DatabaseService));
        return folder;
    }

    public async Task UpdateFolderAsync(Folder folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        LoggingConfiguration.LogInfo($"Updating folder id {folder.Id}.", nameof(DatabaseService));

        using var context = new AuthenticatorDbContext();
        context.Folders.Update(folder);
        await context.SaveChangesAsync();

        LoggingConfiguration.LogDebug($"Folder id {folder.Id} updated.", nameof(DatabaseService));
    }

    public async Task DeleteFolderAsync(int id)
    {
        LoggingConfiguration.LogInfo($"Deleting folder id {id}.", nameof(DatabaseService));

        using var context = new AuthenticatorDbContext();
        var folder = await context.Folders.FindAsync(id);
        if (folder != null)
        {
            var accounts = await context.Accounts.Where(a => a.FolderId == id).ToListAsync();
            foreach (var account in accounts)
            {
                account.FolderId = null;
            }

            context.Folders.Remove(folder);
            await context.SaveChangesAsync();

            LoggingConfiguration.LogDebug($"Folder id {id} deleted; detached {accounts.Count} account(s).", nameof(DatabaseService));
        }
        else
        {
            LoggingConfiguration.LogWarning($"Delete requested for missing folder id {id}.", nameof(DatabaseService));
        }
    }

    // Backup Codes

    public async Task<List<BackupCode>> GetBackupCodesForAccountAsync(int accountId, bool includeUsed = false)
    {
        LoggingConfiguration.LogDebug($"Loading backup codes for account {accountId}. IncludeUsed={includeUsed}.", nameof(DatabaseService));

        using var context = new AuthenticatorDbContext();
        var query = context.BackupCodes
            .Where(bc => bc.AccountId == accountId && !bc.IsDeleted);

        if (!includeUsed)
            query = query.Where(bc => !bc.IsUsed);

        var backupCodes = await query.OrderBy(bc => bc.CreatedAt).ToListAsync();
        foreach (var backup in backupCodes)
            RevealBackupSecret(backup);
        LoggingConfiguration.LogDebug($"Loaded {backupCodes.Count} backup code record(s) for account {accountId}.", nameof(DatabaseService));
        return backupCodes;
    }

    public async Task AddBackupCodesAsync(int accountId, IEnumerable<string> codes)
    {
        ArgumentNullException.ThrowIfNull(codes);

        var codeList = codes.Where(code => !string.IsNullOrWhiteSpace(code)).ToList();
        LoggingConfiguration.LogInfo($"Adding {codeList.Count} backup code(s) for account {accountId}.", nameof(DatabaseService));

        using var context = new AuthenticatorDbContext();
        var backupCodes = codeList.Select(code => new BackupCode
        {
            AccountId = accountId,
            Code = SealValue(code),
            CreatedAt = DateTime.UtcNow
        }).ToList();

        context.BackupCodes.AddRange(backupCodes);
        await context.SaveChangesAsync();

        LoggingConfiguration.LogDebug($"Backup codes saved for account {accountId}.", nameof(DatabaseService));
    }

    public async Task MarkBackupCodeAsUsedAsync(int backupCodeId)
    {
        LoggingConfiguration.LogDebug($"Marking backup code {backupCodeId} as used.", nameof(DatabaseService));

        using var context = new AuthenticatorDbContext();
        var code = await context.BackupCodes.FindAsync(backupCodeId);
        if (code != null)
        {
            code.IsUsed = true;
            code.UsedAt = DateTime.UtcNow;
            await context.SaveChangesAsync();
            LoggingConfiguration.LogDebug($"Backup code {backupCodeId} marked as used.", nameof(DatabaseService));
        }
        else
        {
            LoggingConfiguration.LogWarning($"Backup code {backupCodeId} was not found.", nameof(DatabaseService));
        }
    }

    public async Task DeleteBackupCodesForAccountAsync(int accountId)
    {
        LoggingConfiguration.LogInfo($"Deleting backup codes for account {accountId}.", nameof(DatabaseService));

        using var context = new AuthenticatorDbContext();
        var codes = await context.BackupCodes.Where(bc => bc.AccountId == accountId).ToListAsync();
        context.BackupCodes.RemoveRange(codes);
        await context.SaveChangesAsync();

        LoggingConfiguration.LogDebug($"Deleted {codes.Count} backup code(s) for account {accountId}.", nameof(DatabaseService));
    }

    public async Task<int> GetTotalBackupCodeCountAsync(bool excludeUsed = true)
    {
        LoggingConfiguration.LogDebug($"Counting backup codes. ExcludeUsed={excludeUsed}.", nameof(DatabaseService));

        using var context = new AuthenticatorDbContext();
        var query = context.BackupCodes.Where(bc => !bc.IsDeleted);
        if (excludeUsed)
            query = query.Where(bc => !bc.IsUsed);

        var count = await query.CountAsync();
        LoggingConfiguration.LogDebug($"Backup code count result: {count}.", nameof(DatabaseService));
        return count;
    }

    // Password Entries

    public async Task<List<PasswordEntry>> GetPasswordEntriesAsync(string? source = null, bool includeDeleted = false)
    {
        LoggingConfiguration.LogDebug($"Loading password entries. Source={source ?? "All"}, IncludeDeleted={includeDeleted}.", nameof(DatabaseService));

        using var context = new AuthenticatorDbContext();
        var query = context.PasswordEntries.AsQueryable();

        if (!string.IsNullOrEmpty(source))
            query = query.Where(pe => pe.Source == source);

        if (!includeDeleted)
            query = query.Where(pe => !pe.IsDeleted);

        var entries = await query.OrderBy(pe => pe.ServiceName).ToListAsync();
        LoggingConfiguration.LogInfo($"Loaded {entries.Count} password entrie(s).", nameof(DatabaseService));
        return entries;
    }

    public async Task<PasswordEntry> AddPasswordEntryAsync(PasswordEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        LoggingConfiguration.LogInfo($"Adding password entry for service '{entry.ServiceName}'.", nameof(DatabaseService));

        using var context = new AuthenticatorDbContext();
        entry.CreatedAt = DateTime.UtcNow;
        context.PasswordEntries.Add(entry);
        await context.SaveChangesAsync();

        LoggingConfiguration.LogInfo($"Password entry added with id {entry.Id}.", nameof(DatabaseService));
        return entry;
    }

    public async Task UpdatePasswordEntryAsync(PasswordEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        LoggingConfiguration.LogInfo($"Updating password entry id {entry.Id}.", nameof(DatabaseService));

        using var context = new AuthenticatorDbContext();
        entry.ModifiedAt = DateTime.UtcNow;
        context.PasswordEntries.Update(entry);
        await context.SaveChangesAsync();

        LoggingConfiguration.LogDebug($"Password entry id {entry.Id} updated.", nameof(DatabaseService));
    }

    public async Task DeletePasswordEntryAsync(int id, bool softDelete = true)
    {
        LoggingConfiguration.LogInfo($"Deleting password entry id {id}. SoftDelete={softDelete}.", nameof(DatabaseService));

        using var context = new AuthenticatorDbContext();
        var entry = await context.PasswordEntries.FindAsync(id);
        if (entry != null)
        {
            if (softDelete)
            {
                entry.IsDeleted = true;
                entry.ModifiedAt = DateTime.UtcNow;
            }
            else
            {
                context.PasswordEntries.Remove(entry);
            }
            await context.SaveChangesAsync();

            LoggingConfiguration.LogDebug($"Password entry id {id} deletion operation completed.", nameof(DatabaseService));
        }
        else
        {
            LoggingConfiguration.LogWarning($"Delete requested for missing password entry id {id}.", nameof(DatabaseService));
        }
    }

    // Trash

    public async Task<int> GetTrashCountAsync()
    {
        LoggingConfiguration.LogDebug("Counting trash records.", nameof(DatabaseService));

        using var context = new AuthenticatorDbContext();
        var accountCount = await context.Accounts.CountAsync(a => a.IsDeleted);
        var passwordCount = await context.PasswordEntries.CountAsync(pe => pe.IsDeleted);
        var total = accountCount + passwordCount;

        LoggingConfiguration.LogDebug($"Trash count result: {total} (Accounts={accountCount}, PasswordEntries={passwordCount}).", nameof(DatabaseService));
        return total;
    }

    public async Task EmptyTrashAsync()
    {
        LoggingConfiguration.LogInfo("Emptying trash.", nameof(DatabaseService));

        using var context = new AuthenticatorDbContext();

        var deletedAccounts = await context.Accounts.Where(a => a.IsDeleted).ToListAsync();
        context.Accounts.RemoveRange(deletedAccounts);

        var deletedPasswords = await context.PasswordEntries.Where(pe => pe.IsDeleted).ToListAsync();
        context.PasswordEntries.RemoveRange(deletedPasswords);

        await context.SaveChangesAsync();

        LoggingConfiguration.LogInfo($"Trash emptied. Removed accounts={deletedAccounts.Count}, password entries={deletedPasswords.Count}.", nameof(DatabaseService));
    }

    private void RevealAccountSecrets(Account account)
    {
        account.SecretKey = RevealValue(account.SecretKey);
        if (account.BackupCodes is null)
            return;
        foreach (var backup in account.BackupCodes)
            RevealBackupSecret(backup);
    }

    private void RevealBackupSecret(BackupCode backup) =>
        backup.Code = RevealValue(backup.Code);

    private void SealAccountSecrets(Account account)
    {
        account.SecretKey = SealValue(account.SecretKey);
        if (account.BackupCodes is null)
            return;
        foreach (var backup in account.BackupCodes)
            backup.Code = SealValue(backup.Code);
    }

    private string SealValue(string value)
    {
        if (string.IsNullOrEmpty(value) || !VaultKeyService.Instance.IsUnlocked)
            return value;
        if (_secrets.IsEncrypted(value))
            return value;
        return _secrets.Protect(value);
    }

    private string RevealValue(string value)
    {
        if (string.IsNullOrEmpty(value) || !_secrets.IsEncrypted(value))
            return value;
        if (!VaultKeyService.Instance.IsUnlocked)
            throw new InvalidOperationException("Vault is locked; encrypted secrets cannot be read.");
        return _secrets.Unprotect(value);
    }
}