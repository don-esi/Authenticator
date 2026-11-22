using Authenticator.Logging;

namespace Authenticator.Services.Providers;

public enum ProviderDialogMode
{
    /// <summary>One-shot import; credentials are not retained.</summary>
    Import,
    /// <summary>Persistent connection; credentials retained until Disconnect.</summary>
    Connect
}

/// <summary>
/// Live provider sessions + DPAPI-backed "Connect to" retention.
/// Import-from never writes the store; Connect-to does.
/// Connect is all-or-nothing for the current attempt: any exception tears down the live
/// session and does not leave a new saved connection (so restart stays disconnected).
/// </summary>
public sealed class ProviderSessionService
{
    public static ProviderSessionService Instance { get; } = new();

    private readonly ProviderConnectionStore _store = new();
    private readonly Dictionary<string, IVaultProvider> _live = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>providerId + normalized origin id → last-seen provider entry (for Diff UI).</summary>
    private readonly Dictionary<string, ProviderEntry> _remoteCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public event EventHandler? ConnectionsChanged;
    public event EventHandler<VaultSyncResult>? ConflictScanCompleted;

    public IReadOnlyList<ProviderConnectionRecord> SavedConnections => _store.LoadAll();

    public IReadOnlyList<IVaultProvider> LiveConnected
    {
        get
        {
            lock (_gate)
                return _live.Values.Where(p => p.IsConnected).ToList();
        }
    }

    public bool IsSaved(string providerId) => _store.Find(providerId) is not null;

    public bool TryGetLive(string providerId, out IVaultProvider? provider)
    {
        lock (_gate)
        {
            if (_live.TryGetValue(providerId, out var p) && p.IsConnected)
            {
                provider = p;
                return true;
            }
        }

        provider = null;
        return false;
    }

    public async Task<(IVaultProvider Provider, int? ImportedCount)> ConnectAndPersistAsync(
        IVaultProvider provider,
        ProviderConnectRequest request,
        bool importAfterConnect,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var clientId = string.IsNullOrWhiteSpace(request.ClientId)
            ? (_store.Find(provider.ProviderId)?.ClientId ?? Guid.NewGuid().ToString("N"))
            : request.ClientId;
        request.ClientId = clientId;

        // Preserve client id across attempts, but do not keep a saved row unless this attempt fully succeeds.
        var preservedClientId = clientId;

        try
        {
            await provider.ConnectAsync(request, ct);
            RememberLive(provider);

            int? imported = null;
            if (importAfterConnect)
            {
                // Off UI thread so the busy spinner can animate; each Save uses its own DbContext.
                imported = await Task.Run(
                    async () => await new VaultImportService().BulkImportAsync(provider, ct).ConfigureAwait(false),
                    ct).ConfigureAwait(true);
            }

            // Persist only after login (+ optional import) fully succeeds.
            _store.Upsert(new ProviderConnectionRecord
            {
                ProviderId = provider.ProviderId,
                DisplayName = provider.DisplayName,
                Username = request.Username,
                Password = request.Password,
                FilePath = request.FilePath,
                KeyFilePath = request.KeyFilePath,
                ClientId = preservedClientId,
                ConnectedAtUtc = DateTime.UtcNow
            });
            ConnectionsChanged?.Invoke(this, EventArgs.Empty);
            LoggingConfiguration.LogInfo(
                $"Connected and saved {provider.ProviderId} (importAfter={importAfterConnect}, imported={imported}).",
                nameof(ProviderSessionService));
            return (provider, imported);
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(
                ex,
                $"Connect aborted for {provider.ProviderId}; not connected.",
                nameof(ProviderSessionService));
            AbortFailedConnectAttempt(provider);
            throw;
        }
    }

    public async Task<(IVaultProvider Provider, int ImportedCount)> ImportOnceAsync(
        IVaultProvider provider,
        ProviderConnectRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        // Ephemeral client id — do not touch the connection store.
        request.ClientId ??= Guid.NewGuid().ToString("N");
        try
        {
            await provider.ConnectAsync(request, ct);
            var count = await Task.Run(
                async () => await new VaultImportService().BulkImportAsync(provider, ct).ConfigureAwait(false),
                ct).ConfigureAwait(true);
            return (provider, count);
        }
        finally
        {
            provider.Disconnect();
        }
    }

    /// <summary>
    /// Exception during Connect ⇒ not connected: drop live session and saved row for this provider.
    /// </summary>
    private void AbortFailedConnectAttempt(IVaultProvider provider)
    {
        // Disconnect clears live + DPAPI row so restart will not show connected.
        Disconnect(provider.ProviderId);
        if (provider.IsConnected)
            provider.Disconnect();
    }

    public void Disconnect(string providerId)
    {
        lock (_gate)
        {
            if (_live.Remove(providerId, out var live))
                live.Disconnect();
            ClearRemoteCache_NoLock(providerId);
        }

        _store.Remove(providerId);
        ConnectionsChanged?.Invoke(this, EventArgs.Empty);
        LoggingConfiguration.LogInfo($"Disconnected provider {providerId}.", nameof(ProviderSessionService));
    }

