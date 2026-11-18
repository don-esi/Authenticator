using System.Text.Json;
using Authenticator.Crypto;
using Authenticator.Data;
using Authenticator.Data.Models;
using Authenticator.Logging;
using Authenticator.Services.Providers;
using Microsoft.EntityFrameworkCore;

namespace Authenticator.Services;

/// <summary>
/// Persists Diff Apply snapshots for per-item / global undo (vault restore; optional provider push-back).
/// </summary>
public sealed class ReconcileHistoryService
{
    public const int MaxPerItem = 20;
    public const int MaxGlobalList = 50;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly ISecretProtector _secrets;
    private readonly VaultItemService _items;
    private readonly VaultImportService _import;

    public ReconcileHistoryService(
        ISecretProtector? secrets = null,
        VaultItemService? items = null,
        VaultImportService? import = null)
    {
        _secrets = secrets ?? new SecretProtector();
        _items = items ?? new VaultItemService();
        _import = import ?? new VaultImportService();
    }

    public sealed class VaultSnapshot
    {
        public Guid Id { get; set; }
        public string Title { get; set; } = "";
        public string? Username { get; set; }
        public string? Password { get; set; }
        public string? Url { get; set; }
        public string? Notes { get; set; }
        public string? TotpSecret { get; set; }
        public bool IsFavorite { get; set; }
        public List<LinkSnapshot> Links { get; set; } = new();
    }

    public sealed class LinkSnapshot
    {
        public string Provider { get; set; } = "";
        public string ExternalId { get; set; } = "";
        public bool IsMismatch { get; set; }
        public string? ContentHash { get; set; }
    }

    public static VaultSnapshot CaptureSnapshot(VaultItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new VaultSnapshot
        {
            Id = item.Id,
            Title = item.Title ?? "",
            Username = item.Username,
            Password = item.Password,
            Url = item.Url,
            Notes = item.Notes,
            TotpSecret = item.TotpSecret,
            IsFavorite = item.IsFavorite,
            Links = (item.ExternalLinks ?? Array.Empty<ExternalLink>())
                .Where(l => !string.IsNullOrWhiteSpace(l.Provider))
                .Select(l => new LinkSnapshot
                {
                    Provider = l.Provider,
                    ExternalId = l.ExternalId ?? "",
                    IsMismatch = l.IsMismatch,
                    ContentHash = l.ContentHash
                })
                .ToList()
        };
    }

    public static string BuildSummary(
        IReadOnlyDictionary<string, string> fieldSources,
        bool pushed)
    {
        var parts = fieldSources
            .Where(kv => !string.Equals(kv.Key, "Created", StringComparison.OrdinalIgnoreCase) &&
                         !string.Equals(kv.Key, "Updated", StringComparison.OrdinalIgnoreCase) &&
                         !string.Equals(kv.Key, "Host", StringComparison.OrdinalIgnoreCase))
            .Where(kv => !string.Equals(kv.Value, "vault", StringComparison.OrdinalIgnoreCase))
            .Select(kv => $"{kv.Key}←{kv.Value}")
            .Take(6)
            .ToList();

        if (parts.Count == 0)
            parts.Add("Vault selections");

        var summary = string.Join("; ", parts);
        summary += pushed ? "; push=yes" : "; push=no";
        return summary.Length <= 500 ? summary : summary[..497] + "…";
    }

    public async Task<ReconcileHistoryEntry> RecordApplyAsync(
        Guid itemId,
        VaultSnapshot before,
        IReadOnlyDictionary<string, string> fieldSources,
        bool pushedToProviders,
        IEnumerable<string> providerIds,
        string? summary = null,
        CancellationToken ct = default)
    {
        var sealedJson = SealSnapshotJson(before);
        var entry = new ReconcileHistoryEntry
        {
            Id = Guid.NewGuid(),
            ItemId = itemId,
            CreatedAtUtc = DateTime.UtcNow,
            Summary = summary ?? BuildSummary(fieldSources, pushedToProviders),
            BeforeJson = sealedJson,
            FieldSourcesJson = JsonSerializer.Serialize(fieldSources, JsonOptions),
            PushedToProviders = pushedToProviders,
            ProviderIds = string.Join(",", providerIds.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase))
        };

