using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Authenticator.Logging;

namespace Authenticator.Services.Providers;

/// <summary>Saved "Connect to" link for a password manager (retained until Disconnect).</summary>
public sealed class ProviderConnectionRecord
{
    public string ProviderId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? FilePath { get; set; }
    public string? KeyFilePath { get; set; }
    /// <summary>Stable LastPass client id so reconnects can reuse device trust where possible.</summary>
    public string? ClientId { get; set; }
    public DateTime ConnectedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// DPAPI (CurrentUser) store for persistent provider connections under LocalAppData.
/// Import-from flows never write here.
/// </summary>
public sealed class ProviderConnectionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;
    private List<ProviderConnectionRecord>? _cached;

    public ProviderConnectionStore()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Authenticator");
        Directory.CreateDirectory(folder);
        _path = Path.Combine(folder, "provider-connections.bin");
    }

    public IReadOnlyList<ProviderConnectionRecord> LoadAll()
    {
        EnsureLoaded();
        return _cached!;
    }

    public ProviderConnectionRecord? Find(string providerId)
    {
        EnsureLoaded();
        return _cached!.FirstOrDefault(c =>
            string.Equals(c.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));
    }

    public void Upsert(ProviderConnectionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (string.IsNullOrWhiteSpace(record.ProviderId))
            throw new ArgumentException("ProviderId is required.", nameof(record));

        EnsureLoaded();
        var existing = _cached!.FindIndex(c =>
            string.Equals(c.ProviderId, record.ProviderId, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0)
            _cached[existing] = record;
        else
            _cached.Add(record);
        Save();
    }

    public bool Remove(string providerId)
    {
        EnsureLoaded();
        var removed = _cached!.RemoveAll(c =>
            string.Equals(c.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));
        if (removed > 0)
            Save();
        return removed > 0;
    }

    private void EnsureLoaded()
    {
        if (_cached is not null)
            return;

        if (!File.Exists(_path))
        {
            _cached = new List<ProviderConnectionRecord>();
            return;
        }

        try
        {
            var protectedBytes = File.ReadAllBytes(_path);
            var jsonBytes = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
            var json = Encoding.UTF8.GetString(jsonBytes);
            _cached = JsonSerializer.Deserialize<List<ProviderConnectionRecord>>(json, JsonOptions)
                      ?? new List<ProviderConnectionRecord>();
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Failed to load provider-connections.bin.", nameof(ProviderConnectionStore));
            _cached = new List<ProviderConnectionRecord>();
        }
    }

    private void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_cached ?? new List<ProviderConnectionRecord>(), JsonOptions);
            var jsonBytes = Encoding.UTF8.GetBytes(json);
            var protectedBytes = ProtectedData.Protect(jsonBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(_path, protectedBytes);
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Failed to save provider-connections.bin.", nameof(ProviderConnectionStore));
        }
    }
}
