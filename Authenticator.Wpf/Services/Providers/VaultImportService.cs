using Authenticator.Crypto;
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
    public int LinkCount { get; set; }
    public bool Attempted => LinkCount > 0;
    public List<string> Messages { get; } = new();
}

public sealed class VaultImportService
{
    private readonly VaultItemService _items = new();
    private static readonly ISecretProtector Secrets = new SecretProtector();

    /// <summary>
    /// First-time / explicit import — soft-merges into vault by username+domain (password conflict → mismatch).
    /// </summary>
    public async Task<int> BulkImportAsync(IVaultProvider provider, CancellationToken ct = default)
    {
        if (!provider.IsConnected)
            throw new InvalidOperationException($"{provider.DisplayName} is not connected.");

        var entries = await provider.ListEntriesAsync(ct);
        var indexes = await BuildIdentityIndexesAsync(ct);
        var imported = 0;
        var linkedExisting = 0;
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            var byOrigin = await FindLinkedItemIdAsync(provider.ProviderId, entry.ExternalId, ct);
            if (byOrigin is Guid originId)
            {
                var item = await _items.GetByIdAsync(originId, ct) ?? new VaultItem { Id = originId };
                var mismatch = SoftMerge(item, entry, provider.Capabilities.SupportsFavorites);
                item.IsDeleted = false;
                await _items.SaveAsync(item, ct, touchModifiedAt: false);
                await UpsertLinkAsync(item.Id, provider.ProviderId, entry.ExternalId, LinkHash(item), mismatch, ct);
                IndexItem(indexes, item);
                imported++;
                continue;
            }

            if (TryFindIdentity(indexes, entry, out var vaultId))
            {
                var existing = await _items.GetByIdAsync(vaultId, ct);
                // Same provider already linked under another origin = distinct store rows; don't collapse
                // (that overwrote ExternalId and false-flagged password conflicts on clean imports).
                if (existing is not null && !HasProviderLink(existing, provider.ProviderId))
                {
                    var mismatch = SoftMerge(existing, entry, provider.Capabilities.SupportsFavorites);
                    existing.IsDeleted = false;
                    await _items.SaveAsync(existing, ct, touchModifiedAt: false);
                    await UpsertLinkAsync(vaultId, provider.ProviderId, entry.ExternalId, LinkHash(existing), mismatch, ct);
                    IndexItem(indexes, existing);
                    linkedExisting++;
                    continue;
                }
            }

            var created = new VaultItem { Id = Guid.NewGuid() };
            ApplyEntry(created, entry, provider.Capabilities.SupportsFavorites);
            created.IsDeleted = false;
            await _items.SaveAsync(created, ct, touchModifiedAt: false);
            await UpsertLinkAsync(created.Id, provider.ProviderId, entry.ExternalId, LinkHash(created), mismatch: false, ct);
            IndexItem(indexes, created);
            imported++;
        }

