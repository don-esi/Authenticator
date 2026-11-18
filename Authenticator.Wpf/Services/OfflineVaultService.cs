using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Authenticator.Data;
using Authenticator.Data.Models;
using Authenticator.Logging;
using Microsoft.EntityFrameworkCore;

namespace Authenticator.Services;

/// <summary>
/// Local encrypted offline backup (SQLite) + change queue.
/// </summary>
public sealed class OfflineVaultService
{
    private readonly ISecretProtector _secrets;
    private static readonly string SeedFingerprintPath = CreateSeedFingerprintPath();

    public OfflineVaultService(ISecretProtector? secrets = null)
    {
        _secrets = secrets ?? new SecretProtector();
    }

    private static string CreateSeedFingerprintPath()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Authenticator");
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, "offline-seed-fingerprint.txt");
    }

    public async Task EnsureCreatedAsync(CancellationToken ct = default)
    {
        await using var db = new OfflineVaultDbContext();
        await db.Database.EnsureCreatedAsync(ct);
        await EnsureLastUsedAtColumnAsync(db, ct);
        await EnsureReconcileHistoryTableAsync(db, ct);
    }

    private static async Task EnsureReconcileHistoryTableAsync(OfflineVaultDbContext db, CancellationToken ct)
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE IF NOT EXISTS ReconcileHistory (
                  Id TEXT NOT NULL PRIMARY KEY,
                  ItemId TEXT NOT NULL,
                  CreatedAtUtc TEXT NOT NULL,
                  Summary TEXT NULL,
                  BeforeJson TEXT NOT NULL,
                  FieldSourcesJson TEXT NULL,
                  PushedToProviders INTEGER NOT NULL,
                  ProviderIds TEXT NULL,
                  UndoneAtUtc TEXT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_ReconcileHistory_ItemId ON ReconcileHistory (ItemId);
                CREATE INDEX IF NOT EXISTS IX_ReconcileHistory_CreatedAtUtc ON ReconcileHistory (CreatedAtUtc);
                """,
                ct);
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Failed to ensure offline ReconcileHistory table.", nameof(OfflineVaultService));
        }
    }

    private static async Task EnsureLastUsedAtColumnAsync(OfflineVaultDbContext db, CancellationToken ct)
    {
        try
        {
            var connection = db.Database.GetDbConnection();
            if (connection.State != System.Data.ConnectionState.Open)
                await connection.OpenAsync(ct);

            await using var check = connection.CreateCommand();
            check.CommandText = "PRAGMA table_info(VaultItems)";
            await using var reader = await check.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (string.Equals(reader.GetString(1), "LastUsedAt", StringComparison.OrdinalIgnoreCase))
                    return;
            }
            await reader.DisposeAsync();

            await db.Database.ExecuteSqlRawAsync("ALTER TABLE VaultItems ADD COLUMN LastUsedAt TEXT NULL", ct);
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Failed to ensure offline VaultItems.LastUsedAt column.", nameof(OfflineVaultService));
        }
    }

    public async Task TouchLastUsedAsync(Guid id, DateTime usedAtUtc, CancellationToken ct = default)
    {
        try
        {
            await EnsureCreatedAsync(ct);
            await using var db = new OfflineVaultDbContext();
            var item = await db.VaultItems.FirstOrDefaultAsync(i => i.Id == id, ct);
            if (item is null)
                return;
            item.LastUsedAt = usedAtUtc;
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, $"Touch last-used failed for offline item {id}.", nameof(OfflineVaultService));
        }
    }

    public async Task SetFavoriteAsync(Guid id, bool favorite, DateTime modifiedAtUtc, CancellationToken ct = default)
    {
        await EnsureCreatedAsync(ct);
        await using var db = new OfflineVaultDbContext();
        var item = await db.VaultItems.FirstOrDefaultAsync(i => i.Id == id, ct);
        if (item is null)
            return;
        item.IsFavorite = favorite;
        item.ModifiedAt = modifiedAtUtc;
        await db.SaveChangesAsync(ct);
    }

    public async Task SeedFromSharedAsync(CancellationToken ct = default)
    {
        await EnsureCreatedAsync(ct);
        if (ConnectionConfig.IsOfflineMode)
            return;

        try
        {
            await using var shared = new AuthenticatorDbContext();

            // Cheap fingerprint: counts + newest ModifiedAt — skip full truncate/reload when unchanged.
            var fingerprint = await ComputeSharedFingerprintAsync(shared, ct);
            if (string.Equals(ReadSeedFingerprint(), fingerprint, StringComparison.Ordinal))
            {
                LoggingConfiguration.LogInfo(
                    "Offline vault seed skipped (shared fingerprint unchanged).",
                    nameof(OfflineVaultService));
                return;
            }

            await using var local = new OfflineVaultDbContext();

            var accounts = await shared.Accounts.AsNoTracking().Where(a => !a.IsDeleted).ToListAsync(ct);
            var folders = await shared.Folders.AsNoTracking().ToListAsync(ct);
            var codes = await shared.BackupCodes.AsNoTracking().Where(c => !c.IsDeleted).ToListAsync(ct);
            var vaultFolders = await shared.VaultFolders.AsNoTracking().ToListAsync(ct);
            var items = await shared.VaultItems.AsNoTracking()
                .Include(i => i.CustomFields)
                .Include(i => i.ExternalLinks)
                .Where(i => !i.IsDeleted)
                .ToListAsync(ct);
            var crypto = await shared.VaultCrypto.AsNoTracking().FirstOrDefaultAsync(ct);

            await local.Database.ExecuteSqlRawAsync("DELETE FROM ExternalLinks", ct);
            await local.Database.ExecuteSqlRawAsync("DELETE FROM VaultCustomFields", ct);
            await local.Database.ExecuteSqlRawAsync("DELETE FROM VaultItems", ct);
            await local.Database.ExecuteSqlRawAsync("DELETE FROM VaultFolders", ct);
            await local.Database.ExecuteSqlRawAsync("DELETE FROM BackupCodes", ct);
            await local.Database.ExecuteSqlRawAsync("DELETE FROM Accounts", ct);
            await local.Database.ExecuteSqlRawAsync("DELETE FROM Folders", ct);
            await local.Database.ExecuteSqlRawAsync("DELETE FROM VaultCrypto", ct);

            if (crypto is not null) local.VaultCrypto.Add(crypto);
            local.Folders.AddRange(folders);
            local.Accounts.AddRange(accounts);
            local.BackupCodes.AddRange(codes);
            local.VaultFolders.AddRange(vaultFolders);
            foreach (var item in items)
            {
                local.VaultItems.Add(item);
            }
            await local.SaveChangesAsync(ct);
            WriteSeedFingerprint(fingerprint);
            LoggingConfiguration.LogInfo("Offline vault seeded from shared SQL.", nameof(OfflineVaultService));
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Failed to seed offline vault.", nameof(OfflineVaultService));
        }
    }

    private static async Task<string> ComputeSharedFingerprintAsync(AuthenticatorDbContext shared, CancellationToken ct)
    {
        var accountCount = await shared.Accounts.AsNoTracking().CountAsync(a => !a.IsDeleted, ct);
        var folderCount = await shared.Folders.AsNoTracking().CountAsync(ct);
        var codeCount = await shared.BackupCodes.AsNoTracking().CountAsync(c => !c.IsDeleted, ct);
        var vaultFolderCount = await shared.VaultFolders.AsNoTracking().CountAsync(ct);
        var itemCount = await shared.VaultItems.AsNoTracking().CountAsync(i => !i.IsDeleted, ct);
        var latestItem = await shared.VaultItems.AsNoTracking()
            .Where(i => !i.IsDeleted)
            .Select(i => i.ModifiedAt ?? i.CreatedAt)
            .OrderByDescending(d => d)
            .FirstOrDefaultAsync(ct);
        var latestAccount = await shared.Accounts.AsNoTracking()
            .Where(a => !a.IsDeleted)
            .Select(a => a.ModifiedAt ?? a.CreatedAt)
            .OrderByDescending(d => d)
            .FirstOrDefaultAsync(ct);
        var cryptoFp = await shared.VaultCrypto.AsNoTracking()
            .Select(c => c.VdekFingerprint)
            .FirstOrDefaultAsync(ct) ?? "";

        var payload =
            $"{ConnectionConfig.Server}|{ConnectionConfig.Database}|{accountCount}|{folderCount}|{codeCount}|{vaultFolderCount}|{itemCount}|{latestItem:O}|{latestAccount:O}|{cryptoFp}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash);
    }

    private static string? ReadSeedFingerprint()
    {
        try
        {
            return File.Exists(SeedFingerprintPath) ? File.ReadAllText(SeedFingerprintPath).Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteSeedFingerprint(string fingerprint)
    {
        try
        {
            File.WriteAllText(SeedFingerprintPath, fingerprint);
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Failed to write offline seed fingerprint.", nameof(OfflineVaultService));
        }
    }

    /// <summary>Invalidate cached fingerprint so the next seed reloads (e.g. after reconcile).</summary>
    public static void InvalidateSeedFingerprint()
    {
        try
        {
            if (File.Exists(SeedFingerprintPath))
                File.Delete(SeedFingerprintPath);
        }
        catch
        {
            // ignore
        }
    }

    public async Task MirrorItemAsync(VaultItem item, CancellationToken ct = default)
    {
        try
        {
            await EnsureCreatedAsync(ct);
            await using var local = new OfflineVaultDbContext();
            var existing = await local.VaultItems.FirstOrDefaultAsync(i => i.Id == item.Id, ct);
            if (existing is null)
                local.VaultItems.Add(CloneItem(item));
            else
                local.Entry(existing).CurrentValues.SetValues(CloneItem(item));
            await local.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Mirror item to offline failed.", nameof(OfflineVaultService));
        }
    }

    public async Task<List<VaultItem>> GetItemsAsync(bool favoritesOnly, bool includeDeleted, CancellationToken ct)
    {
        await EnsureCreatedAsync(ct);
        await using var db = new OfflineVaultDbContext();
        var q = db.VaultItems.Include(i => i.CustomFields).Include(i => i.ExternalLinks).AsQueryable();
        if (!includeDeleted) q = q.Where(i => !i.IsDeleted);
        if (favoritesOnly) q = q.Where(i => i.IsFavorite);
        var items = await q.OrderBy(i => i.Title).ToListAsync(ct);
        foreach (var i in items)
            i.HasMismatch = i.ExternalLinks.Any(l => l.IsMismatch);
        return items;
    }

    public async Task<VaultItem?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        await EnsureCreatedAsync(ct);
        await using var db = new OfflineVaultDbContext();
        return await db.VaultItems.Include(i => i.CustomFields).Include(i => i.ExternalLinks)
            .FirstOrDefaultAsync(i => i.Id == id, ct);
    }

    public async Task<VaultItem> SaveAsync(VaultItem item, bool queueChange, CancellationToken ct)
    {
        await EnsureCreatedAsync(ct);
        await using var db = new OfflineVaultDbContext();
        var existing = await db.VaultItems.FirstOrDefaultAsync(i => i.Id == item.Id, ct);
        if (existing is null)
            db.VaultItems.Add(CloneItem(item));
        else
            db.Entry(existing).CurrentValues.SetValues(CloneItem(item));

        if (queueChange)
        {
            db.OfflineChangeQueue.Add(new OfflineChangeQueueEntry
            {
                EntityType = "VaultItem",
                EntityId = item.Id.ToString(),
                Operation = existing is null ? "Upsert" : "Upsert",
                PayloadHash = VaultItemService.ContentHash(item),
                PayloadJson = JsonSerializer.Serialize(new
                {
                    item.Id,
                    item.Title,
                    item.ItemType,
                    item.Username,
                    item.Password,
                    item.Url,
                    item.Notes,
                    item.PayloadJson,
                    item.IsFavorite,
                    item.FolderId,
                    item.LinkedAccountId,
                    item.TotpSecret
                }),
                CreatedAtUtc = DateTime.UtcNow
            });
        }

        await db.SaveChangesAsync(ct);
        return item;
    }

    public async Task DeleteAsync(Guid id, bool softDelete, CancellationToken ct)
    {
        await EnsureCreatedAsync(ct);
        await using var db = new OfflineVaultDbContext();
        var item = await db.VaultItems.FirstOrDefaultAsync(i => i.Id == id, ct);
        if (item is null) return;
        if (softDelete)
        {
            item.IsDeleted = true;
            item.ModifiedAt = DateTime.UtcNow;
        }
        else
            db.VaultItems.Remove(item);

        db.OfflineChangeQueue.Add(new OfflineChangeQueueEntry
        {
            EntityType = "VaultItem",
            EntityId = id.ToString(),
            Operation = softDelete ? "SoftDelete" : "Delete",
            CreatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<VaultFolder>> GetFoldersAsync(CancellationToken ct)
    {
        await EnsureCreatedAsync(ct);
        await using var db = new OfflineVaultDbContext();
        return await db.VaultFolders.OrderBy(f => f.Name).ToListAsync(ct);
    }

    public async Task<VaultFolder> SaveFolderAsync(VaultFolder folder, CancellationToken ct)
    {
        await EnsureCreatedAsync(ct);
        await using var db = new OfflineVaultDbContext();
        if (folder.Id == 0) db.VaultFolders.Add(folder);
        else db.VaultFolders.Update(folder);
        await db.SaveChangesAsync(ct);
        return folder;
    }

    public async Task<int> ReconcileToSharedAsync(CancellationToken ct = default)
    {
        if (ConnectionConfig.IsOfflineMode)
            return 0;

        await EnsureCreatedAsync(ct);
        await using var local = new OfflineVaultDbContext();
        var pending = await local.OfflineChangeQueue
            .Where(q => !q.Applied)
            .OrderBy(q => q.CreatedAtUtc)
            .ToListAsync(ct);

        var vault = new VaultItemService(_secrets, this);
        var applied = 0;
        foreach (var entry in pending)
        {
            try
            {
                if (entry.EntityType == "VaultItem" && Guid.TryParse(entry.EntityId, out var id))
                {
                    if (entry.Operation is "Delete" or "SoftDelete")
                    {
                        await vault.DeleteAsync(id, softDelete: entry.Operation == "SoftDelete", ct);
                    }
                    else if (!string.IsNullOrWhiteSpace(entry.PayloadJson))
                    {
                        var dto = JsonSerializer.Deserialize<VaultItemDto>(entry.PayloadJson);
                        if (dto is not null)
                        {
                            var existing = await vault.GetByIdAsync(id, ct);
                            if (existing is not null &&
                                existing.ModifiedAt is not null &&
                                entry.CreatedAtUtc < existing.ModifiedAt.Value &&
                                !string.Equals(entry.PayloadHash, VaultItemService.ContentHash(existing), StringComparison.OrdinalIgnoreCase))
                            {
                                // Conflict — mark mismatch on Shared link
                                await using var shared = new AuthenticatorDbContext();
                                var link = await shared.ExternalLinks.FirstOrDefaultAsync(
                                    l => l.ItemId == id && l.Provider == "Shared", ct);
                                if (link is null)
                                {
                                    shared.ExternalLinks.Add(new ExternalLink
                                    {
                                        ItemId = id,
                                        Provider = "Shared",
                                        ExternalId = id.ToString(),
                                        IsMismatch = true,
                                        LastSyncedAt = DateTime.UtcNow
                                    });
                                }
                                else
                                {
                                    link.IsMismatch = true;
                                }
                                await shared.SaveChangesAsync(ct);
                            }
                            else
                            {
                                var item = existing ?? new VaultItem { Id = id };
                                item.Title = dto.Title ?? item.Title;
                                item.ItemType = dto.ItemType;
                                item.Username = dto.Username;
                                item.Password = dto.Password;
                                item.Url = dto.Url;
                                item.Notes = dto.Notes;
                                item.PayloadJson = dto.PayloadJson;
                                item.IsFavorite = dto.IsFavorite;
                                item.FolderId = dto.FolderId;
                                item.LinkedAccountId = dto.LinkedAccountId;
                                item.TotpSecret = dto.TotpSecret;
                                await vault.SaveAsync(item, ct);
                            }
                        }
                    }
                }

                entry.Applied = true;
                applied++;
            }
            catch (Exception ex)
            {
                LoggingConfiguration.LogException(ex, $"Offline reconcile failed for queue {entry.Id}.", nameof(OfflineVaultService));
            }
        }

        await local.SaveChangesAsync(ct);
        if (applied > 0)
            InvalidateSeedFingerprint();
        return applied;
    }

    private static VaultItem CloneItem(VaultItem item) => new()
    {
        Id = item.Id,
        ItemType = item.ItemType,
        Title = item.Title,
        FolderId = item.FolderId,
        Notes = item.Notes,
        IsFavorite = item.IsFavorite,
        IsDeleted = item.IsDeleted,
        Username = item.Username,
        Password = item.Password,
        Url = item.Url,
        LinkedAccountId = item.LinkedAccountId,
        TotpSecret = item.TotpSecret,
        PayloadJson = item.PayloadJson,
        CreatedAt = item.CreatedAt,
        ModifiedAt = item.ModifiedAt,
        LastUsedAt = item.LastUsedAt
    };

    private sealed class VaultItemDto
    {
        public Guid Id { get; set; }
        public string? Title { get; set; }
        public VaultItemType ItemType { get; set; }
        public string? Username { get; set; }
        public string? Password { get; set; }
        public string? Url { get; set; }
        public string? Notes { get; set; }
        public string? PayloadJson { get; set; }
        public bool IsFavorite { get; set; }
        public int? FolderId { get; set; }
        public int? LinkedAccountId { get; set; }
        public string? TotpSecret { get; set; }
    }
}
