using Authenticator.Crypto;
using Authenticator.Data;
using Authenticator.Data.Models;
using Authenticator.Logging;
using Authenticator.Services;
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

/// <summary>Per-field source for consolidate merge (single id, multi-id join, or direct edit override).</summary>
public sealed class MergeFieldSelection
{
    private readonly Dictionary<string, Guid> _sources = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<Guid>> _multiSources = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _overrides = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, Guid> Sources => _sources;
    public IReadOnlyDictionary<string, List<Guid>> MultiSources => _multiSources;
    public IReadOnlyDictionary<string, string> Overrides => _overrides;

    public void Set(string field, Guid itemId)
    {
        _sources[field] = itemId;
        _multiSources.Remove(field);
        _overrides.Remove(field);
    }

    /// <summary>Join non-empty values from multiple items (Notes / URL / EquivalentDomains style).</summary>
    public void SetMulti(string field, IEnumerable<Guid> itemIds)
    {
        var list = itemIds.Distinct().ToList();
        if (list.Count == 0)
            return;
        if (list.Count == 1)
        {
            Set(field, list[0]);
            return;
        }

        _multiSources[field] = list;
        _sources.Remove(field);
        _overrides.Remove(field);
    }

    public void SetOverride(string field, string value)
    {
        _overrides[field] = value;
        _sources.Remove(field);
        _multiSources.Remove(field);
    }

    public Guid Get(string field, Guid fallback) =>
        _sources.TryGetValue(field, out var id) ? id : fallback;

    public bool TryGetOverride(string field, out string? value) =>
        _overrides.TryGetValue(field, out value);

    public bool TryGetMulti(string field, out IReadOnlyList<Guid> ids)
    {
        if (_multiSources.TryGetValue(field, out var list))
        {
            ids = list;
            return true;
        }

        ids = Array.Empty<Guid>();
        return false;
    }
}

public sealed class VaultMergeResult
{
    public Guid SurvivorId { get; set; }
    public int DonorsDeleted { get; set; }
    public int LinksTransferred { get; set; }
    public int LinksDroppedSameProvider { get; set; }
    public int OriginsDeleted { get; set; }
    public int OriginsCreated { get; set; }
    public string? CanonicalPath { get; set; }
    public VaultPushResult? Push { get; set; }
    public List<string> Messages { get; } = new();
}

public sealed class ConsolidateSuggestGroup
{
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
    public bool IsExactHost { get; init; }
    public List<VaultItem> Items { get; init; } = new();
}

/// <summary>Timestamp + survivor ranking helpers for consolidate UI / automerge.</summary>
public static class VaultConsolidateHints
{
    public static DateTime? EffectiveTimestampUtc(VaultItem item)
    {
        var raw = item.ModifiedAt ?? (item.CreatedAt == default ? null : item.CreatedAt);
        if (raw is null || raw == default)
            return null;
        return raw.Value.Kind switch
        {
            DateTimeKind.Utc => raw.Value,
            DateTimeKind.Local => raw.Value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(raw.Value, DateTimeKind.Utc)
        };
    }

    public static string FormatTimestampLocal(DateTime? utcOrUnspecified)
    {
        if (utcOrUnspecified is null || utcOrUnspecified == default)
            return "—";
        var local = utcOrUnspecified.Value.Kind == DateTimeKind.Local
            ? utcOrUnspecified.Value
            : DateTime.SpecifyKind(utcOrUnspecified.Value, DateTimeKind.Utc).ToLocalTime();
        return local.ToString("yyyy-MM-dd HH:mm");
    }