        LoggingConfiguration.LogInfo(
            $"Imported {imported} entries from {provider.ProviderId} (linked existing={linkedExisting}).",
            nameof(VaultImportService));
        return imported + linkedExisting;
    }

    /// <summary>
    /// Full reconcile (import missing + conflict scan). Prefer splitting for UI: import first, scan in background.
    /// </summary>
    public async Task<VaultSyncResult> ReconcileAsync(IVaultProvider provider, CancellationToken ct = default)
    {
        var imported = await ImportMissingAsync(provider, ct);
        var scanned = await ScanConflictsAsync(provider, ct);
        return new VaultSyncResult
        {
            Imported = imported,
            Unchanged = scanned.Unchanged,
            Conflicts = scanned.Conflicts
        };
    }

    /// <summary>Fast path: add/link provider entries not yet linked (soft-merge on username+domain).</summary>
    public async Task<int> ImportMissingAsync(IVaultProvider provider, CancellationToken ct = default)
    {
        if (!provider.IsConnected)
            throw new InvalidOperationException($"{provider.DisplayName} is not connected.");

        var entries = await provider.ListEntriesAsync(ct);
        ProviderSessionService.Instance.CacheRemoteEntries(provider.ProviderId, entries);

        var indexes = await BuildIdentityIndexesAsync(ct);
        var imported = 0;
        var linkedExisting = 0;
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            var existingId = await FindLinkedItemIdAsync(provider.ProviderId, entry.ExternalId, ct);
            if (existingId is not null)
                continue;

            if (TryFindIdentity(indexes, entry, out var vaultId))
            {
                var existing = await _items.GetByIdAsync(vaultId, ct);
                // Same provider already linked under another origin = distinct store rows; don't collapse.
                if (existing is not null && !HasProviderLink(existing, provider.ProviderId))
                {
                    var mismatch = SoftMerge(existing, entry, provider.Capabilities.SupportsFavorites);
                    existing.IsDeleted = false;
                    await _items.SaveAsync(existing, ct, touchModifiedAt: false);
                    await UpsertLinkAsync(vaultId, provider.ProviderId, entry.ExternalId, LinkHash(existing), mismatch, ct);
                    IndexItem(indexes, existing);
                    linkedExisting++;
                    continue;
                }
            }

            var item = new VaultItem { Id = Guid.NewGuid() };
            ApplyEntry(item, entry, provider.Capabilities.SupportsFavorites);
            item.IsDeleted = false;
            await _items.SaveAsync(item, ct, touchModifiedAt: false);
            await UpsertLinkAsync(item.Id, provider.ProviderId, entry.ExternalId, LinkHash(item), mismatch: false, ct);
            IndexItem(indexes, item);
            imported++;
        }

        LoggingConfiguration.LogInfo(
            $"Imported {imported} missing from {provider.ProviderId} (linked existing={linkedExisting}).",
            nameof(VaultImportService));
        return imported + linkedExisting;
    }

    /// <summary>
    /// Flag password mismatches on linked items (do not overwrite vault). Soft-fills empty vault fields.
    /// </summary>
    public async Task<VaultSyncResult> ScanConflictsAsync(IVaultProvider provider, CancellationToken ct = default)
    {
        if (!provider.IsConnected)
            throw new InvalidOperationException($"{provider.DisplayName} is not connected.");

        var entries = (await provider.ListEntriesAsync(ct)).ToList();
        ProviderSessionService.Instance.CacheRemoteEntries(provider.ProviderId, entries);

        var result = new VaultSyncResult();
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            var existingId = await FindLinkedItemIdAsync(provider.ProviderId, entry.ExternalId, ct);
            if (existingId is null)
                continue;

            var local = await _items.GetByIdAsync(existingId.Value, ct);
            if (local is null)
                continue;

            // Ensure plaintext before SoftMerge — encrypted vault vs provider plaintext false-flags every row.
            var plain = await _items.GetPasswordAsync(local.Id, ct);
            if (plain is not null)
                local.Password = plain;

            var mismatch = SoftMerge(local, entry, provider.Capabilities.SupportsFavorites);
            // Keep origin Created/Updated — do not stamp import/scan time (breaks Diff "newer").
            await _items.SaveAsync(local, ct, touchModifiedAt: false);

            if (mismatch)
            {
                ProviderSessionService.Instance.CacheRemoteEntry(provider.ProviderId, entry);
                await UpsertLinkAsync(local.Id, provider.ProviderId, entry.ExternalId, LinkHash(local), mismatch: true, ct);
                result.Conflicts++;
            }
            else
            {
                await UpsertLinkAsync(local.Id, provider.ProviderId, entry.ExternalId, LinkHash(local), mismatch: false, ct);
                result.Unchanged++;
            }
        }

        LoggingConfiguration.LogInfo(
            $"Conflict scan {provider.ProviderId}: unchanged={result.Unchanged}, conflicts={result.Conflicts}.",
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
        ApplyOriginTimestamps(item, entry, force: true);
    }

    /// <summary>
    /// True when this vault row already has an origin link for <paramref name="providerId"/>.
    /// Same-store duplicates must stay as separate vault rows (one ExternalLink per provider).
    /// </summary>
    private static bool HasProviderLink(VaultItem item, string providerId) =>
        item.ExternalLinks?.Any(l =>
            string.Equals(l.Provider, providerId, StringComparison.OrdinalIgnoreCase)) == true;

    /// <summary>
    /// Soft-merge provider into vault. Returns true when both sides have non-empty differing passwords (Diff needed).
    /// </summary>
    private static bool SoftMerge(VaultItem vault, ProviderEntry entry, bool syncFavorites)
    {
        if (string.IsNullOrWhiteSpace(vault.Title))
            vault.Title = entry.Title;
        if (string.IsNullOrWhiteSpace(vault.Username))
            vault.Username = entry.Username;
        if (string.IsNullOrWhiteSpace(vault.Url))
            vault.Url = entry.Url;
        if (string.IsNullOrWhiteSpace(vault.Notes))
            vault.Notes = entry.Notes;
        if (string.IsNullOrWhiteSpace(vault.TotpSecret) && !string.IsNullOrWhiteSpace(entry.Totp))
            vault.TotpSecret = entry.Totp;
        if (syncFavorites && !vault.IsFavorite && entry.IsFavorite)
            vault.IsFavorite = true;

        // Repair import-stamped vault times so Diff "newer" can compare real origin dates.
        ApplyOriginTimestamps(vault, entry, force: false);

        var vaultPwd = PlainPassword(vault.Password);
        var remotePwd = PlainPassword(entry.Password);
        if (string.IsNullOrEmpty(vaultPwd) && !string.IsNullOrEmpty(remotePwd))
        {
            vault.Password = entry.Password;
            return false;
        }

        if (string.IsNullOrEmpty(remotePwd) || string.IsNullOrEmpty(vaultPwd))
            return false;

        return !string.Equals(vaultPwd, remotePwd, StringComparison.Ordinal);
    }

    /// <summary>
    /// Copy provider Created/Updated onto the vault row.
    /// <paramref name="force"/> (new import) always prefers provider times when present.
    /// Otherwise only rewrite when vault times look like an import stamp (Created≈Modified, after provider).
    /// </summary>
    private static void ApplyOriginTimestamps(VaultItem vault, ProviderEntry entry, bool force)
    {
        var providerCreated = ToUtc(entry.CreatedAt);
        var providerModified = ToUtc(entry.ModifiedAt) ?? providerCreated;

        if (providerCreated is DateTime pc &&
            (force || vault.CreatedAt == default || vault.CreatedAt > pc))
            vault.CreatedAt = pc;

        if (providerModified is null)
            return;

        var pm = providerModified.Value;
        if (force || vault.ModifiedAt is null || vault.ModifiedAt == default)
        {
            vault.ModifiedAt = pm;
            return;
        }

        if (!LooksLikeImportStamp(vault, pm))
            return;

        vault.ModifiedAt = pm;
        if (providerCreated is DateTime created && vault.CreatedAt > created)
            vault.CreatedAt = created;
    }

    /// <summary>
    /// Vault Created≈Modified and both clearly after the provider content time → import stamp, not a real edit.
    /// </summary>
    private static bool LooksLikeImportStamp(VaultItem vault, DateTime providerModifiedUtc)
    {
        if (vault.ModifiedAt is not DateTime vaultMod)
            return false;
        var created = vault.CreatedAt;
        if (created == default)
            return false;
        if (Math.Abs((vaultMod - created).TotalSeconds) > 5)
            return false;
        return vaultMod > providerModifiedUtc.AddMinutes(1);
    }

    private static DateTime? ToUtc(DateTime? value)
    {
        if (value is null || value == default)
            return null;
        return value.Value.Kind switch
        {
            DateTimeKind.Utc => value.Value,
            DateTimeKind.Local => value.Value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)
        };
    }

    private static string PlainPassword(string? stored)
    {
        var raw = (stored ?? "").Trim();
        if (string.IsNullOrEmpty(raw))
            return "";
        if (!Secrets.IsEncrypted(raw))
            return raw;
        try
        {
            return Secrets.Unprotect(raw).Trim();
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Failed to decrypt password for merge/compare.", nameof(VaultImportService));
            return raw;
        }
    }

    private static string LinkHash(VaultItem item) => VaultItemService.ContentHash(item);

    /// <summary>Keep vault password; clear mismatch; push vault → all linked write-back providers.</summary>
    public async Task<VaultPushResult> UseVaultPasswordAsync(Guid itemId, string providerId, CancellationToken ct = default)
    {
        var item = await _items.GetByIdAsync(itemId, ct) ?? throw new InvalidOperationException("Item not found.");
        var link = await GetLinkAsync(itemId, providerId, ct);
        if (link is not null && !string.IsNullOrWhiteSpace(link.ExternalId))
            await UpsertLinkAsync(itemId, providerId, link.ExternalId, LinkHash(item), mismatch: false, ct);

        return await PushToLinkedProvidersAsync(item, ct);
    }

    /// <summary>Take provider password into vault (soft-merge other empties); clear mismatch; push out.</summary>
    public async Task<VaultPushResult> UseProviderPasswordAsync(
        Guid itemId,
        ProviderEntry remote,
        string providerId,
        CancellationToken ct = default)
    {
        var item = await _items.GetByIdAsync(itemId, ct)
                   ?? new VaultItem { Id = itemId == Guid.Empty ? Guid.NewGuid() : itemId };
        SoftMerge(item, remote, syncFavorites: true);
        // Explicit: adopt provider password even when vault had a different non-empty value.
        item.Password = remote.Password;
        await _items.SaveAsync(item, ct);
        await UpsertLinkAsync(item.Id, providerId, remote.ExternalId, LinkHash(item), mismatch: false, ct);
        return await PushToLinkedProvidersAsync(item, ct);
    }

    public async Task SyncEntryToProviderAsync(Guid itemId, string providerId, CancellationToken ct = default)
    {
        await UseVaultPasswordAsync(itemId, providerId, ct);
    }

    public async Task SyncEntryToSharedAsync(Guid itemId, ProviderEntry remote, string providerId, CancellationToken ct = default)
    {
        await UseProviderPasswordAsync(itemId, remote, providerId, ct);
    }

    /// <summary>
    /// Remove a provider origin link from a vault item (false match / not the same entry).
    /// Next Sync can import that provider entry as its own vault row.
    /// </summary>
    public async Task UnlinkProviderAsync(Guid itemId, string providerId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        await RemoveLinkAsync(itemId, providerId, ct);
        LoggingConfiguration.LogInfo(
            $"Unlinked {providerId} from vault item {itemId}.",
            nameof(VaultImportService));
    }

    /// <summary>Clear IsMismatch on every ExternalLink for a vault item (after reconcile apply).</summary>
    public async Task ClearMismatchesForItemAsync(Guid itemId, CancellationToken ct = default)
    {
        if (!ConnectionConfig.IsOfflineMode)
        {
            await using var db = new AuthenticatorDbContext();
            var links = await db.ExternalLinks.Where(l => l.ItemId == itemId && l.IsMismatch).ToListAsync(ct);
            foreach (var link in links)
            {
                link.IsMismatch = false;
                link.LastSyncedAt = DateTime.UtcNow;
            }

            await db.SaveChangesAsync(ct);
        }

        try
        {
            await using var offline = new OfflineVaultDbContext();
            var links = await offline.ExternalLinks.Where(l => l.ItemId == itemId && l.IsMismatch).ToListAsync(ct);
            foreach (var link in links)
            {
                link.IsMismatch = false;
                link.LastSyncedAt = DateTime.UtcNow;
            }

            await offline.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Offline clear-mismatch failed.", nameof(VaultImportService));
        }
    }

    /// <summary>
    /// Drop all ExternalLinks flagged as mismatch (clears Conflicts without a full vault purge).
    /// Vault items stay; next Sync re-imports unlinked provider entries with current match rules.
    /// </summary>
    public async Task<int> ClearAllConflictLinksAsync(CancellationToken ct = default)
    {
        var removed = 0;
        if (!ConnectionConfig.IsOfflineMode)
        {
            await using var db = new AuthenticatorDbContext();
            removed = await db.ExternalLinks.Where(l => l.IsMismatch).ExecuteDeleteAsync(ct);
        }

        try
        {
            await using var offline = new OfflineVaultDbContext();
            var offlineRemoved = await offline.ExternalLinks.Where(l => l.IsMismatch).ExecuteDeleteAsync(ct);
            if (ConnectionConfig.IsOfflineMode)
                removed = offlineRemoved;
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Offline conflict-link clear failed.", nameof(VaultImportService));
        }

        ProviderSessionService.Instance.ClearRemoteCache();
        LoggingConfiguration.LogInfo($"Cleared {removed} conflict link(s).", nameof(VaultImportService));
        return removed;
    }

    private static async Task RemoveLinkAsync(Guid itemId, string providerId, CancellationToken ct)
    {
        if (!ConnectionConfig.IsOfflineMode)
        {
            await using var db = new AuthenticatorDbContext();
            await db.ExternalLinks
                .Where(l => l.ItemId == itemId && l.Provider == providerId)
                .ExecuteDeleteAsync(ct);
        }

        try
        {
            await using var offline = new OfflineVaultDbContext();
            await offline.ExternalLinks
                .Where(l => l.ItemId == itemId && l.Provider == providerId)
                .ExecuteDeleteAsync(ct);
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Offline unlink failed.", nameof(VaultImportService));
        }
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
        result.LinkCount = links.Count;
        if (links.Count == 0)
            return result;

        var hash = VaultItemService.ContentHash(item);
        foreach (var link in links)
        {
            ct.ThrowIfCancellationRequested();
            var originId = link.ExternalId?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(originId))
            {
                result.Skipped++;
                result.Messages.Add($"{link.Provider}: missing origin store id.");
                continue;
            }

            // Skip non-provider bookkeeping rows if any were stored as "Shared".
            if (string.Equals(link.Provider, "Shared", StringComparison.OrdinalIgnoreCase))
            {
                result.Skipped++;
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
                await UpsertLinkAsync(item.Id, link.Provider, originId, hash, mismatch: true, ct);
                continue;
            }

            if (!provider.Capabilities.SupportsWriteBack)
            {
                result.Skipped++;
                result.Messages.Add($"{provider.DisplayName}: write-back not supported.");
                await UpsertLinkAsync(item.Id, link.Provider, originId, hash, mismatch: true, ct);
                continue;
            }

            try
            {
                var pushed = ToProviderEntry(item, originId);
                await provider.UpdateEntryAsync(pushed, ct);
                // Keep Diff/scan cache aligned so a post-Apply reload does not re-flag this row.
                ProviderSessionService.Instance.CacheRemoteEntry(link.Provider, pushed);
                await UpsertLinkAsync(item.Id, link.Provider, originId, hash, mismatch: false, ct);
                result.Updated++;
                LoggingConfiguration.LogInfo(
                    $"Pushed Shared→{link.Provider} for {item.Id} (origin id {originId}).",
                    nameof(VaultImportService));
            }
            catch (Exception ex)
            {
                LoggingConfiguration.LogException(ex, $"Push to {link.Provider} failed for {item.Id}.", nameof(VaultImportService));
                result.Failed++;
                result.Messages.Add($"{provider.DisplayName}: {ex.Message}");
                await UpsertLinkAsync(item.Id, link.Provider, originId, hash, mismatch: true, ct);
            }
        }

        return result;
    }

    private static ProviderEntry ToProviderEntry(VaultItem item, string externalId) =>
        new()
        {
            ExternalId = externalId,
            Title = item.Title ?? "",
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
        var norm = ProviderSessionService.NormalizeExternalId(externalId);
        if (string.IsNullOrEmpty(norm))
            return null;

        List<ExternalLink> links;
        if (ConnectionConfig.IsOfflineMode)
        {
            await using var offline = new OfflineVaultDbContext();
            links = await offline.ExternalLinks.AsNoTracking()
                .Where(l => l.Provider == provider)
                .ToListAsync(ct);
        }
        else
        {
            await using var db = new AuthenticatorDbContext();
            links = await db.ExternalLinks.AsNoTracking()
                .Where(l => l.Provider == provider)
                .ToListAsync(ct);
        }

        return links.FirstOrDefault(l =>
                string.Equals(
                    ProviderSessionService.NormalizeExternalId(l.ExternalId),
                    norm,
                    StringComparison.Ordinal))
            ?.ItemId;
    }

    private sealed class IdentityIndexes
    {
        public Dictionary<string, Guid> ByUserDomain { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Guid> ByTitleUser { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<Guid, string?> Titles { get; } = new();
    }

    private static async Task<IdentityIndexes> BuildIdentityIndexesAsync(CancellationToken ct)
    {
        List<(Guid Id, string? Title, string? Username, string? Url)> rows;
        if (ConnectionConfig.IsOfflineMode)
        {
            await using var offline = new OfflineVaultDbContext();
            rows = await offline.VaultItems.AsNoTracking()
                .Where(i => !i.IsDeleted)
                .Select(i => new ValueTuple<Guid, string?, string?, string?>(i.Id, i.Title, i.Username, i.Url))
                .ToListAsync(ct);
        }
        else
        {
            await using var db = new AuthenticatorDbContext();
            rows = await db.VaultItems.AsNoTracking()
                .Where(i => !i.IsDeleted)
                .Select(i => new ValueTuple<Guid, string?, string?, string?>(i.Id, i.Title, i.Username, i.Url))
                .ToListAsync(ct);
        }

        var indexes = new IdentityIndexes();
        foreach (var (id, title, username, url) in rows)
            IndexKeys(indexes, id, title, username, url);
        return indexes;
    }

    private static void IndexItem(IdentityIndexes indexes, VaultItem item) =>
        IndexKeys(indexes, item.Id, item.Title, item.Username, item.Url);

    private static void IndexKeys(
        IdentityIndexes indexes,
        Guid id,
        string? title,
        string? username,
        string? url)
    {
        indexes.Titles[id] = title;

        var primary = UserDomainKey(username, url);
        if (primary is not null)
            indexes.ByUserDomain.TryAdd(primary, id);

        var secondary = TitleUserKey(title, username);
        if (secondary is not null)
            indexes.ByTitleUser.TryAdd(secondary, id);
    }

    private static bool TryFindIdentity(IdentityIndexes indexes, ProviderEntry entry, out Guid id)
    {
        id = default;
        var primary = UserDomainKey(entry.Username, entry.Url);
        if (primary is not null &&
            indexes.ByUserDomain.TryGetValue(primary, out id) &&
            indexes.Titles.TryGetValue(id, out var vaultTitle) &&
            TitlesCompatible(vaultTitle, entry.Title))
            return true;

        // Username+host hit with incompatible titles (e.g. Media vs HomeAssistant) → not a match.
        id = default;

        var secondary = TitleUserKey(entry.Title, entry.Username);
        return secondary is not null && indexes.ByTitleUser.TryGetValue(secondary, out id);
    }

    private static string? UserDomainKey(string? username, string? url)
    {
        var user = (username ?? "").Trim();
        if (string.IsNullOrEmpty(user))
            return null;
        var domain = NormalizeDomain(url);
        if (string.IsNullOrEmpty(domain))
            return null;
        return $"{user}\n{domain}";
    }

    private static string? TitleUserKey(string? title, string? username)
    {
        var t = (title ?? "").Trim();
        if (string.IsNullOrEmpty(t))
            return null;
        return $"{t}\n{(username ?? "").Trim()}";
    }

    /// <summary>
    /// Same login across stores often renames slightly; unrelated services on the same host must not merge
    /// when titles are clearly different (Media vs HomeAssistant).
    /// </summary>
    private static bool TitlesCompatible(string? vaultTitle, string? providerTitle)
    {
        var a = NormalizeTitle(vaultTitle);
        var b = NormalizeTitle(providerTitle);
        // Missing title on either side — allow soft-merge to fill.
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
            return true;
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
            return true;
        if (a.Contains(b, StringComparison.OrdinalIgnoreCase) ||
            b.Contains(a, StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    private static string NormalizeTitle(string? title)
    {
        var t = (title ?? "").Trim().ToLowerInvariant();
        if (t.StartsWith("http://", StringComparison.Ordinal) || t.StartsWith("https://", StringComparison.Ordinal))
        {
            var host = NormalizeDomain(t);
            if (!string.IsNullOrEmpty(host))
                t = host;
        }

        return t;
    }

    /// <summary>
    /// Identity host key: lowercase host, strip leading www., ignore path/query/fragment.
    /// Includes non-default port (e.g. media.zog.one:5000 vs :8123) so self-hosted apps don't collide.
    /// </summary>
    public static string? NormalizeDomain(string? url) => VaultDomain.NormalizeDomain(url);

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
