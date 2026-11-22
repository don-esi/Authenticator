using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Authenticator.NativeHost;

/// <summary>
/// Chromium/Firefox native-messaging host: stdio length-prefixed JSON ↔ TCP 127.0.0.1:38456 (WPF).
/// stdout must contain ONLY framed messages — any other console output breaks Chrome.
/// </summary>
internal static class Program
{
    private const string BridgeHost = "127.0.0.1";
    private const int BridgePort = 38456;
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "Authenticator.NativeHost.log");

    private static int Main()
    {
        // Capture stdio before silencing Console — Chrome owns these pipes.
        using var stdin = Console.OpenStandardInput();
        using var stdout = Console.OpenStandardOutput();

        // Prevent runtime / library writes from corrupting the native-messaging stream.
        try
        {
            var log = new StreamWriter(LogPath, append: true, Utf8) { AutoFlush = true };
            Console.SetOut(log);
            Console.SetError(log);
        }
        catch
        {
            Console.SetOut(TextWriter.Null);
            Console.SetError(TextWriter.Null);
        }

        try
        {
            Log($"start pid={Environment.ProcessId}");
            while (true)
            {
                var request = ReadMessage(stdin);
                if (request is null)
                {
                    Log("stdin closed");
                    return 0;
                }

                Log($"req bytes={Utf8.GetByteCount(request)}");
                var response = ForwardToVault(request);
                WriteMessage(stdout, response);
                Log($"res bytes={Utf8.GetByteCount(response)}");
            }
        }
        catch (Exception ex)
        {
            Log(ex.ToString());
            try
            {
                // Best-effort framed error so Chrome shows something useful instead of "invalid JSON".
                WriteMessage(stdout, Fail("error", ex.Message));
            }
            catch { /* ignore */ }
            return 1;
        }
    }

    private static void Log(string line)
    {
        try { File.AppendAllText(LogPath, $"{DateTime.UtcNow:o} {line}\n", Utf8); }
        catch { /* ignore */ }
    }

    private static string ForwardToVault(string requestJson)
    {
        try
        {
            using var client = new TcpClient();
            var connectTask = client.ConnectAsync(BridgeHost, BridgePort);
            if (!connectTask.Wait(3000))
            {
                return Fail(
                    "unavailable",
                    $"Timed out connecting to {BridgeHost}:{BridgePort}. Unlock Authenticator first.");
            }

            connectTask.GetAwaiter().GetResult();

            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Utf8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
            using var writer = new StreamWriter(stream, Utf8, bufferSize: 4096, leaveOpen: true) { AutoFlush = true };

            writer.WriteLine(requestJson);
            stream.ReadTimeout = 5000;
            var line = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(line))
                return Fail("empty", "Desktop vault returned an empty response.");

            // WPF StreamWriter(UTF8) historically emitted a BOM — strip before Chrome sees it.
            line = line.TrimStart('\uFEFF').Trim();
            if (line.Length >= 3 && line[0] == (char)0xEF)
                line = line.TrimStart((char)0xEF, (char)0xBB, (char)0xBF);

            // Ensure we only ever forward pure JSON to Chrome.
            using var _ = JsonDocument.Parse(line);
            return line;
        }
        catch (SocketException ex)
        {
            return Fail(
                "unavailable",
                $"Cannot reach Authenticator on {BridgeHost}:{BridgePort} ({ex.SocketErrorCode}). Unlock the app, then retry.");
        }
        catch (JsonException ex)
        {
            return Fail("error", "Desktop returned non-JSON: " + ex.Message);
        }
        catch (Exception ex)
        {
            return Fail("error", ex.Message);
        }
    }

    private static string Fail(string reason, string message) =>
        JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["ok"] = false,
            ["reason"] = reason,
            ["message"] = message
        });

    private static string? ReadMessage(Stream stdin)
    {
        var lenBuf = new byte[4];
        if (!ReadExact(stdin, lenBuf))
            return null;

        var len = BitConverter.ToInt32(lenBuf, 0);
        if (len <= 0 || len > 1024 * 1024)
            throw new InvalidOperationException($"Invalid native message length {len}.");

        var payload = new byte[len];
        if (!ReadExact(stdin, payload))
            return null;

        return Utf8.GetString(payload);
    }

    private static void WriteMessage(Stream stdout, string json)
    {
        // Validate before framing — Chrome reports "invalid JSON message" for bad payloads.
        using (JsonDocument.Parse(json)) { }

        var payload = Utf8.GetBytes(json);
        var len = BitConverter.GetBytes(payload.Length);
        stdout.Write(len, 0, 4);
        stdout.Write(payload, 0, payload.Length);
        stdout.Flush();
    }

    private static bool ReadExact(Stream stream, byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = stream.Read(buffer, offset, buffer.Length - offset);
            if (read == 0)
                return false;
            offset += read;
        }

        return true;
    }
}
