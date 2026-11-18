using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Authenticator.Data;
using Authenticator.Data.Models;
using Authenticator.Logging;
using Microsoft.EntityFrameworkCore;

namespace Authenticator.Services;

public sealed class VaultItemService
{
    private readonly ISecretProtector _secrets;
    private readonly OfflineVaultService _offline;

    public VaultItemService(ISecretProtector? secrets = null, OfflineVaultService? offline = null)
    {
        _secrets = secrets ?? new SecretProtector();
        _offline = offline ?? new OfflineVaultService();
    }

    /// <param name="revealSecrets">
    /// When false (list UI), strip secret fields from memory — filter/tab switches stay cheap.
    /// Call <see cref="GetByIdAsync"/> before copy/edit.
    /// </param>
    public async Task<List<VaultItem>> GetItemsAsync(
        bool favoritesOnly = false,
        bool includeDeleted = false,
        bool revealSecrets = true,
        CancellationToken ct = default)
    {
        List<VaultItem> items;
        if (ConnectionConfig.IsOfflineMode)
        {
            items = await _offline.GetItemsAsync(favoritesOnly, includeDeleted, ct);
        }
        else
        {
            await using var db = new AuthenticatorDbContext();
            IQueryable<VaultItem> q = db.VaultItems.AsNoTracking().Include(i => i.ExternalLinks);
            if (revealSecrets)
                q = q.Include(i => i.Folder).Include(i => i.CustomFields);
            if (!includeDeleted) q = q.Where(i => !i.IsDeleted);
            if (favoritesOnly) q = q.Where(i => i.IsFavorite);
            items = await q.OrderBy(i => i.Title).ToListAsync(ct);
        }

        foreach (var item in items)
        {
            if (revealSecrets)
                Reveal(item, includePassword: true);
            else
                ClearSecrets(item);
            item.HasMismatch = item.ExternalLinks.Any(l => l.IsMismatch);
        }
        return items;
    }

    public Task<VaultItem?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        GetByIdAsync(id, revealSecrets: true, revealPassword: true, ct);

    /// <param name="revealPassword">
    /// When false, decrypt notes/TOTP/etc. but leave <see cref="VaultItem.Password"/> encrypted
    /// until <see cref="GetPasswordAsync"/> or a full reveal.
    /// </param>
    public async Task<VaultItem?> GetByIdAsync(
        Guid id,
        bool revealSecrets,
        bool revealPassword,
        CancellationToken ct = default)
    {
        VaultItem? item;
        if (ConnectionConfig.IsOfflineMode)
        {
            item = await _offline.GetByIdAsync(id, ct);
        }
        else
        {
            await using var db = new AuthenticatorDbContext();
            item = await db.VaultItems
                .Include(i => i.Folder)
                .Include(i => i.CustomFields)
                .Include(i => i.ExternalLinks)
                .FirstOrDefaultAsync(i => i.Id == id, ct);
        }

        if (item is not null)
        {
            if (revealSecrets)
                Reveal(item, revealPassword);
            item.HasMismatch = item.ExternalLinks.Any(l => l.IsMismatch);
        }
        return item;
    }

    /// <summary>Decrypts and returns the password only — used by copy / editor Reveal.</summary>
    public async Task<string?> GetPasswordAsync(Guid id, CancellationToken ct = default)
    {
        var item = await GetByIdAsync(id, revealSecrets: true, revealPassword: true, ct);
        return item?.Password;
    }

