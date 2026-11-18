using System.IO;
using System.Text.Json;
using Authenticator.Logging;

namespace Authenticator.Services;

public sealed class DebugPreferencesState
{
    /// <summary>When true, Settings shows developer purge/reset actions.</summary>
    public bool DeveloperMode { get; set; }
}

/// <summary>LocalAppData debug toggles (not synced to Shared SQL).</summary>
public sealed class DebugPreferences
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string Path = CreatePath();
    private static DebugPreferencesState? _cached;

    private static string CreatePath()
    {
        var folder = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Authenticator");
        Directory.CreateDirectory(folder);
        return System.IO.Path.Combine(folder, "debug-preferences.json");
    }

    public DebugPreferencesState Load()
    {
        if (_cached is not null)
            return _cached;

        try
        {
            if (File.Exists(Path))
            {
                _cached = JsonSerializer.Deserialize<DebugPreferencesState>(File.ReadAllText(Path), JsonOptions)
                          ?? new DebugPreferencesState();
                return _cached;
            }
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Failed to load debug preferences.", nameof(DebugPreferences));
        }

        _cached = new DebugPreferencesState();
        return _cached;
    }

    public void Save(DebugPreferencesState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _cached = state;
        try
        {
            File.WriteAllText(Path, JsonSerializer.Serialize(state, JsonOptions));
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Failed to save debug preferences.", nameof(DebugPreferences));
        }
    }
}
