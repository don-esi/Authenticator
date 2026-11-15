using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Authenticator.Data;
using Authenticator.Logging;

namespace Authenticator.Services;

public sealed class SqlDiscoveryAdvertisement
{
    public string Product { get; set; } = "Authenticator";
    public int Version { get; set; } = 1;
    public string Server { get; set; } = "";
    public string Database { get; set; } = "Authenticator";
    public int Port { get; set; } = 1433;
    public string? VdekFingerprintPrefix { get; set; }
    public string? RemoteEndPoint { get; set; }
}

/// <summary>
/// UDP LAN discovery for shared SQL Express (AuthenticatorSql/1 on port 38455).
/// </summary>
public sealed class SqlDiscoveryService : IDisposable
{
    public const int DefaultUdpPort = 38455;
    public const string ProbeTag = "AuthenticatorSql/1";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private UdpClient? _listener;
    private CancellationTokenSource? _advertiseCts;
    private Task? _advertiseTask;
    private SqlDiscoveryAdvertisement? _payload;
    private bool _disposed;

    public bool IsAdvertising => _advertiseTask is not null && !_advertiseTask.IsCompleted;

    public void StartAdvertising(SqlDiscoveryAdvertisement advertisement, int? udpPort = null)
    {
        ArgumentNullException.ThrowIfNull(advertisement);
        StopAdvertising();

        _payload = advertisement;
        var port = udpPort ?? ConnectionConfig.DiscoveryUdpPort;
        _advertiseCts = new CancellationTokenSource();
        _listener = new UdpClient(port);
        _listener.EnableBroadcast = true;
        var ct = _advertiseCts.Token;
        _advertiseTask = Task.Run(() => AdvertiseLoopAsync(ct), ct);
        LoggingConfiguration.LogInfo($"SQL discovery advertising on UDP {port} as {advertisement.Server}.", nameof(SqlDiscoveryService));
    }

    public void StopAdvertising()
    {
        try { _advertiseCts?.Cancel(); } catch { /* ignore */ }
        try { _listener?.Close(); } catch { /* ignore */ }
        _listener = null;
        _advertiseCts = null;
        _advertiseTask = null;
        _payload = null;
    }

    private async Task AdvertiseLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is not null && _payload is not null)
        {
            try
            {
                var result = await _listener.ReceiveAsync(ct);
                var text = Encoding.UTF8.GetString(result.Buffer);
                if (!text.StartsWith(ProbeTag, StringComparison.OrdinalIgnoreCase))
                    continue;

                var json = JsonSerializer.Serialize(_payload, JsonOptions);
                var bytes = Encoding.UTF8.GetBytes(json);
                await _listener.SendAsync(bytes, bytes.Length, result.RemoteEndPoint);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LoggingConfiguration.LogException(ex, "Discovery advertise loop error.", nameof(SqlDiscoveryService));
                await Task.Delay(500, ct).ConfigureAwait(false);
            }
        }
    }

    public static async Task<IReadOnlyList<SqlDiscoveryAdvertisement>> ProbeAsync(
        TimeSpan? timeout = null,
        int? udpPort = null,
        CancellationToken cancellationToken = default)
    {
        var port = udpPort ?? ConnectionConfig.DiscoveryUdpPort;
        var wait = timeout ?? TimeSpan.FromSeconds(2.5);
        var found = new Dictionary<string, SqlDiscoveryAdvertisement>(StringComparer.OrdinalIgnoreCase);

        using var client = new UdpClient();
        client.EnableBroadcast = true;
        var probe = Encoding.UTF8.GetBytes(ProbeTag);
        var endpoint = new IPEndPoint(IPAddress.Broadcast, port);
        await client.SendAsync(probe, probe.Length, endpoint);

        var deadline = DateTime.UtcNow + wait;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
                break;

            var receiveTask = client.ReceiveAsync(cancellationToken).AsTask();
            var delayTask = Task.Delay(remaining, cancellationToken);
            var completed = await Task.WhenAny(receiveTask, delayTask);
            if (completed != receiveTask)
                break;

            try
            {
                var result = await receiveTask;
                var text = Encoding.UTF8.GetString(result.Buffer);
                if (text.StartsWith(ProbeTag, StringComparison.OrdinalIgnoreCase))
                    continue;

                var ad = JsonSerializer.Deserialize<SqlDiscoveryAdvertisement>(text, JsonOptions);
                if (ad is null || string.IsNullOrWhiteSpace(ad.Server))
                    continue;
                if (!string.Equals(ad.Product, "Authenticator", StringComparison.OrdinalIgnoreCase))
                    continue;

                ad.RemoteEndPoint = result.RemoteEndPoint.ToString();
                found[ad.Server] = ad;
            }
            catch (Exception ex)
            {
                LoggingConfiguration.LogException(ex, "Failed to parse discovery reply.", nameof(SqlDiscoveryService));
            }
        }

        LoggingConfiguration.LogInfo($"SQL discovery found {found.Count} host(s).", nameof(SqlDiscoveryService));
        return found.Values.ToList();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        StopAdvertising();
    }
}
