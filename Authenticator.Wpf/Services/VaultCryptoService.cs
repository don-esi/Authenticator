using System.Security.Cryptography;
using Authenticator.Crypto;
using Authenticator.Data;
using Authenticator.Data.Models;
using Authenticator.Logging;
using Microsoft.EntityFrameworkCore;

namespace Authenticator.Services;

/// <summary>
/// Promotes the in-memory DEK to a shared VDEK and persists recovery wraps.
/// </summary>
public sealed class VaultCryptoService
{
    public async Task<VaultCrypto?> GetAsync(CancellationToken ct = default)
    {
        try
        {
            await using var db = new AuthenticatorDbContext();
            return await db.VaultCrypto.AsNoTracking().FirstOrDefaultAsync(x => x.Id == 1, ct);
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Failed to load VaultCrypto.", nameof(VaultCryptoService));
            return null;
        }
    }

    /// <summary>
    /// Ensures shared VaultCrypto exists using the currently unlocked DEK (promotion).
    /// Returns the recovery key when newly created (show once); null if already present.
    /// </summary>
    public async Task<string?> EnsureSharedVaultAsync(CancellationToken ct = default)
    {
        var dek = VaultKeyService.Instance.RequireDek();
        await using var db = new AuthenticatorDbContext();
        var row = await db.VaultCrypto.FirstOrDefaultAsync(x => x.Id == 1, ct);
        if (row is not null && !string.IsNullOrWhiteSpace(row.RecoveryWrap) && !string.IsNullOrWhiteSpace(row.VdekFingerprint))
        {
            ConnectionConfig.SetFingerprintPrefix(row.VdekFingerprint[..Math.Min(8, row.VdekFingerprint.Length)]);
            return null;
        }

        var recoveryKey = VaultRecovery.GenerateRecoveryKey();
        var (wrap, salt) = VaultRecovery.WrapWithRecoveryKey(dek, recoveryKey);
        var fingerprint = VaultRecovery.Fingerprint(dek);

        if (row is null)
        {
            db.VaultCrypto.Add(new VaultCrypto
            {
                Id = 1,
                CryptoFormatVersion = VaultKeyService.CryptoFormatVersion,
                VdekFingerprint = fingerprint,
                RecoveryWrap = wrap,
                RecoverySalt = salt,
                CreatedAt = DateTime.UtcNow
            });
        }
        else
        {
            row.CryptoFormatVersion = VaultKeyService.CryptoFormatVersion;
            row.VdekFingerprint = fingerprint;
            row.RecoveryWrap = wrap;
            row.RecoverySalt = salt;
            row.ModifiedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct);
        ConnectionConfig.SetFingerprintPrefix(fingerprint[..8]);
        LoggingConfiguration.LogInfo("Shared VDEK recovery material written to VaultCrypto.", nameof(VaultCryptoService));
        return recoveryKey;
    }

    /// <summary>Enroll this machine: unwrap VDEK via recovery key into the session (caller persists unlock wraps).</summary>
    public async Task<bool> EnrollWithRecoveryKeyAsync(string recoveryKey, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recoveryKey);
        await using var db = new AuthenticatorDbContext();
        var row = await db.VaultCrypto.AsNoTracking().FirstOrDefaultAsync(x => x.Id == 1, ct);
        if (row is null || string.IsNullOrWhiteSpace(row.RecoveryWrap))
            return false;

        var dek = VaultRecovery.UnwrapWithRecoveryKey(row.RecoveryWrap, row.RecoverySalt, recoveryKey);
        try
        {
            if (!string.Equals(VaultRecovery.Fingerprint(dek), row.VdekFingerprint, StringComparison.OrdinalIgnoreCase))
                return false;
            VaultKeyService.Instance.SetDek(dek);
            ConnectionConfig.SetFingerprintPrefix(row.VdekFingerprint[..Math.Min(8, row.VdekFingerprint.Length)]);
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }
}