        await InsertAsync(entry, ct);
        await PruneForItemAsync(itemId, ct);
        LoggingConfiguration.LogInfo(
            $"Reconcile history recorded for {itemId}: {entry.Summary}",
            nameof(ReconcileHistoryService));
        return entry;
    }

    public async Task<List<ReconcileHistoryEntry>> ListForItemAsync(
        Guid itemId,
        int take = MaxPerItem,
        CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, 100);
        if (ConnectionConfig.IsOfflineMode)
        {
            await using var offline = new OfflineVaultDbContext();
            return await offline.ReconcileHistory
                .AsNoTracking()
                .Where(h => h.ItemId == itemId)
                .OrderByDescending(h => h.CreatedAtUtc)
                .Take(take)
                .ToListAsync(ct);
        }

        await using var db = new AuthenticatorDbContext();
        return await db.ReconcileHistory
            .AsNoTracking()
            .Where(h => h.ItemId == itemId)
            .OrderByDescending(h => h.CreatedAtUtc)
            .Take(take)
            .ToListAsync(ct);
    }

    public async Task<List<ReconcileHistoryEntry>> ListRecentAsync(
        int take = MaxGlobalList,
        CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, 200);
        List<ReconcileHistoryEntry> rows;
        if (ConnectionConfig.IsOfflineMode)
        {
            await using var offline = new OfflineVaultDbContext();
            rows = await offline.ReconcileHistory
                .AsNoTracking()
                .OrderByDescending(h => h.CreatedAtUtc)
                .Take(take)
                .ToListAsync(ct);
        }
        else
        {
            await using var db = new AuthenticatorDbContext();
            rows = await db.ReconcileHistory
                .AsNoTracking()
                .OrderByDescending(h => h.CreatedAtUtc)
                .Take(take)
                .ToListAsync(ct);
        }

        if (rows.Count == 0)
            return rows;

        var ids = rows.Select(r => r.ItemId).Distinct().ToList();
        Dictionary<Guid, string> titles;
        if (ConnectionConfig.IsOfflineMode)
        {
            await using var offline = new OfflineVaultDbContext();
            titles = await offline.VaultItems.AsNoTracking()
                .Where(i => ids.Contains(i.Id))
                .ToDictionaryAsync(i => i.Id, i => i.Title, ct);
        }
        else
        {
            await using var db = new AuthenticatorDbContext();
            titles = await db.VaultItems.AsNoTracking()
                .Where(i => ids.Contains(i.Id))
                .ToDictionaryAsync(i => i.Id, i => i.Title, ct);
        }

        foreach (var row in rows)
            row.ItemTitle = titles.GetValueOrDefault(row.ItemId, "(missing item)");

        return rows;
    }

    public async Task<VaultItem> UndoAsync(
        Guid historyId,
        bool pushToProviders,
        CancellationToken ct = default)
    {
        var entry = await GetByIdAsync(historyId, ct)
                    ?? throw new InvalidOperationException("History entry not found.");
        if (entry.UndoneAtUtc is not null)
            throw new InvalidOperationException("This reconcile was already undone.");

        var snapshot = UnsealSnapshotJson(entry.BeforeJson);
        var item = await _items.GetByIdAsync(entry.ItemId, ct)
                   ?? new VaultItem { Id = entry.ItemId == Guid.Empty ? snapshot.Id : entry.ItemId };

        item.Title = snapshot.Title ?? "";
        item.Username = snapshot.Username;
        item.Password = snapshot.Password;
        item.Url = snapshot.Url;
        item.Notes = snapshot.Notes;
        item.TotpSecret = snapshot.TotpSecret;
        item.IsFavorite = snapshot.IsFavorite;
        item.IsDeleted = false;

        await _items.SaveAsync(item, ct);
        await RestoreLinkFlagsAsync(item.Id, snapshot.Links, ct);

        // Re-load plaintext for optional push.
        item = await _items.GetByIdAsync(item.Id, ct) ?? item;

        if (pushToProviders)
            await _import.PushToLinkedProvidersAsync(item, ct);

        await MarkUndoneAsync(historyId, ct);
        LoggingConfiguration.LogInfo(
            $"Reconcile undo applied for item {item.Id} (history {historyId}, push={pushToProviders}).",
            nameof(ReconcileHistoryService));
        return item;
    }

    private string SealSnapshotJson(VaultSnapshot snapshot)
    {
        var copy = new VaultSnapshot
        {
            Id = snapshot.Id,
            Title = snapshot.Title,
            Username = snapshot.Username,
            Password = SealValue(snapshot.Password),
            Url = snapshot.Url,
            Notes = SealValue(snapshot.Notes),
            TotpSecret = SealValue(snapshot.TotpSecret),
            IsFavorite = snapshot.IsFavorite,
            Links = snapshot.Links.Select(l => new LinkSnapshot
            {
                Provider = l.Provider,
                ExternalId = l.ExternalId,
                IsMismatch = l.IsMismatch,
                ContentHash = l.ContentHash
            }).ToList()
        };
        return JsonSerializer.Serialize(copy, JsonOptions);
    }

    private VaultSnapshot UnsealSnapshotJson(string json)
    {
        var snapshot = JsonSerializer.Deserialize<VaultSnapshot>(json, JsonOptions)
                       ?? throw new InvalidOperationException("Invalid reconcile snapshot.");
        snapshot.Password = UnsealValue(snapshot.Password);
        snapshot.Notes = UnsealValue(snapshot.Notes);
        snapshot.TotpSecret = UnsealValue(snapshot.TotpSecret);
        return snapshot;
    }

    private string? SealValue(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return value;
        return _secrets.IsEncrypted(value) ? value : _secrets.Protect(value);
    }

    private string? UnsealValue(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return value;
        try
        {
            return _secrets.IsEncrypted(value) ? _secrets.Unprotect(value) : value;
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Failed to unseal reconcile snapshot secret.", nameof(ReconcileHistoryService));
            return value;
        }
    }

    private async Task InsertAsync(ReconcileHistoryEntry entry, CancellationToken ct)
    {
        if (!ConnectionConfig.IsOfflineMode)
        {
            await using var db = new AuthenticatorDbContext();
            db.ReconcileHistory.Add(entry);
            await db.SaveChangesAsync(ct);
        }

        try
        {
            await using var offline = new OfflineVaultDbContext();
            // Avoid tracking conflict when already inserted into shared then mirrored.
            var clone = CloneEntry(entry);
            offline.ReconcileHistory.Add(clone);
            await offline.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Offline reconcile history mirror failed.", nameof(ReconcileHistoryService));
        }
    }

    private async Task PruneForItemAsync(Guid itemId, CancellationToken ct)
    {
        await PruneInDbAsync(() => new AuthenticatorDbContext(), itemId, !ConnectionConfig.IsOfflineMode, ct);
        try
        {
            await PruneInDbAsync(() => new OfflineVaultDbContext(), itemId, true, ct);
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Offline reconcile history prune failed.", nameof(ReconcileHistoryService));
        }
    }

    private static async Task PruneInDbAsync(
        Func<DbContext> factory,
        Guid itemId,
        bool enabled,
        CancellationToken ct)
    {
        if (!enabled)
            return;

        await using var db = factory();
        var set = db.Set<ReconcileHistoryEntry>();
        var keepIds = await set.AsNoTracking()
            .Where(h => h.ItemId == itemId)
            .OrderByDescending(h => h.CreatedAtUtc)
            .Take(MaxPerItem)
            .Select(h => h.Id)
            .ToListAsync(ct);
        if (keepIds.Count < MaxPerItem)
            return;

        await set.Where(h => h.ItemId == itemId && !keepIds.Contains(h.Id)).ExecuteDeleteAsync(ct);
    }

    private async Task<ReconcileHistoryEntry?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        if (!ConnectionConfig.IsOfflineMode)
        {
            await using var db = new AuthenticatorDbContext();
            var row = await db.ReconcileHistory.AsNoTracking().FirstOrDefaultAsync(h => h.Id == id, ct);
            if (row is not null)
                return row;
        }

        await using var offline = new OfflineVaultDbContext();
        return await offline.ReconcileHistory.AsNoTracking().FirstOrDefaultAsync(h => h.Id == id, ct);
    }

    private async Task MarkUndoneAsync(Guid historyId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        if (!ConnectionConfig.IsOfflineMode)
        {
            await using var db = new AuthenticatorDbContext();
            var row = await db.ReconcileHistory.FirstOrDefaultAsync(h => h.Id == historyId, ct);
            if (row is not null)
            {
                row.UndoneAtUtc = now;
                await db.SaveChangesAsync(ct);
            }
        }

        try
        {
            await using var offline = new OfflineVaultDbContext();
            var row = await offline.ReconcileHistory.FirstOrDefaultAsync(h => h.Id == historyId, ct);
            if (row is not null)
            {
                row.UndoneAtUtc = now;
                await offline.SaveChangesAsync(ct);
            }
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Offline mark-undone failed.", nameof(ReconcileHistoryService));
        }
    }

    private async Task RestoreLinkFlagsAsync(
        Guid itemId,
        IReadOnlyList<LinkSnapshot> links,
        CancellationToken ct)
    {
        foreach (var link in links)
        {
            if (string.IsNullOrWhiteSpace(link.Provider) || string.IsNullOrWhiteSpace(link.ExternalId))
                continue;
            await UpsertLinkMismatchAsync(
                itemId,
                link.Provider,
                link.ExternalId,
                link.ContentHash,
                link.IsMismatch,
                ct);
        }
    }

    private static async Task UpsertLinkMismatchAsync(
        Guid itemId,
        string provider,
        string externalId,
        string? contentHash,
        bool isMismatch,
        CancellationToken ct)
    {
        async Task Upsert(DbContext db)
        {
            var set = db.Set<ExternalLink>();
            var existing = await set.FirstOrDefaultAsync(
                l => l.ItemId == itemId && l.Provider == provider, ct);
            if (existing is null)
            {
                set.Add(new ExternalLink
                {
                    ItemId = itemId,
                    Provider = provider,
                    ExternalId = externalId,
                    ContentHash = contentHash,
                    IsMismatch = isMismatch,
                    LastSyncedAt = DateTime.UtcNow
                });
            }
            else
            {
                existing.ExternalId = externalId;
                existing.ContentHash = contentHash ?? existing.ContentHash;
                existing.IsMismatch = isMismatch;
                existing.LastSyncedAt = DateTime.UtcNow;
            }

            await db.SaveChangesAsync(ct);
        }

        if (!ConnectionConfig.IsOfflineMode)
        {
            await using var db = new AuthenticatorDbContext();
            await Upsert(db);
        }

        try
        {
            await using var offline = new OfflineVaultDbContext();
            await Upsert(offline);
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Offline restore link flags failed.", nameof(ReconcileHistoryService));
        }
    }

    private static ReconcileHistoryEntry CloneEntry(ReconcileHistoryEntry e) =>
        new()
        {
            Id = e.Id,
            ItemId = e.ItemId,
            CreatedAtUtc = e.CreatedAtUtc,
            Summary = e.Summary,
            BeforeJson = e.BeforeJson,
            FieldSourcesJson = e.FieldSourcesJson,
            PushedToProviders = e.PushedToProviders,
            ProviderIds = e.ProviderIds,
            UndoneAtUtc = e.UndoneAtUtc
        };
}