    /// <summary>
    /// Builds an id→plaintext password map for search. Loaded on demand when the user types a query.
    /// </summary>
    public async Task<Dictionary<Guid, string>> GetPasswordIndexAsync(CancellationToken ct = default)
    {
        List<(Guid Id, string? Password)> rows;
        if (ConnectionConfig.IsOfflineMode)
        {
            var offlineItems = await _offline.GetItemsAsync(favoritesOnly: false, includeDeleted: false, ct);
            rows = offlineItems.Select(i => (i.Id, i.Password)).ToList();
        }
        else
        {
            await using var db = new AuthenticatorDbContext();
            rows = await db.VaultItems.AsNoTracking()
                .Where(i => !i.IsDeleted)
                .Select(i => new ValueTuple<Guid, string?>(i.Id, i.Password))
                .ToListAsync(ct);
        }

        var index = new Dictionary<Guid, string>(rows.Count);
        foreach (var (id, password) in rows)
        {
            if (string.IsNullOrEmpty(password))
                continue;
            try
            {
                index[id] = _secrets.IsEncrypted(password) ? _secrets.Unprotect(password) : password;
            }
            catch (Exception ex)
            {
                LoggingConfiguration.LogException(ex, $"Failed to decrypt password for search index ({id}).", nameof(VaultItemService));
            }
        }

        return index;
    }

    public async Task<VaultItem> SaveAsync(VaultItem item, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Id == Guid.Empty) item.Id = Guid.NewGuid();
        item.ModifiedAt = DateTime.UtcNow;

        if (ConnectionConfig.IsOfflineMode)
        {
            Seal(item);
            await _offline.SaveAsync(item, queueChange: true, ct);
            Reveal(item, includePassword: true);
            return item;
        }

        await using var db = new AuthenticatorDbContext();
        var existing = await db.VaultItems
            .Include(i => i.CustomFields)
            .FirstOrDefaultAsync(i => i.Id == item.Id, ct);

