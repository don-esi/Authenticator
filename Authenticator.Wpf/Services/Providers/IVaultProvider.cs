using Authenticator.Data.Models;

namespace Authenticator.Services.Providers;

public sealed class ProviderCapabilities
{
    public bool SupportsFavorites { get; init; }
    public bool SupportsWriteBack { get; init; }
    /// <summary>Provider can delete an origin entry by ExternalId (LastPass/KeePass).</summary>
    public bool SupportsDelete { get; init; }
    /// <summary>Provider can create a new origin entry and return its ExternalId.</summary>
    public bool SupportsCreate { get; init; }
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
    /// <summary>Origin store created time (UTC) when the provider exposes it.</summary>
    public DateTime? CreatedAt { get; set; }
    /// <summary>Origin store last-modified time (UTC) when the provider exposes it.</summary>
    public DateTime? ModifiedAt { get; set; }
}

public sealed class ProviderConnectRequest
{
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? FilePath { get; set; }
    public string? KeyFilePath { get; set; }
    /// <summary>Stable device/client id for cloud providers (e.g. LastPass).</summary>
    public string? ClientId { get; set; }
    /// <summary>
    /// Interactive LastPass MFA UI (WealthForge-style). When null, MFA is cancelled
    /// (silent reconnect path relying on device trust).
    /// </summary>
    public PasswordManager.LastPass.Ui.IAsyncUi? LastPassUi { get; set; }
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
    /// (origin store id). When <see cref="ProviderEntry.Path"/> is set, moves the entry into that folder/group.
    /// </summary>
    Task UpdateEntryAsync(ProviderEntry entry, CancellationToken ct = default);

    /// <summary>
    /// Deletes an origin entry. Required when <see cref="ProviderCapabilities.SupportsDelete"/> is true.
    /// </summary>
    Task DeleteEntryAsync(string externalId, CancellationToken ct = default);

    /// <summary>
    /// Creates a new origin entry (honors <see cref="ProviderEntry.Path"/>). Returns the new ExternalId.
    /// </summary>
    Task<string> CreateEntryAsync(ProviderEntry entry, CancellationToken ct = default);
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
