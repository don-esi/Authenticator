using System.Security.Cryptography;
using System.Text;

namespace Authenticator.Services;

/// <summary>
/// AES-GCM field encryption for vault secrets. Ciphertext uses a version prefix so
/// legacy plaintext values remain detectable for one-shot migration.
/// </summary>
public interface ISecretProtector
{
    bool IsEncrypted(string? value);
    string Protect(string plaintext);
    string Unprotect(string stored);
}

public sealed class SecretProtector : ISecretProtector
{
    public const string Prefix = "v1:";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly VaultKeyService _keys;

    public SecretProtector(VaultKeyService? keys = null)
    {
        _keys = keys ?? VaultKeyService.Instance;
    }

    public bool IsEncrypted(string? value) =>
        !string.IsNullOrEmpty(value) && value.StartsWith(Prefix, StringComparison.Ordinal);

    public string Protect(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        var dek = _keys.RequireDek();
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plainBytes.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(dek, TagSize);
        aes.Encrypt(nonce, plainBytes, cipher, tag);

        var packed = new byte[NonceSize + TagSize + cipher.Length];
        Buffer.BlockCopy(nonce, 0, packed, 0, NonceSize);
        Buffer.BlockCopy(tag, 0, packed, NonceSize, TagSize);
        Buffer.BlockCopy(cipher, 0, packed, NonceSize + TagSize, cipher.Length);
        return Prefix + Convert.ToBase64String(packed);
    }

    public string Unprotect(string stored)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stored);
        if (!IsEncrypted(stored))
            return stored;

        var dek = _keys.RequireDek();
        var packed = Convert.FromBase64String(stored[Prefix.Length..]);
        if (packed.Length < NonceSize + TagSize)
            throw new CryptographicException("Ciphertext is truncated.");

        var nonce = packed.AsSpan(0, NonceSize);
        var tag = packed.AsSpan(NonceSize, TagSize);
        var cipher = packed.AsSpan(NonceSize + TagSize);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(dek, TagSize);
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }
}
