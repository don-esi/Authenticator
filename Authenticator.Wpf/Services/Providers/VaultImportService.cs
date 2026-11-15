using Authenticator.Data;
using Authenticator.Data.Models;
using Authenticator.Logging;
using Microsoft.EntityFrameworkCore;

namespace Authenticator.Services.Providers;

public sealed class VaultImportService
{
    private readonly VaultItemService _items = new();

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

            item.Title = entry.Title;
            item.ItemType = entry.ItemType;
            item.Username = entry.Username;
            item.Password = entry.Password;
            item.Url = entry.Url;
            item.Notes = entry.Notes;
            item.TotpSecret = string.IsNullOrWhiteSpace(entry.Totp) ? item.TotpSecret : entry.Totp;
            if (provider.Capabilities.SupportsFavorites)
                item.IsFavorite = entry.IsFavorite;
            item.IsDeleted = false;

            await _items.SaveAsync(item, ct);
            await UpsertLinkAsync(item.Id, provider.ProviderId, entry.ExternalId, VaultItemService.ContentHash(item), mismatch: false, ct);
            imported++;
        }

        LoggingConfiguration.LogInfo($"Imported {imported} entries from {provider.ProviderId}.", nameof(VaultImportService));
        return imported;
    }

    public async Task SyncEntryToProviderAsync(Guid itemId, string providerId, CancellationToken ct = default)
    {
        // Write-back is capability-gated; for now update ExternalLink hash from shared as "synced to shared view".
        var item = await _items.GetByIdAsync(itemId, ct) ?? throw new InvalidOperationException("Item not found.");
        await UpsertLinkAsync(itemId, providerId, itemId.ToString(), VaultItemService.ContentHash(item), mismatch: false, ct);
        LoggingConfiguration.LogInfo($"Marked sync Shared→{providerId} for {itemId} (hash updated).", nameof(VaultImportService));
    }

    public async Task SyncEntryToSharedAsync(Guid itemId, ProviderEntry remote, string providerId, CancellationToken ct = default)
    {
        var item = await _items.GetByIdAsync(itemId, ct) ?? new VaultItem { Id = itemId == Guid.Empty ? Guid.NewGuid() : itemId };
        item.Title = remote.Title;
        item.Username = remote.Username;
        item.Password = remote.Password;
        item.Url = remote.Url;
        item.Notes = remote.Notes;
        item.TotpSecret = remote.Totp;
        item.IsFavorite = remote.IsFavorite;
        item.ItemType = remote.ItemType;
        await _items.SaveAsync(item, ct);
        await UpsertLinkAsync(item.Id, providerId, remote.ExternalId, VaultItemService.ContentHash(item), mismatch: false, ct);
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
        await using var db = new AuthenticatorDbContext();
        var link = await db.ExternalLinks.FirstOrDefaultAsync(
            l => l.ItemId == itemId && l.Provider == provider, ct);
        if (link is null)
        {
            db.ExternalLinks.Add(new ExternalLink
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
            link.ExternalId = externalId;
            link.ContentHash = hash;
            link.IsMismatch = mismatch;
            link.LastSyncedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);
    }
}
