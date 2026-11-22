namespace Authenticator.Services;

/// <summary>
/// Shared host normalization and domain-match scoring for vault Relevant / autofill.
/// </summary>
public static class VaultDomain
{
    private static readonly HashSet<string> MultiPartTlds = new(StringComparer.OrdinalIgnoreCase)
    {
        "co.uk", "org.uk", "ac.uk", "gov.uk",
        "co.jp", "ne.jp", "or.jp",
        "com.au", "net.au", "org.au",
        "co.nz", "org.nz",
        "com.br", "com.mx", "com.ar",
        "co.in", "com.tw", "com.hk", "com.sg"
    };

    /// <summary>
    /// Normalize a URL or host to lowercase host (strip www.) + non-default port when present.
    /// Accepts scheme-less input (e.g. github.com/login).
    /// </summary>
    public static string? NormalizeDomain(string? url)
    {
        var raw = (url ?? "").Trim();
        if (string.IsNullOrEmpty(raw))
            return null;

        // Bare host or host/path without scheme.
        if (!raw.Contains("://", StringComparison.Ordinal))
            raw = "https://" + raw;

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
            return null;

        var host = uri.IdnHost;
        if (string.IsNullOrWhiteSpace(host))
            host = uri.Host;
        if (string.IsNullOrWhiteSpace(host))
            return null;

        host = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal))
            host = host[4..];
        if (string.IsNullOrEmpty(host))
            return null;

        if (!uri.IsDefaultPort && uri.Port > 0)
            return $"{host}:{uri.Port}";

        return host;
    }

    /// <summary>
    /// Score how well <paramref name="vaultHost"/> matches a captured browser host.
    /// 100 exact, 80 registrable domain, 60 registrable label, 0 none.
    /// </summary>
    public static int ScoreMatch(string? capturedHost, string? vaultHost)
    {
        var captured = NormalizeDomain(capturedHost);
        var vault = NormalizeDomain(vaultHost);
        if (captured is null || vault is null)
            return 0;

        if (string.Equals(captured, vault, StringComparison.OrdinalIgnoreCase))
            return 100;

        var capReg = RegistrableDomain(StripPort(captured));
        var vaultReg = RegistrableDomain(StripPort(vault));
        if (capReg is not null &&
            vaultReg is not null &&
            string.Equals(capReg, vaultReg, StringComparison.OrdinalIgnoreCase))
            return 80;

        var capLabel = RegistrableLabel(capReg);
        var vaultLabel = RegistrableLabel(vaultReg);
        if (capLabel is not null &&
            vaultLabel is not null &&
            string.Equals(capLabel, vaultLabel, StringComparison.OrdinalIgnoreCase) &&
            capLabel.Length >= 3)
            return 60;

        return 0;
    }

    public static string? RegistrableDomain(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return null;

        host = StripPort(host).Trim().Trim('.').ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal))
            host = host[4..];

        var labels = host.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (labels.Length == 0)
            return null;
        if (labels.Length == 1)
            return labels[0];

        var lastTwo = $"{labels[^2]}.{labels[^1]}";
        if (labels.Length >= 3 && MultiPartTlds.Contains(lastTwo))
            return $"{labels[^3]}.{lastTwo}";

        return lastTwo;
    }

    private static string? RegistrableLabel(string? registrableDomain)
    {
        if (string.IsNullOrWhiteSpace(registrableDomain))
            return null;
        var first = registrableDomain.Split('.', 2)[0];
        return string.IsNullOrWhiteSpace(first) ? null : first;
    }

    private static string StripPort(string host)
    {
        var idx = host.LastIndexOf(':');
        if (idx <= 0)
            return host;
        // IPv6 in brackets not used for vault hosts today.
        if (host.Contains(']', StringComparison.Ordinal))
            return host;
        return host[..idx];
    }
}
