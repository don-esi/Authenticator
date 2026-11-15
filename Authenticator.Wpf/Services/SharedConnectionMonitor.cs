using Authenticator.Data;
using Authenticator.Logging;

namespace Authenticator.Services;

/// <summary>
/// Probes shared SQL reachability and toggles offline mode.
/// </summary>
public sealed class SharedConnectionMonitor
{
    public static SharedConnectionMonitor Instance { get; } = new();

    public async Task<bool> RefreshAsync(CancellationToken ct = default)
    {
        var profile = ConnectionConfig.ActiveProfile;
        var useShared = string.Equals(profile.Name, ConnectionConfig.SharedProfileName, StringComparison.OrdinalIgnoreCase)
                        || ConnectionConfig.Role is ConnectionRole.Host or ConnectionRole.Join;

        if (!useShared && string.Equals(profile.Name, ConnectionConfig.LocalProfileName, StringComparison.OrdinalIgnoreCase))
        {
            ConnectionConfig.SetOfflineMode(false);
            return true;
        }

        var ok = await ConnectionConfig.TestConnectionAsync(profile.Server, profile.Database, ct);
        ConnectionConfig.SetOfflineMode(!ok);
        if (!ok)
            LoggingConfiguration.LogInfo("Shared SQL unreachable; offline mode enabled.", nameof(SharedConnectionMonitor));
        return ok;
    }
}
