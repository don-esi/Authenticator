using System.Text.Json;
using Authenticator.Data;
using Authenticator.Data.Models;
using Authenticator.Logging;
using Microsoft.EntityFrameworkCore;

namespace Authenticator.Services;

/// <summary>CRUD + snapshot for global domain equivalence groups (Shared SQL + offline mirror).</summary>
public sealed class DomainEquivalenceService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static DomainEquivalenceSnapshot? _cached;
    private static DateTime _cachedAtUtc;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    public static void InvalidateCache()
    {
        _cached = null;
        _cachedAtUtc = default;
    }

    public static async Task<DomainEquivalenceSnapshot> LoadSnapshotAsync(CancellationToken ct = default)
    {
        if (_cached is not null && DateTime.UtcNow - _cachedAtUtc < CacheTtl)
            return _cached;

        try
        {
            List<DomainEquivalenceGroup> groups;
            if (ConnectionConfig.IsOfflineMode)
            {
                await new OfflineVaultService().EnsureCreatedAsync(ct).ConfigureAwait(false);
                await using var offline = new OfflineVaultDbContext();
                groups = await offline.DomainEquivalenceGroups.AsNoTracking()
                    .OrderBy(g => g.Name)
                    .ToListAsync(ct)
                    .ConfigureAwait(false);
            }
            else
            {
                await using var db = new AuthenticatorDbContext();
                groups = await db.DomainEquivalenceGroups.AsNoTracking()
                    .OrderBy(g => g.Name)
                    .ToListAsync(ct)
                    .ConfigureAwait(false);
            }

            var sets = new List<HashSet<string>>();
            foreach (var g in groups)
            {
                var hosts = ParseDomainsJson(g.DomainsJson);
                if (hosts.Count == 0)
                    continue;
                sets.Add(new HashSet<string>(hosts, StringComparer.OrdinalIgnoreCase));
            }

            _cached = new DomainEquivalenceSnapshot(sets);
            _cachedAtUtc = DateTime.UtcNow;
            return _cached;
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Failed to load domain equivalence groups.", nameof(DomainEquivalenceService));
            return DomainEquivalenceSnapshot.Empty;
        }
    }

    public async Task<List<DomainEquivalenceGroup>> ListAsync(CancellationToken ct = default)
    {
        if (ConnectionConfig.IsOfflineMode)
        {
            await new OfflineVaultService().EnsureCreatedAsync(ct).ConfigureAwait(false);
            await using var offline = new OfflineVaultDbContext();
            return await offline.DomainEquivalenceGroups.AsNoTracking()
                .OrderBy(g => g.Name)
                .ToListAsync(ct)
                .ConfigureAwait(false);
        }

        await using var db = new AuthenticatorDbContext();
        return await db.DomainEquivalenceGroups.AsNoTracking()
            .OrderBy(g => g.Name)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<DomainEquivalenceGroup> SaveAsync(DomainEquivalenceGroup group, CancellationToken ct = default)
    {
        group.Name = (group.Name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(group.Name))
            throw new InvalidOperationException("Name is required.");

        // UI may pass a textarea; stored form is always a JSON string array.
        var hosts = ParseDomainsJson(group.DomainsJson);
        if (hosts.Count == 0)
            throw new InvalidOperationException("Add at least one domain.");
        group.DomainsJson = SerializeDomains(hosts);
        group.ModifiedAt = DateTime.UtcNow;
        if (group.CreatedAt == default)
            group.CreatedAt = DateTime.UtcNow;

        if (ConnectionConfig.IsOfflineMode)
        {
            await new OfflineVaultService().EnsureCreatedAsync(ct).ConfigureAwait(false);
            await using var offline = new OfflineVaultDbContext();
            if (group.Id == 0)
                offline.DomainEquivalenceGroups.Add(group);
            else
                offline.DomainEquivalenceGroups.Update(group);
            await offline.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        else
        {
            await using var db = new AuthenticatorDbContext();
            if (group.Id == 0)
                db.DomainEquivalenceGroups.Add(group);
            else
                db.DomainEquivalenceGroups.Update(group);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            try
            {
                await new OfflineVaultService().EnsureCreatedAsync(ct).ConfigureAwait(false);
                await using var offline = new OfflineVaultDbContext();
                var local = await offline.DomainEquivalenceGroups.FirstOrDefaultAsync(g => g.Id == group.Id, ct)
                    .ConfigureAwait(false);
                if (local is null)
                {
                    offline.DomainEquivalenceGroups.Add(new DomainEquivalenceGroup
                    {
                        Id = group.Id,
                        Name = group.Name,
                        DomainsJson = group.DomainsJson,
                        CreatedAt = group.CreatedAt,
                        ModifiedAt = group.ModifiedAt
                    });
                }
                else
                {
                    local.Name = group.Name;
                    local.DomainsJson = group.DomainsJson;
                    local.ModifiedAt = group.ModifiedAt;
                }
                await offline.SaveChangesAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LoggingConfiguration.LogException(ex, "Mirror domain group to offline failed.", nameof(DomainEquivalenceService));
            }
        }

        InvalidateCache();
        OfflineVaultService.InvalidateSeedFingerprint();
        return group;
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        if (ConnectionConfig.IsOfflineMode)
        {
            await new OfflineVaultService().EnsureCreatedAsync(ct).ConfigureAwait(false);
            await using var offline = new OfflineVaultDbContext();
            var row = await offline.DomainEquivalenceGroups.FirstOrDefaultAsync(g => g.Id == id, ct)
                .ConfigureAwait(false);
            if (row is not null)
            {
                offline.DomainEquivalenceGroups.Remove(row);
                await offline.SaveChangesAsync(ct).ConfigureAwait(false);
            }
        }
        else
        {
            await using var db = new AuthenticatorDbContext();
            var row = await db.DomainEquivalenceGroups.FirstOrDefaultAsync(g => g.Id == id, ct)
                .ConfigureAwait(false);
            if (row is not null)
            {
                db.DomainEquivalenceGroups.Remove(row);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            try
            {
                await using var offline = new OfflineVaultDbContext();
                var local = await offline.DomainEquivalenceGroups.FirstOrDefaultAsync(g => g.Id == id, ct)
                    .ConfigureAwait(false);
                if (local is not null)
                {
                    offline.DomainEquivalenceGroups.Remove(local);
                    await offline.SaveChangesAsync(ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                LoggingConfiguration.LogException(ex, "Delete offline domain group failed.", nameof(DomainEquivalenceService));
            }
        }

        InvalidateCache();
        OfflineVaultService.InvalidateSeedFingerprint();
    }

    public static IReadOnlyList<string> ParseDomainsJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<string>();

        try
        {
            if (LooksLikeJsonArray(json))
            {
                var arr = JsonSerializer.Deserialize<string[]>(json, JsonOptions) ?? Array.Empty<string>();
                return VaultDomain.NormalizeDomainList(arr);
            }
        }
        catch
        {
            // fall through to free-text parse
        }

        return VaultDomain.ParseDomainList(json);
    }

    public static string SerializeDomains(IEnumerable<string> hosts) =>
        JsonSerializer.Serialize(VaultDomain.NormalizeDomainList(hosts), JsonOptions);

    public static string DomainsToEditText(string? domainsJson) =>
        string.Join(Environment.NewLine, ParseDomainsJson(domainsJson));

    private static bool LooksLikeJsonArray(string? text) =>
        !string.IsNullOrWhiteSpace(text) && text.TrimStart().StartsWith('[');
}

/// <summary>Immutable host-group lookup used by <see cref="VaultDomain"/> scoring.</summary>
public sealed class DomainEquivalenceSnapshot
{
    public static DomainEquivalenceSnapshot Empty { get; } = new(Array.Empty<HashSet<string>>());

    private readonly List<HashSet<string>> _groups;

    public DomainEquivalenceSnapshot(IEnumerable<HashSet<string>> groups)
    {
        _groups = groups.ToList();
    }

    public bool AreEquivalent(string? a, string? b)
    {
        var left = VaultDomain.NormalizeDomain(a);
        var right = VaultDomain.NormalizeDomain(b);
        if (left is null || right is null)
            return false;
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
            return true;

        foreach (var g in _groups)
        {
            if (g.Contains(left) && g.Contains(right))
                return true;
        }

        return false;
    }
}
