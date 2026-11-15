using System.Security.Cryptography;
using System.Text;

namespace Authenticator.Crypto;

/// <summary>
/// Recovery-key wrap of the shared VDEK for new-machine enrollment.
/// </summary>
public static class VaultRecovery
{
    private const int RecoveryIterations = 200_000;

    /// <summary>Creates a printable recovery key (Base32-ish hex groups).</summary>
    public static string GenerateRecoveryKey()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static string Fingerprint(byte[] dek)
    {
        ArgumentNullException.ThrowIfNull(dek);
        var hash = SHA256.HashData(dek);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string FingerprintPrefix(byte[] dek, int hexChars = 8) =>
        Fingerprint(dek)[..Math.Min(hexChars, 64)];

    public static byte[] DeriveRecoveryKek(string recoveryKey, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(recoveryKey.Trim()),
            salt,
            RecoveryIterations,
            HashAlgorithmName.SHA256,
            outputLength: VaultKeyService.DekSize);

    public static (string WrapBase64, string SaltBase64) WrapWithRecoveryKey(byte[] dek, string recoveryKey)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var kek = DeriveRecoveryKek(recoveryKey, salt);
        try
        {
            return (VaultKeyService.WrapKey(kek, dek), Convert.ToBase64String(salt));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    public static byte[] UnwrapWithRecoveryKey(string wrapBase64, string saltBase64, string recoveryKey)
    {
        var salt = Convert.FromBase64String(saltBase64);
        var kek = DeriveRecoveryKek(recoveryKey, salt);
        try
        {
            return VaultKeyService.UnwrapKey(kek, wrapBase64);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }
}