    /// <summary>Drops in-memory sessions on lock; saved Connect-to records remain until Disconnect.</summary>
    public void DropLiveSessions()
    {
        lock (_gate)
        {
            foreach (var provider in _live.Values)
                provider.Disconnect();
            _live.Clear();
            _remoteCache.Clear();
        }
    }

    /// <summary>Clears Diff/reconcile remote entry cache (e.g. after developer vault purge).</summary>
    public void ClearRemoteCache()
    {
        lock (_gate)
            _remoteCache.Clear();
    }

    public static string NormalizeExternalId(string? externalId)
    {
        if (string.IsNullOrWhiteSpace(externalId))
            return "";
        return externalId.Trim().Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();
    }

    public void CacheRemoteEntry(string providerId, ProviderEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var key = RemoteCacheKey(providerId, entry.ExternalId);
        if (string.IsNullOrEmpty(key))
            return;
        lock (_gate)
            _remoteCache[key] = CloneEntry(entry);
    }

    public void CacheRemoteEntries(string providerId, IEnumerable<ProviderEntry> entries)
    {
        foreach (var entry in entries)
            CacheRemoteEntry(providerId, entry);
    }

    public bool TryGetCachedRemote(string providerId, string externalId, out ProviderEntry? entry)
    {
        var key = RemoteCacheKey(providerId, externalId);
        lock (_gate)
        {
            if (!string.IsNullOrEmpty(key) && _remoteCache.TryGetValue(key, out var cached))
            {
                entry = CloneEntry(cached);
                return true;
            }
        }

        entry = null;
        return false;
    }

    /// <summary>
    /// Resolves a provider-side entry for Diff: live list (normalized id) → cache → title/username fallback.
    /// </summary>
    public async Task<ProviderEntry?> FindRemoteEntryAsync(
        string providerId,
        string externalId,
        string? title = null,
        string? username = null,
        CancellationToken ct = default)
    {
        if (TryGetCachedRemote(providerId, externalId, out var cached) && cached is not null)
        {
            // Still refresh from live when connected so Diff is current.
        }

        IReadOnlyList<ProviderEntry> entries = Array.Empty<ProviderEntry>();
        try
        {
            var provider = await EnsureConnectedAsync(providerId, ct);
            entries = await provider.ListEntriesAsync(ct);
            CacheRemoteEntries(providerId, entries);
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, $"FindRemoteEntry connect/list failed for {providerId}.", nameof(ProviderSessionService));
            return cached;
        }

        var norm = NormalizeExternalId(externalId);
        var hit = entries.FirstOrDefault(e =>
            string.Equals(NormalizeExternalId(e.ExternalId), norm, StringComparison.Ordinal));
        if (hit is not null)
            return hit;

        if (!string.IsNullOrWhiteSpace(title))
        {
            hit = entries.FirstOrDefault(e =>
                string.Equals(e.Title, title, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(username) ||
                 string.Equals(e.Username, username, StringComparison.OrdinalIgnoreCase)));
            if (hit is not null)
                return hit;
        }

