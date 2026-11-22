using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Authenticator.Crypto;
using Authenticator.Data.Models;
using Authenticator.Logging;

namespace Authenticator.Services;

/// <summary>
/// Local bridge for the browser native-messaging host while the vault is unlocked.
/// Listens on 127.0.0.1 only (TCP). Named pipes were unreachable from Chrome-launched hosts.
/// </summary>
public sealed class VaultNativePipeServer : IDisposable
{
    /// <summary>Loopback port shared with Authenticator.NativeHost.</summary>
    public const int TcpPort = 38456;

    public const string PipeName = "AuthenticatorVault"; // legacy name; bridge is TCP now
    public static VaultNativePipeServer Instance { get; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private CancellationTokenSource? _cts;
    private TcpListener? _listener;
    private Task? _listenTask;
    private int _generation;

    /// <summary>Raised when the extension reports the active tab host (for Relevant filter).</summary>
    public event EventHandler<string?>? TabHostChanged;

    public string? LastExtensionHost { get; private set; }

    public bool IsRunning => _cts is not null && !_cts.IsCancellationRequested && _listener is not null;

    public void Start()
    {
        Stop();
        _cts = new CancellationTokenSource();
        var gen = Interlocked.Increment(ref _generation);
        var ct = _cts.Token;

        try
        {
            _listener = new TcpListener(IPAddress.Loopback, TcpPort);
            _listener.Start();
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, $"Failed to bind 127.0.0.1:{TcpPort}.", nameof(VaultNativePipeServer));
            _cts.Dispose();
            _cts = null;
            _listener = null;
            return;
        }

        _listenTask = Task.Run(() => ListenLoopAsync(gen, ct));
        LoggingConfiguration.LogInfo(
            $"Vault native bridge listening on 127.0.0.1:{TcpPort}.",
            nameof(VaultNativePipeServer));
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        try { _listener?.Stop(); } catch { /* ignore */ }
        _listener = null;
        _cts?.Dispose();
        _cts = null;
        _listenTask = null;
        LastExtensionHost = null;
        Interlocked.Increment(ref _generation);
    }

    public void Dispose() => Stop();

    private async Task ListenLoopAsync(int generation, CancellationToken ct)
    {
        var listener = _listener;
        if (listener is null)
            return;

        while (!ct.IsCancellationRequested && generation == _generation)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                if (generation != _generation)
                {
                    client.Dispose();
                    break;
                }

                _ = HandleClientAsync(client, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                LoggingConfiguration.LogException(ex, "Native bridge accept failed.", nameof(VaultNativePipeServer));
                try { await Task.Delay(300, ct).ConfigureAwait(false); } catch { /* ignore */ }
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Utf8NoBom, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
                await using var writer = new StreamWriter(stream, Utf8NoBom, bufferSize: 4096, leaveOpen: true) { AutoFlush = true };

                while (!ct.IsCancellationRequested && client.Connected)
                {
                    var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                    if (line is null)
                        break;
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    var response = await DispatchAsync(line, ct).ConfigureAwait(false);
                    await writer.WriteLineAsync(response).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Shutting down.
            }
            catch (IOException)
            {
                // Client disconnected.
            }
            catch (Exception ex)
            {
                LoggingConfiguration.LogException(ex, "Native bridge client handler failed.", nameof(VaultNativePipeServer));
            }
        }
    }

