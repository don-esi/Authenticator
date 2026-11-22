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
    /// 100 exact (or global equivalence), 80 registrable domain, 60 registrable label, 0 none.
    /// </summary>
    public static int ScoreMatch(
        string? capturedHost,
        string? vaultHost,
        DomainEquivalenceSnapshot? equivalence = null)
    {
        var captured = NormalizeDomain(capturedHost);
        var vault = NormalizeDomain(vaultHost);
        if (captured is null || vault is null)
            return 0;

        if (string.Equals(captured, vault, StringComparison.OrdinalIgnoreCase))
            return 100;

        if (equivalence is not null && equivalence.AreEquivalent(captured, vault))
            return 100;

        var capReg = RegistrableDomain(StripPort(captured));
        var vaultReg = RegistrableDomain(StripPort(vault));
        if (capReg is not null &&
            vaultReg is not null &&
            string.Equals(capReg, vaultReg, StringComparison.OrdinalIgnoreCase))
            return 80;

        if (equivalence is not null &&
            capReg is not null &&
            vaultReg is not null &&
            equivalence.AreEquivalent(capReg, vaultReg))
            return 100;

        var capLabel = RegistrableLabel(capReg);
        var vaultLabel = RegistrableLabel(vaultReg);
        if (capLabel is not null &&
            vaultLabel is not null &&
            string.Equals(capLabel, vaultLabel, StringComparison.OrdinalIgnoreCase) &&
            capLabel.Length >= 3)
            return 60;

        return 0;
    }

    /// <summary>
    /// Best score across primary URL, per-item equivalent domains, and global groups.
    /// </summary>
    public static int ScoreItemMatch(
        string? capturedHost,
        string? primaryUrl,
        string? equivalentDomainsText,
        DomainEquivalenceSnapshot? equivalence = null)
    {
        var best = ScoreMatch(capturedHost, primaryUrl, equivalence);
        foreach (var alt in ParseDomainList(equivalentDomainsText))
        {
            var score = ScoreMatch(capturedHost, alt, equivalence);
            if (score > best)
                best = score;
        }

        return best;
    }

    public static IReadOnlyList<string> ParseDomainList(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<string>();

        var parts = text.Split(new[] { '\r', '\n', ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return NormalizeDomainList(parts);
    }

    public static IReadOnlyList<string> NormalizeDomainList(IEnumerable<string?> raw)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (var item in raw)
        {
            var n = NormalizeDomain(item);
            if (n is null || !set.Add(n))
                continue;
            list.Add(n);
        }
        return list;
    }

    public static string FormatDomainList(IEnumerable<string?> domains) =>
        string.Join(Environment.NewLine, NormalizeDomainList(domains));

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
