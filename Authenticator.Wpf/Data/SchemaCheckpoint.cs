using System.IO;
using System.Text.Json;
using Authenticator.Logging;

namespace Authenticator.Data;

/// <summary>
/// Local marker so we skip expensive EnsureCreated/ALTER/schema probes on every launch.
/// Bump <see cref="CurrentVersion"/> when schema ensure scripts change.
/// </summary>
public static class SchemaCheckpoint
{
    /// <summary>Bump when DatabaseInitializer / vault table ensures change.</summary>
    public const int CurrentVersion = 4;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string Path = CreatePath();

    private static string CreatePath()
    {
        var folder = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Authenticator");
        Directory.CreateDirectory(folder);
        return System.IO.Path.Combine(folder, "schema-checkpoint.json");
    }

    public static bool IsCurrent()
    {
        try
        {
            if (!File.Exists(Path))
                return false;
            var state = JsonSerializer.Deserialize<State>(File.ReadAllText(Path), JsonOptions);
            return state?.Version == CurrentVersion
                   && string.Equals(state.Server, ConnectionConfig.Server, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(state.Database, ConnectionConfig.Database, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Failed to read schema checkpoint.", nameof(SchemaCheckpoint));
            return false;
        }
    }

    public static void MarkCurrent()
    {
        try
        {
            var state = new State
            {
                Version = CurrentVersion,
                Server = ConnectionConfig.Server,
                Database = ConnectionConfig.Database,
                UpdatedAtUtc = DateTime.UtcNow
            };
            File.WriteAllText(Path, JsonSerializer.Serialize(state, JsonOptions));
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Failed to write schema checkpoint.", nameof(SchemaCheckpoint));
        }
    }

    private sealed class State
    {
        public int Version { get; set; }
        public string Server { get; set; } = "";
        public string Database { get; set; } = "";
        public DateTime UpdatedAtUtc { get; set; }
    }
}