        Seal(item);
        if (existing is null)
        {
            if (item.CreatedAt == default) item.CreatedAt = DateTime.UtcNow;
            // Don't cascade-insert navigation collections that belong to other tables.
            item.ExternalLinks = new List<ExternalLink>();
            db.VaultItems.Add(item);
        }
        else
        {
            db.Entry(existing).CurrentValues.SetValues(item);
            existing.ModifiedAt = item.ModifiedAt;
            // Replace custom fields simply
            db.VaultCustomFields.RemoveRange(existing.CustomFields);
            foreach (var field in item.CustomFields)
            {
                field.ItemId = existing.Id;
                SealField(field);
                db.VaultCustomFields.Add(field);
            }
        }
        await db.SaveChangesAsync(ct);
        Reveal(item, includePassword: true);
        await _offline.MirrorItemAsync(item, ct);
        return item;
    }

    public async Task SetFavoriteAsync(Guid id, bool favorite, CancellationToken ct = default)
    {
        // Patch only — avoid GetById/Save decrypt+reseal of secrets just to flip a star.
        var now = DateTime.UtcNow;
        if (ConnectionConfig.IsOfflineMode)
        {
            await _offline.SetFavoriteAsync(id, favorite, now, ct);
            return;
        }

        await using var db = new AuthenticatorDbContext();
        var item = await db.VaultItems.FirstOrDefaultAsync(i => i.Id == id, ct)
            ?? throw new InvalidOperationException("Item not found.");
        item.IsFavorite = favorite;
        item.ModifiedAt = now;
        await db.SaveChangesAsync(ct);
        await _offline.SetFavoriteAsync(id, favorite, now, ct);
    }

    /// <summary>Records credential use for Recents without bumping <see cref="VaultItem.ModifiedAt"/>.</summary>
    public async Task TouchLastUsedAsync(Guid id, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        if (ConnectionConfig.IsOfflineMode)
        {
            await _offline.TouchLastUsedAsync(id, now, ct);
            return;
        }

        await using var db = new AuthenticatorDbContext();
        var item = await db.VaultItems.FirstOrDefaultAsync(i => i.Id == id, ct);
        if (item is null)
            return;

        item.LastUsedAt = now;
        await db.SaveChangesAsync(ct);
        await _offline.TouchLastUsedAsync(id, now, ct);
    }

    public async Task DeleteAsync(Guid id, bool softDelete = true, CancellationToken ct = default)
    {
        if (ConnectionConfig.IsOfflineMode)
        {
            await _offline.DeleteAsync(id, softDelete, ct);
            return;
        }

        await using var db = new AuthenticatorDbContext();
        var item = await db.VaultItems.FirstOrDefaultAsync(i => i.Id == id, ct);
        if (item is null) return;
        if (softDelete)
        {
            item.IsDeleted = true;
            item.ModifiedAt = DateTime.UtcNow;
        }
        else
            db.VaultItems.Remove(item);
        await db.SaveChangesAsync(ct);
        await _offline.DeleteAsync(id, softDelete, ct);
    }

    public sealed record VaultPurgeResult(int ItemsDeleted, int LinksDeleted, int RemainingItems, int RemainingLinks);

    /// <summary>
    /// Hard-delete all Shared vault items, custom fields, and external links (folders kept).
    /// Clears conflict links, offline mirror, offline change queue, and provider Diff cache.
    /// Verifies remaining row counts (SQL fallback if EF delete leaves rows).
    /// </summary>
    public async Task<VaultPurgeResult> PurgeAllVaultDataAsync(CancellationToken ct = default)
    {
        Providers.ProviderSessionService.Instance.ClearRemoteCache();

        LoggingConfiguration.LogInfo(
            $"Developer purge starting — profile={ConnectionConfig.ActiveProfileName}, " +
            $"server={ConnectionConfig.Server}, db={ConnectionConfig.Database}, offline={ConnectionConfig.IsOfflineMode}.",
            nameof(VaultItemService));

        var itemsDeleted = 0;
        var linksDeleted = 0;
        var remainingItems = 0;
        var remainingLinks = 0;

        if (!ConnectionConfig.IsOfflineMode)
        {
            await using var db = new AuthenticatorDbContext();
            linksDeleted = await db.ExternalLinks.CountAsync(ct);
            itemsDeleted = await db.VaultItems.CountAsync(ct);
            await db.ExternalLinks.ExecuteDeleteAsync(ct);
            await db.VaultCustomFields.ExecuteDeleteAsync(ct);
            await db.VaultItems.ExecuteDeleteAsync(ct);
            await db.OfflineChangeQueue.ExecuteDeleteAsync(ct);

            remainingLinks = await db.ExternalLinks.CountAsync(ct);
            remainingItems = await db.VaultItems.CountAsync(ct);
            if (remainingItems > 0 || remainingLinks > 0)
            {
                LoggingConfiguration.LogInfo(
                    $"EF purge left remaining items={remainingItems}, links={remainingLinks}; trying raw SQL.",
                    nameof(VaultItemService));
                await db.Database.ExecuteSqlRawAsync(
                    "DELETE FROM ExternalLinks; DELETE FROM VaultCustomFields; DELETE FROM VaultItems; DELETE FROM OfflineChangeQueue;",
                    ct);
                remainingLinks = await db.ExternalLinks.CountAsync(ct);
                remainingItems = await db.VaultItems.CountAsync(ct);
            }
        }

        try
        {
            await using var offline = new OfflineVaultDbContext();
            if (ConnectionConfig.IsOfflineMode)
            {
                linksDeleted = await offline.ExternalLinks.CountAsync(ct);
                itemsDeleted = await offline.VaultItems.CountAsync(ct);
            }

            await offline.ExternalLinks.ExecuteDeleteAsync(ct);
            await offline.VaultCustomFields.ExecuteDeleteAsync(ct);
            await offline.VaultItems.ExecuteDeleteAsync(ct);
            await offline.OfflineChangeQueue.ExecuteDeleteAsync(ct);

            if (ConnectionConfig.IsOfflineMode)
            {
                remainingLinks = await offline.ExternalLinks.CountAsync(ct);
                remainingItems = await offline.VaultItems.CountAsync(ct);
            }
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Offline vault purge failed.", nameof(VaultItemService));
        }

        Providers.ProviderSessionService.Instance.ClearRemoteCache();

        if (remainingItems > 0 || remainingLinks > 0)
        {
            LoggingConfiguration.LogInfo(
                $"Purge incomplete: remaining items={remainingItems}, links={remainingLinks}.",
                nameof(VaultItemService));
            throw new InvalidOperationException(
                $"Purge did not clear the vault (remaining items={remainingItems}, links={remainingLinks}). " +
                $"Check connection profile {ConnectionConfig.ActiveProfileName} → {ConnectionConfig.Server}/{ConnectionConfig.Database}.");
        }

        LoggingConfiguration.LogInfo(
            $"Purged vault data: deleted items={itemsDeleted}, links={linksDeleted}; remaining=0.",
            nameof(VaultItemService));
        return new VaultPurgeResult(itemsDeleted, linksDeleted, remainingItems, remainingLinks);
    }

    public async Task<List<VaultFolder>> GetFoldersAsync(CancellationToken ct = default)
    {
        if (ConnectionConfig.IsOfflineMode)
            return await _offline.GetFoldersAsync(ct);

        await using var db = new AuthenticatorDbContext();
        return await db.VaultFolders.OrderBy(f => f.Name).ToListAsync(ct);
    }

    public async Task<VaultFolder> SaveFolderAsync(VaultFolder folder, CancellationToken ct = default)
    {
        if (ConnectionConfig.IsOfflineMode)
            return await _offline.SaveFolderAsync(folder, ct);

        await using var db = new AuthenticatorDbContext();
        if (folder.Id == 0)
            db.VaultFolders.Add(folder);
        else
            db.VaultFolders.Update(folder);
        await db.SaveChangesAsync(ct);
        return folder;
    }

    public static string ContentHash(VaultItem item)
    {
        var payload = $"{item.Title}|{item.Username}|{item.Password}|{item.Url}|{item.Notes}|{item.PayloadJson}|{item.IsFavorite}|{item.ItemType}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private void Seal(VaultItem item)
    {
        if (!string.IsNullOrEmpty(item.Password) && !_secrets.IsEncrypted(item.Password))
            item.Password = _secrets.Protect(item.Password);
        if (!string.IsNullOrEmpty(item.TotpSecret) && !_secrets.IsEncrypted(item.TotpSecret))
            item.TotpSecret = _secrets.Protect(item.TotpSecret);
        if (!string.IsNullOrEmpty(item.Notes) && !_secrets.IsEncrypted(item.Notes))
            item.Notes = _secrets.Protect(item.Notes);
        if (!string.IsNullOrEmpty(item.PayloadJson) && !_secrets.IsEncrypted(item.PayloadJson))
            item.PayloadJson = _secrets.Protect(item.PayloadJson);
        foreach (var f in item.CustomFields)
            SealField(f);
    }

    private void SealField(VaultCustomField field)
    {
        if (field.IsSecret && !string.IsNullOrEmpty(field.Value) && !_secrets.IsEncrypted(field.Value))
            field.Value = _secrets.Protect(field.Value);
    }

    private void Reveal(VaultItem item, bool includePassword)
    {
        try
        {
            if (includePassword && !string.IsNullOrEmpty(item.Password) && _secrets.IsEncrypted(item.Password))
                item.Password = _secrets.Unprotect(item.Password);
            if (!string.IsNullOrEmpty(item.TotpSecret) && _secrets.IsEncrypted(item.TotpSecret))
                item.TotpSecret = _secrets.Unprotect(item.TotpSecret);
            if (!string.IsNullOrEmpty(item.Notes) && _secrets.IsEncrypted(item.Notes))
                item.Notes = _secrets.Unprotect(item.Notes);
            if (!string.IsNullOrEmpty(item.PayloadJson) && _secrets.IsEncrypted(item.PayloadJson))
                item.PayloadJson = _secrets.Unprotect(item.PayloadJson);
            foreach (var f in item.CustomFields)
            {
                if (f.IsSecret && !string.IsNullOrEmpty(f.Value) && _secrets.IsEncrypted(f.Value))
                    f.Value = _secrets.Unprotect(f.Value);
            }
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, $"Failed to reveal vault item {item.Id}.", nameof(VaultItemService));
        }
    }

    private static void ClearSecrets(VaultItem item)
    {
        item.Password = null;
        item.TotpSecret = null;
        item.Notes = null;
        item.PayloadJson = null;
        foreach (var f in item.CustomFields)
        {
            if (f.IsSecret)
                f.Value = null;
        }
    }
}
