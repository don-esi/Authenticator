using Authenticator.Data.Models;

namespace Authenticator.Services.Providers;

public sealed class ProviderCapabilities
{
    public bool SupportsFavorites { get; init; }
    public bool SupportsWriteBack { get; init; }
    public bool SupportsSecureNotes { get; init; }
    public bool RequiresFilePath { get; init; }
    public bool RequiresUsername { get; init; }
}

public sealed class ProviderEntry
{
    public string ExternalId { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? Url { get; set; }
    public string? Notes { get; set; }
    public string? Totp { get; set; }
    public string? Path { get; set; }
    public bool IsFavorite { get; set; }
    public VaultItemType ItemType { get; set; } = VaultItemType.Login;
    public Dictionary<string, string> Fields { get; set; } = new();
}

public sealed class ProviderConnectRequest
{
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? FilePath { get; set; }
    public string? KeyFilePath { get; set; }
    /// <summary>Stable device/client id for cloud providers (e.g. LastPass).</summary>
    public string? ClientId { get; set; }
}

public interface IVaultProvider
{
    string ProviderId { get; }
    string DisplayName { get; }
    ProviderCapabilities Capabilities { get; }
    bool IsConnected { get; }

    Task ConnectAsync(ProviderConnectRequest request, CancellationToken ct = default);
    void Disconnect();
    Task<IReadOnlyList<ProviderEntry>> ListEntriesAsync(CancellationToken ct = default);

    /// <summary>
    /// Updates an existing provider entry identified by <see cref="ProviderEntry.ExternalId"/>
    /// (origin store id). No-op capability when <see cref="ProviderCapabilities.SupportsWriteBack"/> is false.
    /// </summary>
    Task UpdateEntryAsync(ProviderEntry entry, CancellationToken ct = default);
}

public static class VaultProviderCatalog
{
    public static IReadOnlyList<IVaultProvider> CreateAll() =>
    [
        new KeePassVaultProvider(),
        new OpVaultProvider(),
        new LastPassVaultProvider(),
        new BitwardenVaultProvider(),
        new DashlaneVaultProvider(),
        new OnePasswordVaultProvider(),
        new ProtonPassVaultProvider(),
        new RoboFormVaultProvider(),
        new StickyPasswordVaultProvider(),
        new TrueKeyVaultProvider(),
        new ZohoVaultVaultProvider()
    ];
}
