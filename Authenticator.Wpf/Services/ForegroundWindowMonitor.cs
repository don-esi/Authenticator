using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace Authenticator.Services;

public sealed class ForegroundWindowInfo
{
    public IntPtr Hwnd { get; init; }
    public string Title { get; init; } = "";
    public string ProcessName { get; init; } = "";
    public string? UrlHint { get; init; }
}

/// <summary>
/// Polls the foreground window to drive vault auto-filter / auto-fill.
/// </summary>
public sealed class ForegroundWindowMonitor : IDisposable
{
    public static ForegroundWindowMonitor Instance { get; } = new();

    private readonly DispatcherTimer _timer;
    private ForegroundWindowInfo? _last;
    private uint _cachedPid;
    private string _cachedProcessName = "";

    public event EventHandler<ForegroundWindowInfo>? ForegroundChanged;

    public ForegroundWindowInfo? Current => _last;

    private ForegroundWindowMonitor()
    {
        // Process.GetProcessById every tick is costly; keep this leisurely.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _timer.Tick += (_, _) => Poll();
    }

    public void Start() => _timer.Start();

    public void Stop() => _timer.Stop();

    public void Dispose() => Stop();

    private void Poll()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
            return;

        var title = GetWindowText(hwnd);
        _ = GetWindowThreadProcessId(hwnd, out var pid);
        var processName = ResolveProcessName(pid);

        var urlHint = ExtractUrlHint(title);
        var info = new ForegroundWindowInfo
        {
            Hwnd = hwnd,
            Title = title,
            ProcessName = processName,
            UrlHint = urlHint
        };

        if (_last is not null &&
            _last.Hwnd == info.Hwnd &&
            string.Equals(_last.Title, info.Title, StringComparison.Ordinal))
            return;

        _last = info;
        ForegroundChanged?.Invoke(this, info);
    }

    private string ResolveProcessName(uint pid)
    {
        if (pid == 0)
            return "";
        if (pid == _cachedPid)
            return _cachedProcessName;

        try
        {
            using var proc = System.Diagnostics.Process.GetProcessById((int)pid);
            _cachedPid = pid;
            _cachedProcessName = proc.ProcessName;
            return _cachedProcessName;
        }
        catch
        {
            _cachedPid = 0;
            _cachedProcessName = "";
            return "";
        }
    }

    private static string? ExtractUrlHint(string title)
    {
        // Browser titles often end with " - Domain" or contain https://
        if (title.Contains("https://", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("http://", StringComparison.OrdinalIgnoreCase))
        {
            var start = title.IndexOf("http", StringComparison.OrdinalIgnoreCase);
            var end = title.IndexOfAny([' ', '|', '-'], start);
            return end > start ? title[start..end] : title[start..];
        }

        // "Page - Example - Google Chrome"
        var parts = title.Split(['-', '|', '—'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? parts[^2] : null;
    }

    private static string GetWindowText(IntPtr hwnd)
    {
        var sb = new StringBuilder(512);
        _ = GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}