    private async Task<string> DispatchAsync(string line, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

            return type switch
            {
                "ping" => JsonSerializer.Serialize(new
                {
                    ok = true,
                    type = "pong",
                    unlocked = VaultKeyService.Instance.IsUnlocked,
                    bridge = $"127.0.0.1:{TcpPort}"
                }, JsonOptions),
                "tabChanged" => HandleTabChanged(root),
                "getCredentials" => await HandleGetCredentialsAsync(root, ct).ConfigureAwait(false),
                _ => JsonSerializer.Serialize(new { ok = false, reason = "unknown_type" }, JsonOptions)
            };
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Native bridge dispatch failed.", nameof(VaultNativePipeServer));
            return JsonSerializer.Serialize(new { ok = false, reason = "error", message = ex.Message }, JsonOptions);
        }
    }

    private string HandleTabChanged(JsonElement root)
    {
        var host = root.TryGetProperty("host", out var h) ? h.GetString() : null;
        host = VaultDomain.NormalizeDomain(host) ?? host?.Trim();
        LastExtensionHost = string.IsNullOrWhiteSpace(host) ? null : host;
        TabHostChanged?.Invoke(this, LastExtensionHost);
        return JsonSerializer.Serialize(new { ok = true }, JsonOptions);
    }

    private async Task<string> HandleGetCredentialsAsync(JsonElement root, CancellationToken ct)
    {
        if (!VaultKeyService.Instance.IsUnlocked)
            return JsonSerializer.Serialize(new { ok = false, reason = "locked" }, JsonOptions);

        var origin = root.TryGetProperty("origin", out var o) ? o.GetString() : null;
        var url = root.TryGetProperty("url", out var u) ? u.GetString() : null;
        var captured = VaultDomain.NormalizeDomain(origin)
                       ?? VaultDomain.NormalizeDomain(url);
        if (string.IsNullOrWhiteSpace(captured))
            return JsonSerializer.Serialize(new { ok = false, reason = "no_match" }, JsonOptions);

        LastExtensionHost = captured;
        TabHostChanged?.Invoke(this, captured);

        Guid? requestedId = null;
        if (root.TryGetProperty("itemId", out var idEl))
        {
            var idText = idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : idEl.ToString();
            if (!string.IsNullOrWhiteSpace(idText) && Guid.TryParse(idText, out var parsed))
                requestedId = parsed;
        }

        var vault = new VaultItemService();
        var equivalence = await DomainEquivalenceService.LoadSnapshotAsync(ct).ConfigureAwait(false);
        var all = await vault.GetItemsAsync(revealSecrets: false, ct: ct).ConfigureAwait(false);
        var scored = new List<(VaultItem Item, int Score)>();
        foreach (var item in all)
        {
            if (item.ItemType != VaultItemType.Login || !item.AllowAutoFill)
                continue;
            var score = VaultDomain.ScoreItemMatch(captured, item.Url, item.EquivalentDomains, equivalence);
            if (score <= 0)
                continue;
            scored.Add((item, score));
        }

        var exact = scored.Where(s => s.Score == 100).ToList();
        if (exact.Count == 0)
            return JsonSerializer.Serialize(new { ok = false, reason = "no_match" }, JsonOptions);

        if (requestedId is Guid pickId)
        {
            var picked = exact.FirstOrDefault(s => s.Item.Id == pickId);
            if (picked.Item is null)
                return JsonSerializer.Serialize(new { ok = false, reason = "no_match" }, JsonOptions);
            return await RevealCredentialAsync(vault, picked.Item, picked.Score, ct).ConfigureAwait(false);
        }

        if (exact.Count > 1)
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                reason = "ambiguous",
                candidates = exact.Select(s => new
                {
                    id = s.Item.Id.ToString("D"),
                    title = s.Item.Title ?? "",
                    username = s.Item.Username ?? "",
                    score = s.Score
                }).ToArray()
            }, JsonOptions);
        }

        return await RevealCredentialAsync(vault, exact[0].Item, exact[0].Score, ct).ConfigureAwait(false);
    }

    private static async Task<string> RevealCredentialAsync(
        VaultItemService vault, VaultItem chosen, int score, CancellationToken ct)
    {
        var full = await vault.GetByIdAsync(chosen.Id, revealSecrets: true, revealPassword: true, ct).ConfigureAwait(false);
        if (full is null)
            return JsonSerializer.Serialize(new { ok = false, reason = "no_match" }, JsonOptions);

        try { await vault.TouchLastUsedAsync(full.Id, ct).ConfigureAwait(false); }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, $"Touch last-used failed for {full.Id}.", nameof(VaultNativePipeServer));
        }

        return JsonSerializer.Serialize(new
        {
            ok = true,
            items = new[]
            {
                new
                {
                    id = full.Id.ToString("D"),
                    username = full.Username ?? "",
                    password = full.Password ?? "",
                    allowAutoLogin = full.AllowAutoLogin,
                    score
                }
            }
        }, JsonOptions);
    }
}
