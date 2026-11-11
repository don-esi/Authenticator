using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Authenticator.Logging;

namespace Authenticator.Services;

public sealed class LockCredentials
{
    public string? PinHash { get; set; }
    public string? PinSalt { get; set; }
    public string? PinDekWrap { get; set; }

    public string? YubiKeyPublicId { get; set; }
    public int? YubiKeySerialNumber { get; set; }
    public string? YubiKeyLastOtp { get; set; }
    /// <summary>Random KEK used only after a valid OTP; itself DPAPI-protected via this file.</summary>
    public string? YubiSlotKek { get; set; }
    public string? YubiDekWrap { get; set; }

    public bool HelloEnabled { get; set; }
    /// <summary>Random KEK used only after Windows Hello consent; DPAPI-protected via this file.</summary>
    public string? HelloKek { get; set; }
    public string? HelloDekWrap { get; set; }

    public int CryptoFormatVersion { get; set; }

    // Legacy fields cleared on save.
    public string? YubiKeySecretBase64 { get; set; }
    public int YubiKeySlot { get; set; }
    public string? Fido2CredentialIdBase64 { get; set; }
    public string? Fido2PublicKeyCoseBase64 { get; set; }
    public int? Fido2SerialNumber { get; set; }

    public bool PinEnabled =>
        !string.IsNullOrWhiteSpace(PinSalt)
        && (!string.IsNullOrWhiteSpace(PinDekWrap) || (!string.IsNullOrWhiteSpace(PinHash)));

    public bool YubiKeyEnabled => !string.IsNullOrWhiteSpace(YubiKeyPublicId);
    public bool HelloUnlockEnabled => HelloEnabled && !string.IsNullOrWhiteSpace(HelloKek) && !string.IsNullOrWhiteSpace(HelloDekWrap);
    public bool HasUnlockMethod => PinEnabled || YubiKeyEnabled || HelloUnlockEnabled;
    public bool HasEncryptedVault =>
        !string.IsNullOrWhiteSpace(PinDekWrap)
        || !string.IsNullOrWhiteSpace(YubiDekWrap)
        || !string.IsNullOrWhiteSpace(HelloDekWrap)
        || CryptoFormatVersion >= VaultKeyService.CryptoFormatVersion;

    public int UnlockMethodCount
    {
        get
        {
            var count = 0;
            if (PinEnabled) count++;
            if (YubiKeyEnabled) count++;
            if (HelloUnlockEnabled) count++;
            return count;
        }
    }
}