        return cached;
    }

    private void ClearRemoteCache_NoLock(string providerId)
    {
        var prefix = providerId + "\u001f";
        foreach (var key in _remoteCache.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
            _remoteCache.Remove(key);
    }

    private static string RemoteCacheKey(string providerId, string? externalId)
    {
        var norm = NormalizeExternalId(externalId);
        return string.IsNullOrEmpty(norm) ? "" : $"{providerId}\u001f{norm}";
    }

    private static ProviderEntry CloneEntry(ProviderEntry e) =>
        new()
        {
            ExternalId = e.ExternalId,
            Title = e.Title,
            Username = e.Username,
            Password = e.Password,
            Url = e.Url,
            Notes = e.Notes,
            Totp = e.Totp,
            Path = e.Path,
            IsFavorite = e.IsFavorite,
            ItemType = e.ItemType,
            Fields = new Dictionary<string, string>(e.Fields),
            CreatedAt = e.CreatedAt,
            ModifiedAt = e.ModifiedAt
        };

    public async Task ReconnectAllAsync(CancellationToken ct = default)
    {
        var saved = _store.LoadAll().ToList();
        foreach (var record in saved)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await ReconnectOneAsync(record, ct);
            }
            catch (Exception ex)
            {
                LoggingConfiguration.LogException(
                    ex,
                    $"Failed to reconnect {record.ProviderId}; connection kept for retry.",
                    nameof(ProviderSessionService));
            }
        }

        ConnectionsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Fast sync: import missing entries only. Conflict scan is separate / background.</summary>
    public async Task<VaultSyncResult> SyncProviderImportAsync(string providerId, CancellationToken ct = default)
    {
        var live = await EnsureConnectedAsync(providerId, ct);
        var imported = await new VaultImportService().ImportMissingAsync(live, ct);
        return new VaultSyncResult { Imported = imported };
    }

    public async Task<VaultSyncResult> SyncAllImportAsync(CancellationToken ct = default)
    {
        var total = new VaultSyncResult();
        // Lazy connect: saved providers are not opened at unlock — bring them up on demand.
        foreach (var conn in SavedConnections.ToList())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var provider = await EnsureConnectedAsync(conn.ProviderId, ct);
                var imported = await new VaultImportService().ImportMissingAsync(provider, ct);
                total.Imported += imported;
            }
            catch (Exception ex)
            {
                LoggingConfiguration.LogException(
                    ex,
                    $"Sync import skipped for {conn.ProviderId}.",
                    nameof(ProviderSessionService));
            }
        }

        return total;
    }

    /// <summary>Background-friendly hash conflict scan for one connected provider.</summary>
    public async Task<VaultSyncResult> ScanConflictsAsync(string providerId, CancellationToken ct = default)
    {
        var live = await EnsureConnectedAsync(providerId, ct);
        var result = await new VaultImportService().ScanConflictsAsync(live, ct);
        ConflictScanCompleted?.Invoke(this, result);
        return result;
    }

    public async Task<VaultSyncResult> ScanAllConflictsAsync(CancellationToken ct = default)
    {
        var total = new VaultSyncResult();
        foreach (var conn in SavedConnections.ToList())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var provider = await EnsureConnectedAsync(conn.ProviderId, ct);
                var part = await new VaultImportService().ScanConflictsAsync(provider, ct);
                total.Unchanged += part.Unchanged;
                total.Conflicts += part.Conflicts;
            }
            catch (Exception ex)
            {
                LoggingConfiguration.LogException(
                    ex,
                    $"Conflict scan skipped for {conn.ProviderId}.",
                    nameof(ProviderSessionService));
            }
        }

        ConflictScanCompleted?.Invoke(this, total);
        return total;
    }

    /// <summary>Legacy full reconcile (import + scan). Prefer import + background scan for UI.</summary>
    public async Task<VaultSyncResult> SyncProviderAsync(string providerId, CancellationToken ct = default)
    {
        var import = await SyncProviderImportAsync(providerId, ct);
        var scan = await ScanConflictsAsync(providerId, ct);
        return new VaultSyncResult
        {
            Imported = import.Imported,
            Unchanged = scan.Unchanged,
            Conflicts = scan.Conflicts
        };
    }

    public async Task<VaultSyncResult> SyncAllConnectedAsync(CancellationToken ct = default)
    {
        var import = await SyncAllImportAsync(ct);
        var scan = await ScanAllConflictsAsync(ct);
        return new VaultSyncResult
        {
            Imported = import.Imported,
            Unchanged = scan.Unchanged,
            Conflicts = scan.Conflicts
        };
    }

    /// <summary>
    /// Ensure every saved provider is live, import missing entries, then hash-diff Shared vs provider (reconcile).
    /// </summary>
    public async Task<VaultSyncResult> ReconcileAllProvidersAsync(CancellationToken ct = default)
    {
        foreach (var conn in SavedConnections.ToList())
        {
            ct.ThrowIfCancellationRequested();
            await EnsureConnectedAsync(conn.ProviderId, ct);
        }

        return await SyncAllConnectedAsync(ct);
    }

    /// <summary>Ensure one saved provider is live, import missing, then conflict-scan (reconcile).</summary>
    public async Task<VaultSyncResult> ReconcileProviderAsync(string providerId, CancellationToken ct = default)
    {
        await EnsureConnectedAsync(providerId, ct);
        return await SyncProviderAsync(providerId, ct);
    }

    /// <summary>Reconnects a saved provider without importing/reconciling.</summary>
    public async Task<IVaultProvider> EnsureConnectedAsync(string providerId, CancellationToken ct = default)
    {
        if (TryGetLive(providerId, out var live) && live is not null)
            return live;

        var record = _store.Find(providerId)
                     ?? throw new InvalidOperationException($"{providerId} is not connected.");
        return await ReconnectOneAsync(record, ct);
    }

    private async Task<IVaultProvider> ReconnectOneAsync(ProviderConnectionRecord record, CancellationToken ct)
    {
        var provider = VaultProviderCatalog.CreateAll()
                           .FirstOrDefault(p => string.Equals(p.ProviderId, record.ProviderId, StringComparison.OrdinalIgnoreCase))
                       ?? throw new InvalidOperationException($"Unknown provider {record.ProviderId}.");

        await provider.ConnectAsync(new ProviderConnectRequest
        {
            Username = record.Username,
            Password = record.Password,
            FilePath = record.FilePath,
            KeyFilePath = record.KeyFilePath,
            ClientId = record.ClientId
        }, ct);

        RememberLive(provider);
        LoggingConfiguration.LogInfo($"Reconnected {provider.ProviderId}.", nameof(ProviderSessionService));
        return provider;
    }

    private void RememberLive(IVaultProvider provider)
    {
        lock (_gate)
        {
            if (_live.Remove(provider.ProviderId, out var previous) && !ReferenceEquals(previous, provider))
                previous.Disconnect();
            _live[provider.ProviderId] = provider;
        }
    }
}
