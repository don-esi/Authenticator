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
/// </summary>
public sealed class ProviderSessionService
{
    public static ProviderSessionService Instance { get; } = new();

    private readonly ProviderConnectionStore _store = new();
    private readonly Dictionary<string, IVaultProvider> _live = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public event EventHandler? ConnectionsChanged;

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

        await provider.ConnectAsync(request, ct);
        RememberLive(provider);

        _store.Upsert(new ProviderConnectionRecord
        {
            ProviderId = provider.ProviderId,
            DisplayName = provider.DisplayName,
            Username = request.Username,
            Password = request.Password,
            FilePath = request.FilePath,
            KeyFilePath = request.KeyFilePath,
            ClientId = clientId,
            ConnectedAtUtc = DateTime.UtcNow
        });
        ConnectionsChanged?.Invoke(this, EventArgs.Empty);

        int? imported = null;
        if (importAfterConnect)
            imported = await new VaultImportService().BulkImportAsync(provider, ct);

        return (provider, imported);
    }

    public async Task<(IVaultProvider Provider, int ImportedCount)> ImportOnceAsync(
        IVaultProvider provider,
        ProviderConnectRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        // Ephemeral client id — do not touch the connection store.
        request.ClientId ??= Guid.NewGuid().ToString("N");
        await provider.ConnectAsync(request, ct);
        var count = await new VaultImportService().BulkImportAsync(provider, ct);
        provider.Disconnect();
        return (provider, count);
    }

    public void Disconnect(string providerId)
    {
        lock (_gate)
        {
            if (_live.Remove(providerId, out var live))
                live.Disconnect();
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
        }
    }

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

    public async Task<VaultSyncResult> SyncAllConnectedAsync(CancellationToken ct = default)
    {
        var total = new VaultSyncResult();
        foreach (var provider in LiveConnected.ToList())
        {
            ct.ThrowIfCancellationRequested();
            var part = await new VaultImportService().ReconcileAsync(provider, ct);
            total.Imported += part.Imported;
            total.Unchanged += part.Unchanged;
            total.Conflicts += part.Conflicts;
        }

        return total;
    }

    public async Task<VaultSyncResult> SyncProviderAsync(string providerId, CancellationToken ct = default)
    {
        var live = await EnsureConnectedAsync(providerId, ct);
        return await new VaultImportService().ReconcileAsync(live, ct);
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
