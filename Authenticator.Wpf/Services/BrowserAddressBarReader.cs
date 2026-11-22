using System.Windows.Automation;
using Authenticator.Logging;

namespace Authenticator.Services;

/// <summary>
/// Reads the active tab URL from Chromium/Edge address bars via UI Automation.
/// </summary>
public static class BrowserAddressBarReader
{
    private static readonly string[] OmniboxNames =
    [
        "Address and search bar",
        "Address bar",
        "Search or enter address",
        "Search with Google or enter address",
        "Search with Bing or enter address"
    ];

    public static (string? Url, string? Host) TryRead(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return (null, null);

        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            if (root is null)
                return (null, null);

            var edit = FindOmnibox(root);
            if (edit is null)
                return (null, null);

            if (edit.TryGetCurrentPattern(ValuePattern.Pattern, out var patternObj) &&
                patternObj is ValuePattern value)
            {
                var raw = value.Current.Value?.Trim();
                if (string.IsNullOrWhiteSpace(raw))
                    return (null, null);

                // Omnibox may show the host without scheme while browsing.
                var host = VaultDomain.NormalizeDomain(raw);
                if (host is null)
                    return (null, null);

                var url = raw.Contains("://", StringComparison.Ordinal)
                    ? raw
                    : "https://" + raw.TrimStart('/');
                return (url, host);
            }
        }
        catch (ElementNotAvailableException)
        {
            // Window closed mid-read.
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Failed to read browser address bar.", nameof(BrowserAddressBarReader));
        }

        return (null, null);
    }

    private static AutomationElement? FindOmnibox(AutomationElement root)
    {
        foreach (var name in OmniboxNames)
        {
            try
            {
                var byName = root.FindFirst(
                    TreeScope.Descendants,
                    new AndCondition(
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                        new PropertyCondition(AutomationElement.NameProperty, name)));
                if (byName is not null)
                    return byName;
            }
            catch (ElementNotAvailableException)
            {
                return null;
            }
        }

        // Fallback: first visible Edit with a ValuePattern that looks like a URL/host.
        try
        {
            var edits = root.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
            if (edits is null)
                return null;

            foreach (AutomationElement edit in edits)
            {
                try
                {
                    if (!edit.TryGetCurrentPattern(ValuePattern.Pattern, out var patternObj) ||
                        patternObj is not ValuePattern value)
                        continue;

                    var raw = value.Current.Value?.Trim() ?? "";
                    if (raw.Length < 3)
                        continue;
                    if (VaultDomain.NormalizeDomain(raw) is not null)
                        return edit;
                    if (raw.Contains('.', StringComparison.Ordinal) && !raw.Contains(' '))
                        return edit;
                }
                catch (ElementNotAvailableException)
                {
                    // Skip stale node.
                }
            }
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }

        return null;
    }
}
