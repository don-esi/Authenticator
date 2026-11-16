using Authenticator.Data;
using Authenticator.Data.Models;
using Authenticator.Logging;
using Microsoft.EntityFrameworkCore;

namespace Authenticator.Services.Providers;

public sealed class VaultSyncResult
{
    public int Imported { get; set; }
    public int Unchanged { get; set; }
    public int Conflicts { get; set; }
    public int TotalProcessed => Imported + Unchanged + Conflicts;
}

public sealed class VaultPushResult
{
    public int Updated { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public List<string> Messages { get; } = new();
}

public sealed class VaultImportService
{
    private readonly VaultItemService _items = new();

    /// <summary>First-time / explicit import — writes provider values into Shared (overwrites linked items).</summary>
    public async Task<int> BulkImportAsync(IVaultProvider provider, CancellationToken ct = default)
    {
        if (!provider.IsConnected)
            throw new InvalidOperationException($"{provider.DisplayName} is not connected.");

        var entries = await provider.ListEntriesAsync(ct);
        var imported = 0;
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            var existingId = await FindLinkedItemIdAsync(provider.ProviderId, entry.ExternalId, ct);
            var item = existingId is Guid id
                ? await _items.GetByIdAsync(id, ct) ?? new VaultItem { Id = id }
                : new VaultItem { Id = Guid.NewGuid() };

            ApplyEntry(item, entry, provider.Capabilities.SupportsFavorites);
            item.IsDeleted = false;

            await _items.SaveAsync(item, ct);
            await UpsertLinkAsync(item.Id, provider.ProviderId, entry.ExternalId, VaultItemService.ContentHash(item), mismatch: false, ct);
            imported++;
        }

        LoggingConfiguration.LogInfo($"Imported {imported} entries from {provider.ProviderId}.", nameof(VaultImportService));
        return imported;
    }

    /// <summary>
    /// Ongoing sync: import new links; leave matching items alone; mark hash conflicts without overwriting Shared.
    /// </summary>
    public async Task<VaultSyncResult> ReconcileAsync(IVaultProvider provider, CancellationToken ct = default)
    {
        if (!provider.IsConnected)
            throw new InvalidOperationException($"{provider.DisplayName} is not connected.");

        var entries = await provider.ListEntriesAsync(ct);
        var result = new VaultSyncResult();
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            var existingId = await FindLinkedItemIdAsync(provider.ProviderId, entry.ExternalId, ct);
            if (existingId is null)
            {
                var item = new VaultItem { Id = Guid.NewGuid() };
                ApplyEntry(item, entry, provider.Capabilities.SupportsFavorites);
                item.IsDeleted = false;
                await _items.SaveAsync(item, ct);
                await UpsertLinkAsync(item.Id, provider.ProviderId, entry.ExternalId, VaultItemService.ContentHash(item), mismatch: false, ct);
                result.Imported++;
                continue;
            }

            var local = await _items.GetByIdAsync(existingId.Value, ct);
            if (local is null)
            {
                var item = new VaultItem { Id = existingId.Value };
                ApplyEntry(item, entry, provider.Capabilities.SupportsFavorites);
                item.IsDeleted = false;
                await _items.SaveAsync(item, ct);
                await UpsertLinkAsync(item.Id, provider.ProviderId, entry.ExternalId, VaultItemService.ContentHash(item), mismatch: false, ct);
                result.Imported++;
                continue;
            }

            var localHash = VaultItemService.ContentHash(local);
            var remoteHash = ContentHash(entry);
            if (string.Equals(localHash, remoteHash, StringComparison.OrdinalIgnoreCase))
            {
                await UpsertLinkAsync(local.Id, provider.ProviderId, entry.ExternalId, localHash, mismatch: false, ct);
                result.Unchanged++;
            }
            else
            {
                // Keep Shared values; flag for Diff/match resolution.
                await UpsertLinkAsync(local.Id, provider.ProviderId, entry.ExternalId, remoteHash, mismatch: true, ct);
                result.Conflicts++;
            }
        }