    public static int LinkCount(VaultItem item) =>
        (item.ExternalLinks ?? Array.Empty<ExternalLink>())
        .Count(l => !string.IsNullOrWhiteSpace(l.Provider) &&
                    !string.Equals(l.Provider, "Shared", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Preferred survivor: most provider links, then newest Updated (else Created).
    /// Null when fewer than two items or a hard tie on both score legs.
    /// </summary>
    public static VaultItem? SuggestSurvivor(IReadOnlyList<VaultItem> items)
    {
        if (items.Count < 2)
            return null;

        var ranked = items
            .Select(i => (Item: i, Links: LinkCount(i), Ts: EffectiveTimestampUtc(i) ?? DateTime.MinValue))
            .OrderByDescending(t => t.Links)
            .ThenByDescending(t => t.Ts)
            .ThenBy(t => t.Item.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var best = ranked[0];
        var second = ranked[1];
        if (best.Links == second.Links && best.Ts == second.Ts)
            return best.Item; // stable pick by title after ties

        return best.Item;
    }

    /// <summary>
    /// Per-field pick: prefer non-empty value from the newest-dated source; fall back to survivor.
    /// Notes: if multiple distinct non-empty notes exist, multi-merge them.
    /// </summary>
    public static MergeFieldSelection BuildAutomergeSelection(
        VaultItem survivor,
        IReadOnlyList<VaultItem> donors)
    {
        var all = new List<VaultItem> { survivor };
        all.AddRange(donors);
        var selection = new MergeFieldSelection();

        foreach (var field in new[] { "Title", "Username", "Password", "URL", "Notes", "Favorite", "TOTP", "Folder" })
        {
            if (field is "Notes")
            {
                var withNotes = all
                    .Where(i => !string.IsNullOrWhiteSpace(i.Notes))
                    .Select(i => (i.Id, Text: i.Notes!.Trim()))
                    .GroupBy(t => t.Text, StringComparer.Ordinal)
                    .Select(g => g.First().Id)
                    .ToList();
                if (withNotes.Count > 1)
                {
                    selection.SetMulti("Notes", withNotes);
                    continue;
                }
            }

            if (field is "Folder")
            {
                // Prefer Shared folder name; else richest provider Path among linked origins.
                var bestShared = all
                    .Select(i => (Item: i, Value: FolderDisplayName(i), Ts: EffectiveTimestampUtc(i) ?? DateTime.MinValue))
                    .Where(t => !string.IsNullOrWhiteSpace(t.Value))
                    .OrderByDescending(t => t.Value!.Length)
                    .ThenByDescending(t => t.Ts)
                    .ThenByDescending(t => t.Item.Id == survivor.Id)
                    .FirstOrDefault();
                if (bestShared.Item is not null)
                {
                    selection.Set("Folder", bestShared.Item.Id);
                    continue;
                }

                var bestPath = all
                    .Select(i => (Item: i, Value: RichestProviderPath(i), Ts: EffectiveTimestampUtc(i) ?? DateTime.MinValue))
                    .Where(t => !string.IsNullOrWhiteSpace(t.Value))
                    .OrderByDescending(t => t.Value!.Length)
                    .ThenByDescending(t => t.Ts)
                    .FirstOrDefault();
                if (bestPath.Item is not null && !string.IsNullOrWhiteSpace(bestPath.Value))
                    selection.SetOverride("Folder", bestPath.Value!);
                else
                    selection.Set("Folder", survivor.Id);
                continue;
            }

            var best = all
                .Select(i => (Item: i, Value: FieldRaw(field, i), Ts: EffectiveTimestampUtc(i) ?? DateTime.MinValue))
                .Where(t => !string.IsNullOrWhiteSpace(t.Value))
                .OrderByDescending(t => t.Ts)
                .ThenByDescending(t => t.Item.Id == survivor.Id)
                .FirstOrDefault();

            selection.Set(field, best.Item?.Id ?? survivor.Id);
        }

        return selection;
    }

    public static string? FolderDisplayName(VaultItem item)
    {
        var name = item.Folder?.Name?.Trim();
        if (!string.IsNullOrEmpty(name))
            return name;
        return null;
    }

    /// <summary>Longest non-empty provider Path from cached remote entries for this item's links.</summary>
    public static string? RichestProviderPath(VaultItem item)
    {
        string? best = null;
        foreach (var link in item.ExternalLinks ?? Array.Empty<ExternalLink>())
        {
            if (string.IsNullOrWhiteSpace(link.Provider) ||
                string.Equals(link.Provider, "Shared", StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.IsNullOrWhiteSpace(link.ExternalId))
                continue;
            if (!ProviderSessionService.Instance.TryGetCachedRemote(link.Provider, link.ExternalId, out var remote) ||
                remote is null)
                continue;
            var path = NormalizeCanonicalPath(remote.Path);
            if (string.IsNullOrEmpty(path))
                continue;
            if (best is null || path.Length > best.Length)
                best = path;
        }

        return best;
    }

    public static string? NormalizeCanonicalPath(string? path)
    {
        var p = (path ?? "").Trim();
        if (string.IsNullOrEmpty(p) || string.Equals(p, "(none)", StringComparison.OrdinalIgnoreCase))
            return null;
        return p.Replace('\\', '/');
    }

    /// <summary>
    /// True when any path segment looks like a provider recycle/trash/deleted-items folder
    /// (KeePass Recycle Bin, LastPass Deleted Items, etc.). Sync/import should skip those entries.
    /// </summary>
    public static bool IsDeletedItemsFolderPath(string? path)
    {
        var normalized = NormalizeCanonicalPath(path);
        if (normalized is null)
            return false;

        foreach (var segment in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (LooksLikeDeletedItemsFolder(segment))
                return true;
        }

        return false;
    }

    /// <summary>Match folder names like Recycle Bin, Trash, Deleted Items (ignores spaces/_/-).</summary>
    public static bool LooksLikeDeletedItemsFolder(string? folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName))
            return false;

        var compact = new string(folderName
            .Where(c => !char.IsWhiteSpace(c) && c is not '_' and not '-' and not '.')
            .ToArray());
        if (compact.Length == 0)
            return false;

        return compact.Equals("recyclebin", StringComparison.OrdinalIgnoreCase)
               || compact.Equals("trash", StringComparison.OrdinalIgnoreCase)
               || compact.Equals("trashbin", StringComparison.OrdinalIgnoreCase)
               || compact.Equals("trashcan", StringComparison.OrdinalIgnoreCase)
               || compact.Equals("deleted", StringComparison.OrdinalIgnoreCase)
               || compact.Equals("deleteditem", StringComparison.OrdinalIgnoreCase)
               || compact.Equals("deleteditems", StringComparison.OrdinalIgnoreCase)
               || compact.Equals("recentlydeleted", StringComparison.OrdinalIgnoreCase)
               || compact.Equals("rubbish", StringComparison.OrdinalIgnoreCase)
               || compact.Equals("rubbishbin", StringComparison.OrdinalIgnoreCase)
               || compact.Equals("wastebasket", StringComparison.OrdinalIgnoreCase)
               || compact.Equals("wastebin", StringComparison.OrdinalIgnoreCase)
               || compact.Equals("papierkorb", StringComparison.OrdinalIgnoreCase)
               || compact.Equals("corbeille", StringComparison.OrdinalIgnoreCase);
    }

    private static string? FieldRaw(string field, VaultItem item) => field switch
    {
        "Title" => item.Title,
        "Username" => item.Username,
        "Password" => item.Password,
        "URL" => item.Url,
        "Notes" => item.Notes,
        "Favorite" => item.IsFavorite.ToString(),
        "TOTP" => item.TotpSecret,
        "Folder" => FolderDisplayName(item) ?? RichestProviderPath(item),
        _ => null
    };
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

        var entries = ExcludeDeletedItemsFolders(await provider.ListEntriesAsync(ct), provider.ProviderId);
        var indexes = await BuildIdentityIndexesAsync(ct);
        var imported = 0;
        var linkedExisting = 0;
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            if (await IsOriginSuppressedAsync(provider.ProviderId, entry.ExternalId, ct))
                continue;

            var byOrigin = await FindLinkedItemIdAsync(provider.ProviderId, entry.ExternalId, ct);
            if (byOrigin is Guid originId)
            {
                var item = await _items.GetByIdAsync(originId, ct);
                if (item is null || item.IsDeleted)
                {
                    // Stale link on a soft-deleted / missing row — do not resurrect; fall through.
                    await RemoveLinkAsync(originId, provider.ProviderId, ct);
                }
                else
                {
                    var mismatch = SoftMerge(item, entry, provider.Capabilities.SupportsFavorites);
                    await _items.SaveAsync(item, ct, touchModifiedAt: false);
                    await UpsertLinkAsync(item.Id, provider.ProviderId, entry.ExternalId, LinkHash(item), mismatch, ct);
                    IndexItem(indexes, item);
                    imported++;
                    continue;
                }
            }

            if (TryFindIdentity(indexes, entry, out var vaultId))
            {
                var existing = await _items.GetByIdAsync(vaultId, ct);
                // Same provider already linked under another origin = distinct store rows; don't collapse
                // (that overwrote ExternalId and false-flagged password conflicts on clean imports).
                if (existing is not null && !existing.IsDeleted && !HasProviderLink(existing, provider.ProviderId))
                {
                    var mismatch = SoftMerge(existing, entry, provider.Capabilities.SupportsFavorites);
                    await _items.SaveAsync(existing, ct, touchModifiedAt: false);
                    await UpsertLinkAsync(vaultId, provider.ProviderId, entry.ExternalId, LinkHash(existing), mismatch, ct);
                    IndexItem(indexes, existing);
                    linkedExisting++;
                    continue;
                }

                if (existing is not null && !existing.IsDeleted && HasProviderLink(existing, provider.ProviderId))
                {
                    await SuppressOriginAsync(provider.ProviderId, entry.ExternalId, existing.Id, ct);
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

        var entries = ExcludeDeletedItemsFolders(await provider.ListEntriesAsync(ct), provider.ProviderId);
        ProviderSessionService.Instance.CacheRemoteEntries(provider.ProviderId, entries);

        var indexes = await BuildIdentityIndexesAsync(ct);
        var imported = 0;
        var linkedExisting = 0;
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            if (await IsOriginSuppressedAsync(provider.ProviderId, entry.ExternalId, ct))
                continue;

            var existingId = await FindLinkedItemIdAsync(provider.ProviderId, entry.ExternalId, ct);
            if (existingId is Guid linkedId)
            {
                var linked = await _items.GetByIdAsync(linkedId, ct);
                if (linked is not null && !linked.IsDeleted)
                    continue;

                // Link pointed at soft-deleted / missing item — strip and continue as unlinked.
                await RemoveLinkAsync(linkedId, provider.ProviderId, ct);
            }

            if (TryFindIdentity(indexes, entry, out var vaultId))
            {
                var existing = await _items.GetByIdAsync(vaultId, ct);
                // Same provider already linked under another origin = distinct store rows; don't collapse.
                if (existing is not null && !existing.IsDeleted && !HasProviderLink(existing, provider.ProviderId))
                {
                    var mismatch = SoftMerge(existing, entry, provider.Capabilities.SupportsFavorites);
                    await _items.SaveAsync(existing, ct, touchModifiedAt: false);
                    await UpsertLinkAsync(vaultId, provider.ProviderId, entry.ExternalId, LinkHash(existing), mismatch, ct);
                    IndexItem(indexes, existing);
                    linkedExisting++;
                    continue;
                }

                // Survivor already has this provider (typical after consolidate dropped a 2nd origin).
                // Do not spawn a duplicate vault row — suppress and skip.
                if (existing is not null && !existing.IsDeleted && HasProviderLink(existing, provider.ProviderId))
                {
                    await SuppressOriginAsync(provider.ProviderId, entry.ExternalId, existing.Id, ct);
                    LoggingConfiguration.LogInfo(
                        $"Skipped re-import of {provider.ProviderId}:{entry.ExternalId} — identity matches {existing.Id} which already has that provider (post-consolidate).",
                        nameof(VaultImportService));
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

        var entries = ExcludeDeletedItemsFolders(await provider.ListEntriesAsync(ct), provider.ProviderId);
        ProviderSessionService.Instance.CacheRemoteEntries(provider.ProviderId, entries);

        var result = new VaultSyncResult();
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            var existingId = await FindLinkedItemIdAsync(provider.ProviderId, entry.ExternalId, ct);
            if (existingId is null)
                continue;

            var local = await _items.GetByIdAsync(existingId.Value, ct);
            if (local is null || local.IsDeleted)
            {
                if (local?.IsDeleted == true)
                    await RemoveLinkAsync(local.Id, provider.ProviderId, ct);
                continue;
            }

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

    /// <summary>
    /// Drop provider entries whose Path sits under recycle/trash/deleted-items folders.
    /// </summary>
    private static IReadOnlyList<ProviderEntry> ExcludeDeletedItemsFolders(
        IReadOnlyList<ProviderEntry> entries,
        string providerId)
    {
        var kept = new List<ProviderEntry>(entries.Count);
        var skipped = 0;
        foreach (var entry in entries)
        {
            if (VaultConsolidateHints.IsDeletedItemsFolderPath(entry.Path))
            {
                skipped++;
                continue;
            }

            kept.Add(entry);
        }

        if (skipped > 0)
        {
            LoggingConfiguration.LogInfo(
                $"Skipped {skipped} {providerId} entr{(skipped == 1 ? "y" : "ies")} under recycle/trash/deleted folders.",
                nameof(VaultImportService));
        }

        return kept;
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
        // Adopt provider type when Shared still has the default Login from a prior blind import.
        if (vault.ItemType == VaultItemType.Login && entry.ItemType != VaultItemType.Login)
            vault.ItemType = entry.ItemType;
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

    /// <summary>
    /// Merge donor vault rows into <paramref name="survivorId"/>: apply field selection (incl. Folder),
    /// transfer ExternalLinks, soft-delete donors. With updateLinkedSources: push survivor (with Path),
    /// delete dropped same-provider origins remotely, create on missing providers from the merge set.
    /// </summary>
    public async Task<VaultMergeResult> MergeVaultItemsAsync(
        Guid survivorId,
        IReadOnlyList<Guid> donorIds,
        MergeFieldSelection selection,
        bool updateLinkedSources,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(donorIds);

        var donors = donorIds
            .Where(id => id != Guid.Empty && id != survivorId)
            .Distinct()
            .ToList();
        if (donors.Count == 0)
            throw new InvalidOperationException("Select at least one other vault item to merge.");

        var survivor = await _items.GetByIdAsync(survivorId, ct)
                       ?? throw new InvalidOperationException("Survivor vault item not found.");
        var donorItems = new List<VaultItem>();
        foreach (var id in donors)
        {
            var donor = await _items.GetByIdAsync(id, ct)
                        ?? throw new InvalidOperationException($"Donor vault item {id} not found.");
            if (donor.IsDeleted)
                throw new InvalidOperationException($"Donor '{donor.Title}' is already deleted.");
            donorItems.Add(donor);
        }

        var byId = new Dictionary<Guid, VaultItem> { [survivor.Id] = survivor };
        foreach (var d in donorItems)
            byId[d.Id] = d;

        var survivorLinksBefore = await GetLinksForItemAsync(survivorId, ct);
        var fieldsBefore = ReconcileHistoryService.CaptureSnapshot(survivor);
        fieldsBefore.Links = survivorLinksBefore
            .Where(l => !string.IsNullOrWhiteSpace(l.Provider))
            .Select(l => new ReconcileHistoryService.LinkSnapshot
            {
                Provider = l.Provider,
                ExternalId = l.ExternalId ?? "",
                IsMismatch = l.IsMismatch,
                ContentHash = l.ContentHash
            })
            .ToList();
        fieldsBefore.MergedDonors = donorItems.Select(ReconcileHistoryService.CaptureSnapshot).ToList();

        ApplyMergeFields(survivor, byId, selection);
        var canonicalPath = await ApplyMergeFolderAsync(survivor, byId, selection, ct);
        await _items.SaveAsync(survivor, ct, touchModifiedAt: true);

        var history = new ReconcileHistoryService();

        var result = new VaultMergeResult
        {
            SurvivorId = survivorId,
            DonorsDeleted = donorItems.Count,
            CanonicalPath = canonicalPath
        };

        var survivorProviders = new HashSet<string>(
            survivorLinksBefore
                .Where(l => !string.IsNullOrWhiteSpace(l.Provider))
                .Select(l => l.Provider),
            StringComparer.OrdinalIgnoreCase);

        // Providers that appeared anywhere in the merge set (for create-if-missing).
        var mergeProviders = new HashSet<string>(survivorProviders, StringComparer.OrdinalIgnoreCase);
        var droppedOrigins = new List<(string Provider, string ExternalId, string DonorTitle)>();

        foreach (var donor in donorItems)
        {
            var links = await GetLinksForItemAsync(donor.Id, ct);
            foreach (var link in links)
            {
                if (string.IsNullOrWhiteSpace(link.Provider) ||
                    string.Equals(link.Provider, "Shared", StringComparison.OrdinalIgnoreCase))
                {
                    await RemoveLinkAsync(donor.Id, link.Provider, ct);
                    continue;
                }

                mergeProviders.Add(link.Provider);

                if (survivorProviders.Contains(link.Provider))
                {
                    var originId = link.ExternalId?.Trim() ?? "";
                    if (!string.IsNullOrEmpty(originId))
                        droppedOrigins.Add((link.Provider, originId, donor.Title ?? ""));
                    await RemoveLinkAsync(donor.Id, link.Provider, ct);
                    result.LinksDroppedSameProvider++;
                    continue;
                }

                await MoveLinkAsync(donor.Id, survivorId, link.Provider, ct);
                survivorProviders.Add(link.Provider);
                result.LinksTransferred++;
            }

            // Never leave ExternalLinks on a soft-deleted donor — sync FindLinked would resurrect it.
            await RemoveAllLinksForItemAsync(donor.Id, ct);
            await _items.DeleteAsync(donor.Id, softDelete: true, ct);
        }

        await ClearMismatchesForItemAsync(survivorId, ct);

        var fieldSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (field, id) in selection.Sources)
            fieldSources[field] = id == survivorId ? "survivor" : ShortTitle(byId.GetValueOrDefault(id));
        foreach (var (field, ids) in selection.MultiSources)
            fieldSources[field] = "merge:" + string.Join("+", ids.Select(id =>
                id == survivorId ? "survivor" : ShortTitle(byId.GetValueOrDefault(id))));
        foreach (var field in selection.Overrides.Keys)
            fieldSources[field] = "edit";

        if (updateLinkedSources)
        {
            var reloaded = await _items.GetByIdAsync(survivorId, ct) ?? survivor;
            result.Push = await PushToLinkedProvidersAsync(reloaded, canonicalPath, ct);

            foreach (var dropped in droppedOrigins)
            {
                try
                {
                    var provider = await ProviderSessionService.Instance.EnsureConnectedAsync(dropped.Provider, ct);
                    if (!provider.Capabilities.SupportsDelete)
                    {
                        await SuppressOriginAsync(dropped.Provider, dropped.ExternalId, survivorId, ct);
                        result.Messages.Add(
                            $"{dropped.Provider}: delete not supported — suppressed origin from re-import.");
                        continue;
                    }

                    await provider.DeleteEntryAsync(dropped.ExternalId, ct);
                    result.OriginsDeleted++;
                    result.Messages.Add(
                        $"Deleted {dropped.Provider} duplicate from '{dropped.DonorTitle}' ({dropped.ExternalId}).");
                }
                catch (Exception ex)
                {
                    LoggingConfiguration.LogException(ex,
                        $"Delete dropped origin {dropped.Provider}:{dropped.ExternalId} failed.",
                        nameof(VaultImportService));
                    await SuppressOriginAsync(dropped.Provider, dropped.ExternalId, survivorId, ct);
                    result.Messages.Add(
                        $"{dropped.Provider}: delete failed ({ex.Message}) — suppressed so Sync won't resurrect.");
                }
            }

            // Create on connected write-back providers from the merge set that still lack a survivor link.
            var linksAfter = await GetLinksForItemAsync(survivorId, ct);
            var linkedProviders = new HashSet<string>(
                linksAfter.Where(l => !string.IsNullOrWhiteSpace(l.Provider)).Select(l => l.Provider),
                StringComparer.OrdinalIgnoreCase);

            foreach (var providerId in mergeProviders)
            {
                if (linkedProviders.Contains(providerId))
                    continue;
                try
                {
                    var provider = await ProviderSessionService.Instance.EnsureConnectedAsync(providerId, ct);
                    if (!provider.Capabilities.SupportsCreate || !provider.Capabilities.SupportsWriteBack)
                    {
                        result.Messages.Add($"{providerId}: create skipped (not supported or not write-capable).");
                        continue;
                    }

                    var created = ToProviderEntry(reloaded, externalId: "", canonicalPath);
                    var newId = await provider.CreateEntryAsync(created, ct);
                    var hash = VaultItemService.ContentHash(reloaded);
                    await UpsertLinkAsync(survivorId, providerId, newId, hash, mismatch: false, ct);
                    ProviderSessionService.Instance.CacheRemoteEntry(providerId, created);
                    linkedProviders.Add(providerId);
                    result.OriginsCreated++;
                    result.Messages.Add($"Created {provider.DisplayName} entry ({newId}) under '{canonicalPath ?? "(root)"}'.");
                }
                catch (Exception ex)
                {
                    LoggingConfiguration.LogException(ex, $"Create on {providerId} failed for {survivorId}.",
                        nameof(VaultImportService));
                    result.Messages.Add($"{providerId}: create failed ({ex.Message}).");
                }
            }
        }
        else
        {
            // Vault only — suppress dropped origins so Sync cannot recreate Shared rows.
            foreach (var dropped in droppedOrigins)
            {
                await SuppressOriginAsync(dropped.Provider, dropped.ExternalId, survivorId, ct);
                result.Messages.Add(
                    $"Vault only: suppressed {dropped.Provider} origin (remote duplicate still exists until Update all).");
            }
        }

        var summary =
            $"Merge ← {donorItems.Count} donor(s); links+{result.LinksTransferred}/drop{result.LinksDroppedSameProvider}" +
            $"; del{result.OriginsDeleted}/new{result.OriginsCreated}" +
            (updateLinkedSources ? "; push=yes" : "; push=no") +
            (string.IsNullOrEmpty(canonicalPath) ? "" : $"; folder={canonicalPath}");
        await history.RecordApplyAsync(
            survivorId,
            fieldsBefore,
            fieldSources,
            pushedToProviders: updateLinkedSources,
            providerIds: survivorProviders,
            summary: summary.Length <= 500 ? summary : summary[..497] + "…",
            ct: ct);

        ProviderSessionService.Instance.ClearRemoteCache();
        LoggingConfiguration.LogInfo(
            $"Merged {donorItems.Count} donor(s) into {survivorId} (links+{result.LinksTransferred}, drop={result.LinksDroppedSameProvider}, deleted={result.OriginsDeleted}, created={result.OriginsCreated}, path='{canonicalPath}').",
            nameof(VaultImportService));
        return result;
    }

    /// <summary>
    /// Suggested duplicate groups: exact username+host (≥2), plus username+registrable-domain cousins
    /// that span multiple hosts.
    /// </summary>
    public static List<ConsolidateSuggestGroup> BuildSuggestedGroups(IEnumerable<VaultItem> items)
    {
        var live = items.Where(i => !i.IsDeleted).ToList();
        var groups = new List<ConsolidateSuggestGroup>();
        var coveredExact = new HashSet<Guid>();

        var byExact = live
            .Select(i => (Item: i, Key: UserDomainKey(i.Username, i.Url)))
            .Where(t => t.Key is not null)
            .GroupBy(t => t.Key!, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() >= 2)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var g in byExact)
        {
            var list = g.Select(x => x.Item).OrderBy(i => i.Title, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var i in list)
                coveredExact.Add(i.Id);
            var user = (list[0].Username ?? "").Trim();
            var host = NormalizeDomain(list[0].Url) ?? "?";
            groups.Add(new ConsolidateSuggestGroup
            {
                Key = "exact:" + g.Key,
                Label = $"{user} @ {host} ({list.Count})",
                IsExactHost = true,
                Items = list
            });
        }

        var byReg = live
            .Select(i =>
            {
                var user = (i.Username ?? "").Trim();
                var host = NormalizeDomain(i.Url);
                var reg = VaultDomain.RegistrableDomain(host);
                string? key = null;
                if (!string.IsNullOrEmpty(user) && !string.IsNullOrEmpty(reg))
                    key = $"{user}\n{reg}";
                return (Item: i, Key: key, Host: host, Reg: reg);
            })
            .Where(t => t.Key is not null)
            .GroupBy(t => t.Key!, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() >= 2)
            .Where(g => g.Select(x => x.Host).Distinct(StringComparer.OrdinalIgnoreCase).Count() >= 2)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var g in byReg)
        {
            var list = g.Select(x => x.Item).OrderBy(i => i.Title, StringComparer.OrdinalIgnoreCase).ToList();
            // Skip if every member is already in some exact-host group of size ≥2 (noise).
            if (list.All(i => coveredExact.Contains(i.Id)))
                continue;
            var user = (list[0].Username ?? "").Trim();
            var reg = g.First().Reg ?? "?";
            groups.Add(new ConsolidateSuggestGroup
            {
                Key = "reg:" + g.Key,
                Label = $"{user} @ *.{reg} ({list.Count})",
                IsExactHost = false,
                Items = list
            });
        }

        return groups;
    }

    /// <summary>True when any non-empty donor field differs from survivor (field picker needed).</summary>
    public static bool MergeNeedsFieldResolve(VaultItem survivor, IReadOnlyList<VaultItem> donors)
    {
        foreach (var donor in donors)
        {
            if (FieldDiffers(survivor.Title, donor.Title) ||
                FieldDiffers(survivor.Username, donor.Username) ||
                FieldDiffers(survivor.Password, donor.Password) ||
                FieldDiffers(survivor.Url, donor.Url) ||
                FieldDiffers(survivor.Notes, donor.Notes) ||
                FieldDiffers(survivor.TotpSecret, donor.TotpSecret) ||
                survivor.IsFavorite != donor.IsFavorite)
                return true;
        }

        return false;
    }

    public static string? PublicUserDomainKey(string? username, string? url) => UserDomainKey(username, url);

    private static bool FieldDiffers(string? a, string? b)
    {
        var left = (a ?? "").Trim();
        var right = (b ?? "").Trim();
        if (string.IsNullOrEmpty(right))
            return false;
        if (string.IsNullOrEmpty(left))
            return true;
        return !string.Equals(left, right, StringComparison.Ordinal);
    }

    private static void ApplyMergeFields(
        VaultItem survivor,
        IReadOnlyDictionary<Guid, VaultItem> byId,
        MergeFieldSelection selection)
    {
        static string? Raw(string field, VaultItem src) => field switch
        {
            "Title" => src.Title,
            "Username" => src.Username,
            "Password" => src.Password,
            "URL" => src.Url,
            "Notes" => src.Notes,
            "TOTP" => src.TotpSecret,
            "Favorite" => src.IsFavorite.ToString(),
            "Folder" => VaultConsolidateHints.FolderDisplayName(src)
                        ?? VaultConsolidateHints.RichestProviderPath(src),
            _ => null
        };

        string? Pick(string field)
        {
            if (selection.TryGetOverride(field, out var edited) && edited is not null)
                return edited;

            if (selection.TryGetMulti(field, out var ids) && ids.Count > 0)
            {
                var parts = new List<string>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var id in ids)
                {
                    if (!byId.TryGetValue(id, out var src))
                        continue;
                    var raw = (Raw(field, src) ?? "").Trim();
                    if (string.IsNullOrEmpty(raw) || !seen.Add(raw))
                        continue;
                    parts.Add(raw);
                }

                if (parts.Count == 0)
                    return null;
                // Notes / multi-line friendly; single-line fields join with " | ".
                return field is "Notes"
                    ? string.Join(Environment.NewLine + Environment.NewLine, parts)
                    : string.Join(" | ", parts);
            }

            var pickId = selection.Get(field, survivor.Id);
            if (!byId.TryGetValue(pickId, out var one))
                one = survivor;
            return Raw(field, one);
        }

        survivor.Title = Pick("Title") ?? survivor.Title ?? "";
        survivor.Username = Pick("Username");
        survivor.Password = Pick("Password");
        survivor.Url = Pick("URL");
        survivor.Notes = Pick("Notes");
        var totp = Pick("TOTP");
        if (!string.IsNullOrWhiteSpace(totp))
            survivor.TotpSecret = totp;
        if (bool.TryParse(Pick("Favorite"), out var fav))
            survivor.IsFavorite = fav;
    }

    /// <summary>
    /// Resolve Folder selection onto survivor.FolderId and return the canonical path string for providers.
    /// </summary>
    private async Task<string?> ApplyMergeFolderAsync(
        VaultItem survivor,
        IReadOnlyDictionary<Guid, VaultItem> byId,
        MergeFieldSelection selection,
        CancellationToken ct)
    {
        string? folderName = null;
        if (selection.TryGetOverride("Folder", out var edited) && edited is not null)
            folderName = edited.Trim();
        else
        {
            var pickId = selection.Get("Folder", survivor.Id);
            if (byId.TryGetValue(pickId, out var src))
            {
                folderName = VaultConsolidateHints.FolderDisplayName(src)
                             ?? VaultConsolidateHints.RichestProviderPath(src);
                // If picking survivor/donor that only has FolderId without navigation loaded:
                if (string.IsNullOrWhiteSpace(folderName) && src.FolderId is int fid && fid != 0)
                {
                    var folders = await _items.GetFoldersAsync(ct);
                    folderName = folders.FirstOrDefault(f => f.Id == fid)?.Name;
                }
            }
        }

        folderName = VaultConsolidateHints.NormalizeCanonicalPath(folderName);
        var folder = await _items.EnsureFolderByNameAsync(folderName, ct);
        survivor.FolderId = folder?.Id;
        survivor.Folder = folder;
        return folderName ?? folder?.Name;
    }

    private static string ShortTitle(VaultItem? item)
    {
        var t = (item?.Title ?? "").Trim();
        return string.IsNullOrEmpty(t) ? "donor" : (t.Length <= 24 ? t : t[..21] + "…");
    }

    private static async Task MoveLinkAsync(
        Guid fromItemId,
        Guid toItemId,
        string provider,
        CancellationToken ct)
    {
        async Task Move(DbContext db)
        {
            var links = db.Set<ExternalLink>();
            var link = await links.FirstOrDefaultAsync(
                l => l.ItemId == fromItemId && l.Provider == provider, ct);
            if (link is null)
                return;

            var clash = await links.FirstOrDefaultAsync(
                l => l.ItemId == toItemId && l.Provider == provider, ct);
            if (clash is not null)
            {
                links.Remove(link);
            }
            else
            {
                link.ItemId = toItemId;
                link.LastSyncedAt = DateTime.UtcNow;
            }

            await db.SaveChangesAsync(ct);
        }

        if (!ConnectionConfig.IsOfflineMode)
        {
            await using var db = new AuthenticatorDbContext();
            await Move(db);
        }

        try
        {
            await using var offline = new OfflineVaultDbContext();
            await Move(offline);
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Offline move ExternalLink failed.", nameof(VaultImportService));
        }
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
    public Task<VaultPushResult> PushToLinkedProvidersAsync(VaultItem item, CancellationToken ct = default) =>
        PushToLinkedProvidersAsync(item, canonicalPath: null, ct);

    public async Task<VaultPushResult> PushToLinkedProvidersAsync(
        VaultItem item,
        string? canonicalPath,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        var result = new VaultPushResult();
        var links = await GetLinksForItemAsync(item.Id, ct);
        result.LinkCount = links.Count;
        if (links.Count == 0)
            return result;

        var path = canonicalPath
                   ?? VaultConsolidateHints.NormalizeCanonicalPath(item.Folder?.Name)
                   ?? VaultConsolidateHints.RichestProviderPath(item);

        // Resolve Folder name when navigation wasn't loaded.
        if (path is null && item.FolderId is int fid && fid != 0)
        {
            var folders = await _items.GetFoldersAsync(ct);
            path = VaultConsolidateHints.NormalizeCanonicalPath(
                folders.FirstOrDefault(f => f.Id == fid)?.Name);
        }

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
                var pushed = ToProviderEntry(item, originId, path);
                await provider.UpdateEntryAsync(pushed, ct);
                // Keep Diff/scan cache aligned so a post-Apply reload does not re-flag this row.
                ProviderSessionService.Instance.CacheRemoteEntry(link.Provider, pushed);
                await UpsertLinkAsync(item.Id, link.Provider, originId, hash, mismatch: false, ct);
                result.Updated++;
                LoggingConfiguration.LogInfo(
                    $"Pushed Shared→{link.Provider} for {item.Id} (origin id {originId}, path='{path}').",
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

    private static ProviderEntry ToProviderEntry(VaultItem item, string externalId, string? path = null) =>
        new()
        {
            ExternalId = externalId,
            Title = item.Title ?? "",
            Username = item.Username,
            Password = item.Password,
            Url = item.Url,
            Notes = item.Notes,
            Totp = item.TotpSecret,
            Path = path,
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

        // Prefer a live (non-deleted) vault row. Soft-deleted donors must not win origin lookup.
        async Task<Guid?> FindInDb(DbContext db)
        {
            var links = await db.Set<ExternalLink>().AsNoTracking()
                .Where(l => l.Provider == provider)
                .ToListAsync(ct);
            var match = links.FirstOrDefault(l =>
                string.Equals(
                    ProviderSessionService.NormalizeExternalId(l.ExternalId),
                    norm,
                    StringComparison.Ordinal));
            if (match is null)
                return null;

            var live = await db.Set<VaultItem>().AsNoTracking()
                .Where(i => i.Id == match.ItemId && !i.IsDeleted)
                .Select(i => (Guid?)i.Id)
                .FirstOrDefaultAsync(ct);
            return live;
        }

        if (ConnectionConfig.IsOfflineMode)
        {
            await using var offline = new OfflineVaultDbContext();
            return await FindInDb(offline);
        }

        await using var shared = new AuthenticatorDbContext();
        return await FindInDb(shared);
    }

    private static async Task SuppressOriginAsync(
        string provider,
        string? externalId,
        Guid mergedIntoItemId,
        CancellationToken ct)
    {
        var raw = (externalId ?? "").Trim();
        var norm = ProviderSessionService.NormalizeExternalId(raw);
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrEmpty(norm))
            return;

        async Task Upsert(DbContext db)
        {
            var set = db.Set<SuppressedExternalOrigin>();
            var existing = await set.FirstOrDefaultAsync(
                s => s.Provider == provider && s.ExternalIdNormalized == norm, ct);
            if (existing is null)
            {
                set.Add(new SuppressedExternalOrigin
                {
                    Provider = provider,
                    ExternalIdNormalized = norm,
                    ExternalIdRaw = raw,
                    MergedIntoItemId = mergedIntoItemId,
                    CreatedAtUtc = DateTime.UtcNow
                });
            }
            else
            {
                existing.MergedIntoItemId = mergedIntoItemId;
                existing.ExternalIdRaw = raw;
                existing.CreatedAtUtc = DateTime.UtcNow;
            }

            await db.SaveChangesAsync(ct);
        }

        if (!ConnectionConfig.IsOfflineMode)
        {
            await new VaultMigrationService().EnsureSchemaOnlyAsync(ct);
            await using var db = new AuthenticatorDbContext();
            await Upsert(db);
        }

        try
        {
            await new OfflineVaultService().EnsureCreatedAsync(ct);
            await using var offline = new OfflineVaultDbContext();
            await Upsert(offline);
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Offline suppress origin failed.", nameof(VaultImportService));
        }
    }

    private static async Task<bool> IsOriginSuppressedAsync(
        string provider,
        string? externalId,
        CancellationToken ct)
    {
        var norm = ProviderSessionService.NormalizeExternalId(externalId);
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrEmpty(norm))
            return false;

        async Task<bool> Exists(DbContext db) =>
            await db.Set<SuppressedExternalOrigin>().AsNoTracking()
                .AnyAsync(s => s.Provider == provider && s.ExternalIdNormalized == norm, ct);

        try
        {
            if (ConnectionConfig.IsOfflineMode)
            {
                await new OfflineVaultService().EnsureCreatedAsync(ct);
                await using var offline = new OfflineVaultDbContext();
                return await Exists(offline);
            }

            await new VaultMigrationService().EnsureSchemaOnlyAsync(ct);
            await using var db = new AuthenticatorDbContext();
            return await Exists(db);
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Suppress origin lookup failed.", nameof(VaultImportService));
            return false;
        }
    }

    private static async Task RemoveAllLinksForItemAsync(Guid itemId, CancellationToken ct)
    {
        async Task Wipe(DbContext db)
        {
            await db.Set<ExternalLink>().Where(l => l.ItemId == itemId).ExecuteDeleteAsync(ct);
        }

        if (!ConnectionConfig.IsOfflineMode)
        {
            await using var db = new AuthenticatorDbContext();
            await Wipe(db);
        }

        try
        {
            await using var offline = new OfflineVaultDbContext();
            await Wipe(offline);
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Offline remove-all links failed.", nameof(VaultImportService));
        }
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
