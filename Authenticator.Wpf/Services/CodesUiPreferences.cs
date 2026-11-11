using System.IO;
using System.Text.Json;
using Authenticator.Logging;

namespace Authenticator.Services;

public enum CodesSortMode
{
    LatestFirst,
    EarliestFirst,
    ServiceAsc,
    ServiceDesc,
    AccountAsc,
    AccountDesc
}

public enum CodesViewMode
{
    Condensed,
    Large,
    Squares
}

public enum AppThemeMode
{
    System,
    Light,
    Dark
}

public sealed class CodesUiPreferencesState
{
    public CodesSortMode SortMode { get; set; } = CodesSortMode.ServiceAsc;
    public CodesViewMode ViewMode { get; set; } = CodesViewMode.Condensed;
    public AppThemeMode ThemeMode { get; set; } = AppThemeMode.System;

    /// <summary>Close/minimize hides to notification area instead of exiting.</summary>
    public bool MinimizeToTray { get; set; } = true;

    /// <summary>Register Ctrl+Shift+A to show/expand the window.</summary>
    public bool GlobalHotkeyEnabled { get; set; } = true;

    /// <summary>Keep the main window topmost while docked and expanded.</summary>
    public bool AlwaysOnTopWhenDocked { get; set; } = true;

    /// <summary>Dock edge handle fill color (hex, e.g. #E53935).</summary>
    public string DockHandleColor { get; set; } = "#E53935";

    /// <summary>Dock edge handle opacity (0.2–1.0).</summary>
    public double DockHandleOpacity { get; set; } = 0.92;
}

/// <summary>
/// Persists codes-list and chrome prefs under LocalAppData (no DB migration).
/// Favorites stay on Account rows; sort/view/theme live here.
/// Sort rule: favorites pin first, then the chosen sort within each group.
/// </summary>
public sealed class CodesUiPreferences
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string Path = CreatePath();
    private static CodesUiPreferencesState? _cached;

    private static string CreatePath()
    {
        var folder = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Authenticator");
        Directory.CreateDirectory(folder);
        return System.IO.Path.Combine(folder, "codes-ui-preferences.json");
    }

    public CodesUiPreferencesState Load()
    {
        if (_cached is not null)
            return _cached;

        try
        {
            if (File.Exists(Path))
            {
                var json = File.ReadAllText(Path);
                _cached = JsonSerializer.Deserialize<CodesUiPreferencesState>(json, JsonOptions)
                          ?? new CodesUiPreferencesState();
                return _cached;
            }
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Failed to load codes UI preferences.", nameof(CodesUiPreferences));
        }

        _cached = new CodesUiPreferencesState();
        return _cached;
    }

    public void Save(CodesUiPreferencesState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _cached = state;
        try
        {
            File.WriteAllText(Path, JsonSerializer.Serialize(state, JsonOptions));
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Failed to save codes UI preferences.", nameof(CodesUiPreferences));
        }
    }
}