/// <summary>
/// Persists unlock credentials with DPAPI (CurrentUser) under LocalAppData.
/// </summary>
public sealed class LockCredentialStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;
    private LockCredentials? _cached;

    public LockCredentialStore()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Authenticator");
        Directory.CreateDirectory(folder);
        _path = Path.Combine(folder, "lock-credentials.bin");
    }

    public LockCredentials Load()
    {
        if (_cached is not null)
            return _cached;

        if (!File.Exists(_path))
        {
            _cached = new LockCredentials();
            return _cached;
        }

        try
        {
            var protectedBytes = File.ReadAllBytes(_path);
            var jsonBytes = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
            var json = Encoding.UTF8.GetString(jsonBytes);
            _cached = JsonSerializer.Deserialize<LockCredentials>(json, JsonOptions) ?? new LockCredentials();
            return _cached;
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Failed to load lock credentials.", nameof(LockCredentialStore));
            _cached = new LockCredentials();
            return _cached;
        }
    }

    public void Save(LockCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        credentials.YubiKeySecretBase64 = null;
        credentials.YubiKeySlot = 0;
        credentials.Fido2CredentialIdBase64 = null;
        credentials.Fido2PublicKeyCoseBase64 = null;
        credentials.Fido2SerialNumber = null;

        var json = JsonSerializer.Serialize(credentials, JsonOptions);
        var jsonBytes = Encoding.UTF8.GetBytes(json);
        var protectedBytes = ProtectedData.Protect(jsonBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(_path, protectedBytes);
        _cached = credentials;
        LoggingConfiguration.LogInfo(
            $"Lock credentials saved. Pin={credentials.PinEnabled}, YubiKey={credentials.YubiKeyEnabled}, Hello={credentials.HelloUnlockEnabled}.",
            nameof(LockCredentialStore));
    }

    public void SetPin(string pin, VaultKeyService? keys = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pin);
        if (pin.Length < 4)
            throw new ArgumentException("PIN must be at least 4 characters.", nameof(pin));

        keys ??= VaultKeyService.Instance;
        var credentials = Load();
        var salt = RandomNumberGenerator.GetBytes(16);
        var kek = VaultKeyService.DerivePinKek(pin, salt);
        var dek = EnsureDek(credentials, keys);
        credentials.PinSalt = Convert.ToBase64String(salt);
        credentials.PinHash = Convert.ToBase64String(kek);
        credentials.PinDekWrap = VaultKeyService.WrapKey(kek, dek);
        credentials.CryptoFormatVersion = VaultKeyService.CryptoFormatVersion;
        Save(credentials);
        CryptographicOperations.ZeroMemory(kek);
    }

    public void ClearPin()
    {
        var credentials = Load();
        EnsureNotLastUnlockMethod(credentials, clearingPin: true, clearingYubi: false, clearingHello: false);
        credentials.PinHash = null;
        credentials.PinSalt = null;
        credentials.PinDekWrap = null;
        Save(credentials);
    }

    /// <summary>
    /// Unwraps the DEK with the PIN (preferred) or falls back to legacy hash verify + wrap creation.
    /// </summary>
    public bool TryUnlockWithPin(string pin, VaultKeyService? keys = null)
    {
        keys ??= VaultKeyService.Instance;
        var credentials = Load();
        if (!credentials.PinEnabled || string.IsNullOrWhiteSpace(pin))
            return false;

        var salt = Convert.FromBase64String(credentials.PinSalt!);
        var kek = VaultKeyService.DerivePinKek(pin, salt);

        try
        {
            if (!string.IsNullOrWhiteSpace(credentials.PinDekWrap))
            {
                var dek = VaultKeyService.UnwrapKey(kek, credentials.PinDekWrap);
                keys.SetDek(dek);
                CryptographicOperations.ZeroMemory(dek);
                return true;
            }

            // Legacy: hash verify only, then create DEK wraps on first unlock after upgrade.
            if (string.IsNullOrWhiteSpace(credentials.PinHash))
                return false;

            var expected = Convert.FromBase64String(credentials.PinHash);
            if (!CryptographicOperations.FixedTimeEquals(expected, kek))
                return false;

            var legacyDek = EnsureDek(credentials, keys);
            credentials.PinDekWrap = VaultKeyService.WrapKey(kek, legacyDek);
            EnsureYubiWrap(credentials, legacyDek);
            EnsureHelloWrap(credentials, legacyDek);
            credentials.CryptoFormatVersion = VaultKeyService.CryptoFormatVersion;
            Save(credentials);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    public bool VerifyPin(string pin) => TryUnlockWithPin(pin);

    public void SetYubiKeyRegistration(YubiKeyRegistration registration, VaultKeyService? keys = null)
    {
        ArgumentNullException.ThrowIfNull(registration);
        keys ??= VaultKeyService.Instance;
        var credentials = Load();
        credentials.YubiKeyPublicId = registration.PublicId;
        credentials.YubiKeySerialNumber = registration.SerialNumber;
        credentials.YubiKeyLastOtp = null;

        var dek = EnsureDek(credentials, keys);
        var slotKek = RandomNumberGenerator.GetBytes(VaultKeyService.DekSize);
        credentials.YubiSlotKek = Convert.ToBase64String(slotKek);
        credentials.YubiDekWrap = VaultKeyService.WrapKey(slotKek, dek);
        credentials.CryptoFormatVersion = VaultKeyService.CryptoFormatVersion;
        Save(credentials);
        CryptographicOperations.ZeroMemory(slotKek);
    }

    public bool TryUnlockWithYubi(VaultKeyService? keys = null)
    {
        keys ??= VaultKeyService.Instance;
        var credentials = Load();
        if (!credentials.YubiKeyEnabled)
            return false;

        if (string.IsNullOrWhiteSpace(credentials.YubiSlotKek) || string.IsNullOrWhiteSpace(credentials.YubiDekWrap))
        {
            // Legacy registration without wraps: create DEK if session already has one, or mint new.
            var dek = EnsureDek(credentials, keys);
            EnsureYubiWrap(credentials, dek);
            EnsurePinWrapIfPossible(credentials, dek);
            EnsureHelloWrap(credentials, dek);
            credentials.CryptoFormatVersion = VaultKeyService.CryptoFormatVersion;
            Save(credentials);
            return true;
        }

        try
        {
            var slotKek = Convert.FromBase64String(credentials.YubiSlotKek);
            var dek = VaultKeyService.UnwrapKey(slotKek, credentials.YubiDekWrap);
            keys.SetDek(dek);
            CryptographicOperations.ZeroMemory(dek);
            CryptographicOperations.ZeroMemory(slotKek);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    public void RememberYubiKeyOtp(string otp)
    {
        var credentials = Load();
        credentials.YubiKeyLastOtp = otp;
        Save(credentials);
    }

    public void ClearYubiKey()
    {
        var credentials = Load();
        EnsureNotLastUnlockMethod(credentials, clearingPin: false, clearingYubi: true, clearingHello: false);
        credentials.YubiKeyPublicId = null;
        credentials.YubiKeySerialNumber = null;
        credentials.YubiKeyLastOtp = null;
        credentials.YubiSlotKek = null;
        credentials.YubiDekWrap = null;
        Save(credentials);
    }

    public YubiKeyRegistration? GetYubiKeyRegistration()
    {
        var credentials = Load();
        if (!credentials.YubiKeyEnabled)
            return null;

        return new YubiKeyRegistration
        {
            PublicId = credentials.YubiKeyPublicId!,
            SerialNumber = credentials.YubiKeySerialNumber
        };
    }

    public void SetHello(VaultKeyService? keys = null)
    {
        keys ??= VaultKeyService.Instance;
        var credentials = Load();
        var dek = EnsureDek(credentials, keys);
        var helloKek = RandomNumberGenerator.GetBytes(VaultKeyService.DekSize);
        credentials.HelloEnabled = true;
        credentials.HelloKek = Convert.ToBase64String(helloKek);
        credentials.HelloDekWrap = VaultKeyService.WrapKey(helloKek, dek);
        credentials.CryptoFormatVersion = VaultKeyService.CryptoFormatVersion;
        Save(credentials);
        CryptographicOperations.ZeroMemory(helloKek);
    }

    public bool TryUnlockWithHello(VaultKeyService? keys = null)
    {
        keys ??= VaultKeyService.Instance;
        var credentials = Load();
        if (!credentials.HelloUnlockEnabled)
            return false;

        try
        {
            var helloKek = Convert.FromBase64String(credentials.HelloKek!);
            var dek = VaultKeyService.UnwrapKey(helloKek, credentials.HelloDekWrap!);
            keys.SetDek(dek);
            CryptographicOperations.ZeroMemory(dek);
            CryptographicOperations.ZeroMemory(helloKek);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    public void ClearHello()
    {
        var credentials = Load();
        EnsureNotLastUnlockMethod(credentials, clearingPin: false, clearingYubi: false, clearingHello: true);
        credentials.HelloEnabled = false;
        credentials.HelloKek = null;
        credentials.HelloDekWrap = null;
        Save(credentials);
    }

    private static byte[] EnsureDek(LockCredentials credentials, VaultKeyService keys)
    {
        if (keys.IsUnlocked)
            return (byte[])keys.RequireDek().Clone();

        if (credentials.HasEncryptedVault)
            throw new InvalidOperationException("Vault is encrypted. Unlock with an existing method before adding another.");

        return keys.CreateDek();
    }

    private static void EnsureYubiWrap(LockCredentials credentials, byte[] dek)
    {
        if (!credentials.YubiKeyEnabled)
            return;
        if (!string.IsNullOrWhiteSpace(credentials.YubiSlotKek) && !string.IsNullOrWhiteSpace(credentials.YubiDekWrap))
            return;

        var slotKek = RandomNumberGenerator.GetBytes(VaultKeyService.DekSize);
        credentials.YubiSlotKek = Convert.ToBase64String(slotKek);
        credentials.YubiDekWrap = VaultKeyService.WrapKey(slotKek, dek);
        CryptographicOperations.ZeroMemory(slotKek);
    }

    private static void EnsureHelloWrap(LockCredentials credentials, byte[] dek)
    {
        if (!credentials.HelloEnabled)
            return;
        if (!string.IsNullOrWhiteSpace(credentials.HelloKek) && !string.IsNullOrWhiteSpace(credentials.HelloDekWrap))
            return;

        var helloKek = RandomNumberGenerator.GetBytes(VaultKeyService.DekSize);
        credentials.HelloKek = Convert.ToBase64String(helloKek);
        credentials.HelloDekWrap = VaultKeyService.WrapKey(helloKek, dek);
        CryptographicOperations.ZeroMemory(helloKek);
    }

    private static void EnsurePinWrapIfPossible(LockCredentials credentials, byte[] dek)
    {
        // Cannot recreate PIN wrap without the PIN; leave for next PIN unlock/set.
        _ = credentials;
        _ = dek;
    }

    private static void EnsureNotLastUnlockMethod(
        LockCredentials credentials,
        bool clearingPin,
        bool clearingYubi,
        bool clearingHello)
    {
        var remaining = credentials.UnlockMethodCount;
        if (clearingPin && credentials.PinEnabled) remaining--;
        if (clearingYubi && credentials.YubiKeyEnabled) remaining--;
        if (clearingHello && credentials.HelloUnlockEnabled) remaining--;

        if (remaining <= 0 && credentials.HasEncryptedVault)
        {
            throw new InvalidOperationException(
                "Cannot remove the last unlock method while secrets are encrypted. Add another unlock method first.");
        }
    }
}
