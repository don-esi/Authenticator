using System.Security.Cryptography;
using System.Text;
using Authenticator.Logging;

namespace Authenticator.Services;

/// <summary>
/// Session DEK holder and wrap/unwrap helpers for unlock methods.
/// DEK lives in memory only while the app is unlocked.
/// </summary>
public sealed class VaultKeyService
{
    public const int DekSize = 32;
    public const int CryptoFormatVersion = 1;
    private const int PinIterations = 100_000;

    public static VaultKeyService Instance { get; } = new();

    private readonly object _gate = new();
    private byte[]? _dek;

    public bool IsUnlocked
    {
        get
        {
            lock (_gate)
                return _dek is not null;
        }
    }

    public byte[] RequireDek()
    {
        lock (_gate)
        {
            if (_dek is null)
                throw new InvalidOperationException("Vault is locked. Unlock before reading or writing secrets.");
            return _dek;
        }
    }

    public void SetDek(byte[] dek)
    {
        ArgumentNullException.ThrowIfNull(dek);
        if (dek.Length != DekSize)
            throw new ArgumentException($"DEK must be {DekSize} bytes.", nameof(dek));

        lock (_gate)
        {
            ClearUnlockedDek_NoLock();
            _dek = (byte[])dek.Clone();
        }

        LoggingConfiguration.LogInfo("Vault DEK loaded into session.", nameof(VaultKeyService));
    }

    public byte[] CreateDek()
    {
        var dek = RandomNumberGenerator.GetBytes(DekSize);
        SetDek(dek);
        return dek;
    }

    public void Clear()
    {
        lock (_gate)
            ClearUnlockedDek_NoLock();
        LoggingConfiguration.LogInfo("Vault DEK cleared from session.", nameof(VaultKeyService));
    }

    public static byte[] DerivePinKek(string pin, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(pin),
            salt,
            PinIterations,
            HashAlgorithmName.SHA256,
            outputLength: DekSize);

    public static string WrapKey(byte[] kek, byte[] dek)
    {
        ArgumentNullException.ThrowIfNull(kek);
        ArgumentNullException.ThrowIfNull(dek);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[dek.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(kek, 16);
        aes.Encrypt(nonce, dek, cipher, tag);
        var packed = new byte[12 + 16 + cipher.Length];
        Buffer.BlockCopy(nonce, 0, packed, 0, 12);
        Buffer.BlockCopy(tag, 0, packed, 12, 16);
        Buffer.BlockCopy(cipher, 0, packed, 28, cipher.Length);
        return Convert.ToBase64String(packed);
    }

    public static byte[] UnwrapKey(byte[] kek, string wrapBase64)
    {
        ArgumentNullException.ThrowIfNull(kek);
        ArgumentException.ThrowIfNullOrWhiteSpace(wrapBase64);
        var packed = Convert.FromBase64String(wrapBase64);
        if (packed.Length < 28)
            throw new CryptographicException("Wrap blob is truncated.");

        var nonce = packed.AsSpan(0, 12);
        var tag = packed.AsSpan(12, 16);
        var cipher = packed.AsSpan(28);
        var dek = new byte[cipher.Length];
        using var aes = new AesGcm(kek, 16);
        aes.Decrypt(nonce, cipher, tag, dek);
        return dek;
    }

    private void ClearUnlockedDek_NoLock()
    {
        if (_dek is null)
            return;
        CryptographicOperations.ZeroMemory(_dek);
        _dek = null;
    }
}
