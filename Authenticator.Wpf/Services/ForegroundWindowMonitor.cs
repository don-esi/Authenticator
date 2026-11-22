using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Threading;

namespace Authenticator.Services;

public sealed class ForegroundWindowInfo
{
    public IntPtr Hwnd { get; init; }
    public string Title { get; init; } = "";
    public string ProcessName { get; init; } = "";
    /// <summary>Short label for the Relevant filter hint UI.</summary>
    public string? UrlHint { get; init; }
    /// <summary>Tokens used for partial Relevant matching (hosts, app names, title words).</summary>
    public IReadOnlyList<string> MatchTerms { get; init; } = Array.Empty<string>();
    /// <summary>Raw URL from the browser address bar (UIA), when available.</summary>
    public string? CapturedUrl { get; init; }
    /// <summary>Normalized host from <see cref="CapturedUrl"/>; required for autofill.</summary>
    public string? CapturedHost { get; init; }
}

/// <summary>
/// Polls the foreground window to drive vault auto-filter / auto-fill.
/// </summary>
public sealed class ForegroundWindowMonitor : IDisposable
{
    public static ForegroundWindowMonitor Instance { get; } = new();

    private static readonly HashSet<string> BrowserProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "msedgewebview2", "firefox", "brave", "opera", "opera_gx",
        "vivaldi", "safari", "iexplore", "chromium", "waterfox", "librewolf"
    };

    private static readonly HashSet<string> BrowserTitleSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Google Chrome", "Microsoft Edge", "Firefox", "Mozilla Firefox", "Brave", "Opera",
        "Opera GX", "Vivaldi", "Safari", "Internet Explorer", "Chromium", "Arc"
    };

    private static readonly HashSet<string> GenericTitleSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "sign in", "sign-in", "signin", "log in", "log-in", "login", "log on", "logon",
        "new tab", "new window", "home", "welcome", "untitled", "loading",
        "create account", "register", "authentication", "verify", "verification",
        "account", "accounts", "my account", "your account"
    };

    private static readonly HashSet<string> NoiseTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "sign", "login", "log", "in", "on", "the", "and", "for", "your", "my", "our",
        "new", "tab", "window", "home", "page", "http", "https", "www", "com", "org",
        "net", "io", "co", "uk", "us", "app", "apps", "web", "site", "portal",
        "chrome", "edge", "firefox", "brave", "opera", "safari", "mozilla", "microsoft",
        "account", "accounts", "auth", "sso", "oauth", "untitled", "loading",
        // Generic product words — "Snipping Tool" must not match "Budget Tool"
        "tool", "tools", "utility", "utilities", "helper", "helpers", "manager", "managers",
        "viewer", "editor", "client", "server", "service", "services", "desktop", "system",
        "settings", "options", "properties", "dialog", "form", "free", "pro", "plus",
        "online", "official", "password", "passwords", "secure", "security"
    };

    /// <summary>Tokens too generic to count as a relevance hit by themselves.</summary>
    public static bool IsWeakToken(string token) =>
        string.IsNullOrWhiteSpace(token) ||
        token.Length < 4 ||
        NoiseTokens.Contains(token);

    private static readonly HashSet<string> SelfProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authenticator", "Authenticator.Wpf"
    };

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

        // Don't drive vault filter from our own window.
        if (SelfProcesses.Contains(processName))
            return;

        string? capturedUrl = null;
        string? capturedHost = null;
        if (BrowserProcesses.Contains(processName))
            (capturedUrl, capturedHost) = BrowserAddressBarReader.TryRead(hwnd);

        var terms = BuildMatchTerms(title, processName);
        if (!string.IsNullOrWhiteSpace(capturedHost))
        {
            // Prefer structured host terms when UIA succeeded.
            var withHost = new List<string> { capturedHost! };
            foreach (var t in terms)
            {
                if (!withHost.Contains(t, StringComparer.OrdinalIgnoreCase))
                    withHost.Add(t);
            }
            terms = withHost;
        }

        var info = new ForegroundWindowInfo
        {
            Hwnd = hwnd,
            Title = title,
            ProcessName = processName,
            CapturedUrl = capturedUrl,
            CapturedHost = capturedHost,
            UrlHint = !string.IsNullOrWhiteSpace(capturedHost)
                ? capturedHost
                : PickDisplayHint(terms, title, processName),
            MatchTerms = terms
        };

        if (_last is not null &&
            _last.Hwnd == info.Hwnd &&
            string.Equals(_last.Title, info.Title, StringComparison.Ordinal) &&
            string.Equals(_last.CapturedHost, info.CapturedHost, StringComparison.OrdinalIgnoreCase) &&
            SameTerms(_last.MatchTerms, info.MatchTerms))
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

    internal static List<string> BuildMatchTerms(string title, string processName)
    {
        var terms = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;
            var trimmed = value.Trim();
            if (trimmed.Length < 2 || !seen.Add(trimmed))
                return;
            terms.Add(trimmed);

            if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
                AddHostParts(uri.Host);
            else if (LooksLikeHost(trimmed))
                AddHostParts(trimmed);
        }

        void AddHostParts(string host)
        {
            host = host.Trim().Trim('.').ToLowerInvariant();
            if (host.StartsWith("www.", StringComparison.Ordinal))
                host = host[4..];
            if (string.IsNullOrWhiteSpace(host))
                return;

            if (seen.Add(host))
                terms.Add(host);

            foreach (var label in host.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (label.Length < 3 || NoiseTokens.Contains(label))
                    continue;
                if (seen.Add(label))
                    terms.Add(label);
            }
        }

        foreach (Match m in Regex.Matches(title, @"https?://[^\s|]+", RegexOptions.IgnoreCase))
            Add(m.Value.TrimEnd('.', ',', ';', ')', ']'));

        foreach (Match m in Regex.Matches(title, @"\b(?:www\.)?[a-z0-9-]+(?:\.[a-z0-9-]+)+\b", RegexOptions.IgnoreCase))
            Add(m.Value);

        var stripped = StripBrowserSuffix(title);
        foreach (var segment in stripped.Split(['-', '|', '—', '–', '•'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (GenericTitleSegments.Contains(segment) || BrowserTitleSuffixes.Contains(segment))
                continue;
            Add(segment);

            // Also keep email domains: user@company.com → company.com
            var at = segment.IndexOf('@');
            if (at > 0 && at < segment.Length - 1)
                Add(segment[(at + 1)..]);
        }

        foreach (var token in Tokenize(stripped))
            Add(token);

        if (!BrowserProcesses.Contains(processName) && !string.IsNullOrWhiteSpace(processName))
        {
            Add(processName);
            // Notion.exe-style names often match vault titles better without separators.
            Add(Regex.Replace(processName, @"[\._]", " "));
        }

        return terms;
    }

    private static string? PickDisplayHint(IReadOnlyList<string> terms, string title, string processName)
    {
        // Prefer a host-looking term, then a non-generic title segment, then process name.
        foreach (var term in terms)
        {
            if (LooksLikeHost(term))
                return term;
        }

        var stripped = StripBrowserSuffix(title);
        var segments = stripped.Split(['-', '|', '—', '–', '•'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        for (var i = segments.Length - 1; i >= 0; i--)
        {
            if (!GenericTitleSegments.Contains(segments[i]) && !BrowserTitleSuffixes.Contains(segments[i]))
                return segments[i];
        }

        if (!BrowserProcesses.Contains(processName) && !string.IsNullOrWhiteSpace(processName))
            return processName;

        return terms.FirstOrDefault(t => t.Length >= 3 && !NoiseTokens.Contains(t));
    }

    private static string StripBrowserSuffix(string title)
    {
        var result = title.Trim();
        foreach (var suffix in BrowserTitleSuffixes)
        {
            foreach (var sep in new[] { " - ", " – ", " — ", " | " })
            {
                var marker = sep + suffix;
                if (result.EndsWith(marker, StringComparison.OrdinalIgnoreCase))
                    result = result[..^marker.Length].Trim();
            }
        }
        return result;
    }

    private static bool LooksLikeHost(string value) =>
        value.Contains('.') &&
        !value.Contains(' ') &&
        Regex.IsMatch(value, @"^[a-z0-9.-]+\.[a-z]{2,}$", RegexOptions.IgnoreCase);

    internal static IEnumerable<string> Tokenize(string text)
    {
        foreach (Match m in Regex.Matches(text, @"[a-zA-Z0-9][a-zA-Z0-9+@._-]{1,}"))
        {
            var token = m.Value.Trim('.', '_', '-');
            if (IsWeakToken(token))
                continue;
            yield return token;
        }
    }

    /// <summary>Strong match keys only (hosts + distinctive words). Used by Relevant filtering.</summary>
    public static List<string> StrongMatchTerms(IEnumerable<string> terms)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var term in terms)
        {
            if (string.IsNullOrWhiteSpace(term))
                continue;

            if (LooksLikeHost(term) && seen.Add(term))
                result.Add(term);

            foreach (var token in Tokenize(term))
            {
                if (seen.Add(token))
                    result.Add(token);
            }

            // Keep multi-word phrases ("YNAB - Budget Tool" segments already split upstream).
            var compact = Regex.Replace(term, @"\s+", " ").Trim();
            if (compact.Length >= 4 && !IsWeakToken(compact) && !compact.Contains(' ') && seen.Add(compact))
                result.Add(compact);
        }

        return result;
    }

    private static bool SameTerms(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count != b.Count)
            return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
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
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}
