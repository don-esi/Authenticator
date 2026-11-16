using System.IO;
using System.Text.Json;
using Authenticator.Logging;

namespace Authenticator.Services;

public enum VaultSortMode
{
    TitleAsc,
    TitleDesc,
    UsernameAsc,
    UsernameDesc
}

public enum VaultGroupMode
{
    None,
    EntryType,
    UrlDomain,
    Username
}

public sealed class VaultUiPreferencesState
{
    public VaultSortMode SortMode { get; set; } = VaultSortMode.TitleAsc;
    public VaultGroupMode GroupMode { get; set; } = VaultGroupMode.None;
}

public sealed class VaultUiPreferences
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string Path = CreatePath();
    private static VaultUiPreferencesState? _cached;

    private static string CreatePath()
    {
        var folder = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Authenticator");
        Directory.CreateDirectory(folder);
        return System.IO.Path.Combine(folder, "vault-ui-preferences.json");
    }

    public VaultUiPreferencesState Load()
    {
        if (_cached is not null)
            return _cached;

        try
        {
            if (File.Exists(Path))
            {
                var json = File.ReadAllText(Path);
                _cached = JsonSerializer.Deserialize<VaultUiPreferencesState>(json, JsonOptions)
                          ?? new VaultUiPreferencesState();
                return _cached;
            }
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Failed to load vault UI preferences.", nameof(VaultUiPreferences));
        }

        _cached = new VaultUiPreferencesState();
        return _cached;
    }

    public void Save(VaultUiPreferencesState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _cached = state;
        try
        {
            File.WriteAllText(Path, JsonSerializer.Serialize(state, JsonOptions));
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Failed to save vault UI preferences.", nameof(VaultUiPreferences));
        }
    }
}