        LoggingConfiguration.LogInfo(
            $"Reconciled {provider.ProviderId}: imported={result.Imported}, unchanged={result.Unchanged}, conflicts={result.Conflicts}.",
            nameof(VaultImportService));
        return result;
    }

    private static void ApplyEntry(VaultItem item, ProviderEntry entry, bool syncFavorites)
    {
        item.Title = entry.Title;
        item.ItemType = entry.ItemType;
        item.Username = entry.Username;
        item.Password = entry.Password;
        item.Url = entry.Url;
        item.Notes = entry.Notes;
        item.TotpSecret = string.IsNullOrWhiteSpace(entry.Totp) ? item.TotpSecret : entry.Totp;
        if (syncFavorites)
            item.IsFavorite = entry.IsFavorite;
    }

    private static string ContentHash(ProviderEntry entry)
    {
        var probe = new VaultItem
        {
            Title = entry.Title,
            Username = entry.Username,
            Password = entry.Password,
            Url = entry.Url,
            Notes = entry.Notes,
            TotpSecret = entry.Totp,
            IsFavorite = entry.IsFavorite,
            ItemType = entry.ItemType
        };
        return VaultItemService.ContentHash(probe);
    }

    public async Task SyncEntryToProviderAsync(Guid itemId, string providerId, CancellationToken ct = default)
    {
        var item = await _items.GetByIdAsync(itemId, ct) ?? throw new InvalidOperationException("Item not found.");
        var link = await GetLinkAsync(itemId, providerId, ct)
                   ?? throw new InvalidOperationException($"No origin link for {providerId} on this item.");
        if (string.IsNullOrWhiteSpace(link.ExternalId))
            throw new InvalidOperationException($"Origin store id is missing for {providerId}.");

        var provider = await ProviderSessionService.Instance.EnsureConnectedAsync(providerId, ct);
        if (!provider.Capabilities.SupportsWriteBack)
            throw new NotSupportedException($"{provider.DisplayName} does not support write-back.");

        await provider.UpdateEntryAsync(ToProviderEntry(item, link.ExternalId), ct);
        await UpsertLinkAsync(itemId, providerId, link.ExternalId, VaultItemService.ContentHash(item), mismatch: false, ct);
        LoggingConfiguration.LogInfo(
            $"Pushed Shared→{providerId} for {itemId} (origin id {link.ExternalId}).",
            nameof(VaultImportService));
    }

    public async Task SyncEntryToSharedAsync(Guid itemId, ProviderEntry remote, string providerId, CancellationToken ct = default)
    {
        var item = await _items.GetByIdAsync(itemId, ct) ?? new VaultItem { Id = itemId == Guid.Empty ? Guid.NewGuid() : itemId };
        ApplyEntry(item, remote, syncFavorites: true);
        await _items.SaveAsync(item, ct);
        await UpsertLinkAsync(item.Id, providerId, remote.ExternalId, VaultItemService.ContentHash(item), mismatch: false, ct);
    }

    /// <summary>
    /// After Shared edit: push to every linked provider that is connected and supports write-back,
    /// keyed by each link's origin <see cref="ExternalLink.ExternalId"/>.
    /// </summary>
    public async Task<VaultPushResult> PushToLinkedProvidersAsync(VaultItem item, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        var result = new VaultPushResult();
        var links = await GetLinksForItemAsync(item.Id, ct);
        if (links.Count == 0)
            return result;

        var hash = VaultItemService.ContentHash(item);
        foreach (var link in links)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(link.ExternalId))
            {
                result.Skipped++;
                result.Messages.Add($"{link.Provider}: missing origin store id.");
                continue;
            }

            IVaultProvider provider;
            try
            {
                provider = await ProviderSessionService.Instance.EnsureConnectedAsync(link.Provider, ct);
            }
            catch (Exception ex)
            {
                result.Skipped++;
                result.Messages.Add($"{link.Provider}: not connected ({ex.Message}).");
                await UpsertLinkAsync(item.Id, link.Provider, link.ExternalId, hash, mismatch: true, ct);
                continue;
            }

            if (!provider.Capabilities.SupportsWriteBack)
            {
                result.Skipped++;
                result.Messages.Add($"{provider.DisplayName}: write-back not supported.");
                await UpsertLinkAsync(item.Id, link.Provider, link.ExternalId, hash, mismatch: true, ct);
                continue;
            }

            try
            {
                await provider.UpdateEntryAsync(ToProviderEntry(item, link.ExternalId), ct);
                await UpsertLinkAsync(item.Id, link.Provider, link.ExternalId, hash, mismatch: false, ct);
                result.Updated++;
            }
            catch (Exception ex)
            {
                LoggingConfiguration.LogException(ex, $"Push to {link.Provider} failed for {item.Id}.", nameof(VaultImportService));
                result.Failed++;
                result.Messages.Add($"{provider.DisplayName}: {ex.Message}");
                await UpsertLinkAsync(item.Id, link.Provider, link.ExternalId, hash, mismatch: true, ct);
            }
        }

        return result;
    }

    private static ProviderEntry ToProviderEntry(VaultItem item, string externalId) =>
        new()
        {
            ExternalId = externalId,
            Title = item.Title,
            Username = item.Username,
            Password = item.Password,
            Url = item.Url,
            Notes = item.Notes,
            Totp = item.TotpSecret,
            IsFavorite = item.IsFavorite,
            ItemType = item.ItemType
        };

    private static async Task<ExternalLink?> GetLinkAsync(Guid itemId, string provider, CancellationToken ct)
    {
        if (ConnectionConfig.IsOfflineMode)
        {
            await using var offline = new OfflineVaultDbContext();
            return await offline.ExternalLinks.AsNoTracking()
                .FirstOrDefaultAsync(l => l.ItemId == itemId && l.Provider == provider, ct);
        }

        await using var db = new AuthenticatorDbContext();
        return await db.ExternalLinks.AsNoTracking()
            .FirstOrDefaultAsync(l => l.ItemId == itemId && l.Provider == provider, ct);
    }

    private static async Task<List<ExternalLink>> GetLinksForItemAsync(Guid itemId, CancellationToken ct)
    {
        if (ConnectionConfig.IsOfflineMode)
        {
            await using var offline = new OfflineVaultDbContext();
            return await offline.ExternalLinks.AsNoTracking()
                .Where(l => l.ItemId == itemId)
                .ToListAsync(ct);
        }

        await using var db = new AuthenticatorDbContext();
        return await db.ExternalLinks.AsNoTracking()
            .Where(l => l.ItemId == itemId)
            .ToListAsync(ct);
    }

    private static async Task<Guid?> FindLinkedItemIdAsync(string provider, string externalId, CancellationToken ct)
    {
        if (ConnectionConfig.IsOfflineMode)
        {
            await using var offline = new OfflineVaultDbContext();
            var link = await offline.ExternalLinks.AsNoTracking()
                .FirstOrDefaultAsync(l => l.Provider == provider && l.ExternalId == externalId, ct);
            return link?.ItemId;
        }

        await using var db = new AuthenticatorDbContext();
        var shared = await db.ExternalLinks.AsNoTracking()
            .FirstOrDefaultAsync(l => l.Provider == provider && l.ExternalId == externalId, ct);
        return shared?.ItemId;
    }

    private static async Task UpsertLinkAsync(
        Guid itemId,
        string provider,
        string externalId,
        string hash,
        bool mismatch,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalId);

        if (ConnectionConfig.IsOfflineMode)
        {
            await UpsertLinkInDbAsync(new OfflineVaultDbContext(), itemId, provider, externalId, hash, mismatch, ct);
            return;
        }

        await UpsertLinkInDbAsync(new AuthenticatorDbContext(), itemId, provider, externalId, hash, mismatch, ct);

        try
        {
            await UpsertLinkInDbAsync(new OfflineVaultDbContext(), itemId, provider, externalId, hash, mismatch, ct);
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Mirror ExternalLink to offline failed.", nameof(VaultImportService));
        }
    }

    private static async Task UpsertLinkInDbAsync(
        DbContext db,
        Guid itemId,
        string provider,
        string externalId,
        string hash,
        bool mismatch,
        CancellationToken ct)
    {
        await using (db)
        {
            var links = db.Set<ExternalLink>();
            var link = await links.FirstOrDefaultAsync(
                l => l.ItemId == itemId && l.Provider == provider, ct);
            if (link is null)
            {
                links.Add(new ExternalLink
                {
                    ItemId = itemId,
                    Provider = provider,
                    ExternalId = externalId,
                    ContentHash = hash,
                    IsMismatch = mismatch,
                    LastSyncedAt = DateTime.UtcNow
                });
            }
            else
            {
                // Never replace a real origin id with a placeholder.
                if (!string.IsNullOrWhiteSpace(externalId))
                    link.ExternalId = externalId;
                link.ContentHash = hash;
                link.IsMismatch = mismatch;
                link.LastSyncedAt = DateTime.UtcNow;
            }

            await db.SaveChangesAsync(ct);
        }
    }
}
