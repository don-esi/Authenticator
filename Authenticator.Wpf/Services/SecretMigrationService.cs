using Authenticator.Data;
using Authenticator.Logging;
using Microsoft.EntityFrameworkCore;

namespace Authenticator.Services;

/// <summary>
/// One-shot encrypt of legacy plaintext Account.SecretKey and BackupCode.Code values.
/// </summary>
public sealed class SecretMigrationService
{
    private readonly ISecretProtector _protector;

    public SecretMigrationService(ISecretProtector? protector = null)
    {
        _protector = protector ?? new SecretProtector();
    }

    public async Task<(int Accounts, int BackupCodes)> EncryptLegacySecretsAsync()
    {
        if (!VaultKeyService.Instance.IsUnlocked)
            throw new InvalidOperationException("Vault must be unlocked to migrate secrets.");

        await using var context = new AuthenticatorDbContext();
        var accounts = await context.Accounts.ToListAsync();
        var backups = await context.BackupCodes.ToListAsync();

        var accountCount = 0;
        foreach (var account in accounts)
        {
            if (string.IsNullOrEmpty(account.SecretKey) || _protector.IsEncrypted(account.SecretKey))
                continue;
            account.SecretKey = _protector.Protect(account.SecretKey);
            account.ModifiedAt = DateTime.UtcNow;
            accountCount++;
        }

        var backupCount = 0;
        foreach (var backup in backups)
        {
            if (string.IsNullOrEmpty(backup.Code) || _protector.IsEncrypted(backup.Code))
                continue;
            backup.Code = _protector.Protect(backup.Code);
            backupCount++;
        }

        if (accountCount > 0 || backupCount > 0)
            await context.SaveChangesAsync();

        LoggingConfiguration.LogInfo(
            $"Secret migration encrypted accounts={accountCount}, backupCodes={backupCount}.",
            nameof(SecretMigrationService));
        return (accountCount, backupCount);
    }
}
