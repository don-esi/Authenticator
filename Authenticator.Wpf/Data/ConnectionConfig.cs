using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Authenticator.Logging;

namespace Authenticator.Data;

public enum ConnectionRole
{
    Local,
    Host,
    Join
}

public sealed class ConnectionProfile
{
    public string Name { get; set; } = "";
    public string Server { get; set; } = ConnectionConfig.DefaultServer;
    public string Database { get; set; } = ConnectionConfig.DefaultDatabase;
    public bool Discovered { get; set; }
}

public sealed class ConnectionDiscoverySettings
{
    public int UdpPort { get; set; } = SqlDiscoveryDefaults.UdpPort;
    public string? LastFingerprintPrefix { get; set; }
}

/// <summary>
/// Resolves SQL connection from %LocalAppData%\Authenticator\connection.json.
/// Supports legacy {Server,Database} and profile-based Host/Join shapes.
/// </summary>
public static class ConnectionConfig
{
    public const string DefaultServer = ".\\SQLEXPRESS";
    public const string DefaultDatabase = "Authenticator";
    public const string LocalProfileName = "Local";
    public const string SharedProfileName = "Shared";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static ConnectionFile? _file;
    private static string? _cachedConnectionString;

    public static string AppDataFolder
    {
        get
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Authenticator");
            Directory.CreateDirectory(folder);
            return folder;
        }
    }

    public static string ConfigPath => Path.Combine(AppDataFolder, "connection.json");

    public static string OfflineStorePath => Path.Combine(AppDataFolder, "offline-vault.db");

    public static string Server => ActiveProfile.Server;

    public static string Database => ActiveProfile.Database;

    public static string ConnectionString =>
        _cachedConnectionString ??= BuildConnectionString(Server, Database);

    public static ConnectionRole Role => LoadFile().Role;

    public static string ActiveProfileName => LoadFile().ActiveProfile ?? SharedProfileName;

    public static ConnectionProfile ActiveProfile
    {
        get
        {
            var file = LoadFile();
            var name = file.ActiveProfile ?? SharedProfileName;
            var profile = file.Profiles?.FirstOrDefault(p =>
                string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (profile is not null)
                return profile;

            return new ConnectionProfile
            {
                Name = LocalProfileName,
                Server = string.IsNullOrWhiteSpace(file.Server) ? DefaultServer : file.Server!,
                Database = string.IsNullOrWhiteSpace(file.Database) ? DefaultDatabase : file.Database!
            };
        }
    }

    public static IReadOnlyList<ConnectionProfile> Profiles =>
        LoadFile().Profiles ?? new List<ConnectionProfile>();

    public static int DiscoveryUdpPort
    {
        get
        {
            var port = LoadFile().Discovery?.UdpPort ?? 0;
            return port > 0 ? port : SqlDiscoveryDefaults.UdpPort;
        }
    }

    public static string? LastFingerprintPrefix => LoadFile().Discovery?.LastFingerprintPrefix;

    public static bool IsOfflineMode { get; private set; }

    public static event EventHandler? ConnectionChanged;

    public static string BuildConnectionString(string server, string database) =>
        $"Server={server};Database={database};Trusted_Connection=True;TrustServerCertificate=True;Connect Timeout=5";

    public static void InvalidateCache()
    {
        _file = null;
        _cachedConnectionString = null;
    }

    public static void SetOfflineMode(bool offline)
    {
        if (IsOfflineMode == offline)
            return;
        IsOfflineMode = offline;
        ConnectionChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Legacy simple write used by installer and tests.</summary>
    public static void Write(string server, string database) =>
        Save(new ConnectionFile
        {
            ActiveProfile = LocalProfileName,
            Role = ConnectionRole.Local,
            Profiles = new List<ConnectionProfile>
            {
                new() { Name = LocalProfileName, Server = server.Trim(), Database = database.Trim() },
                new() { Name = SharedProfileName, Server = server.Trim(), Database = database.Trim() }
            },
            Server = server.Trim(),
            Database = database.Trim(),
            Discovery = new ConnectionDiscoverySettings()
        });

    public static void Save(ConnectionFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(file, JsonOptions));
        InvalidateCache();
        ConnectionChanged?.Invoke(null, EventArgs.Empty);
    }

    public static ConnectionFile LoadFile()
    {
        if (_file is not null)
            return _file;

        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var file = JsonSerializer.Deserialize<ConnectionFile>(json, JsonOptions) ?? new ConnectionFile();
                Normalize(file);
                _file = file;
                return file;
            }
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Failed to read connection.json; using defaults.", nameof(ConnectionConfig));
        }

        _file = CreateDefaultFile();
        return _file;
    }

    public static void SetActiveProfile(string profileName)
    {
        var file = LoadFile();
        file.ActiveProfile = profileName;
        // Keep legacy fields in sync for older readers.
        var profile = file.Profiles?.FirstOrDefault(p =>
            string.Equals(p.Name, profileName, StringComparison.OrdinalIgnoreCase));
        if (profile is not null)
        {
            file.Server = profile.Server;
            file.Database = profile.Database;
        }
        Save(file);
    }

    public static void UpsertProfile(ConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var file = LoadFile();
        file.Profiles ??= new List<ConnectionProfile>();
        var existing = file.Profiles.FirstOrDefault(p =>
            string.Equals(p.Name, profile.Name, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
            file.Profiles.Add(profile);
        else
        {
            existing.Server = profile.Server;
            existing.Database = profile.Database;
            existing.Discovered = profile.Discovered;
        }
        if (string.Equals(file.ActiveProfile, profile.Name, StringComparison.OrdinalIgnoreCase))
        {
            file.Server = profile.Server;
            file.Database = profile.Database;
        }
        Save(file);
    }

    public static void SetRole(ConnectionRole role)
    {
        var file = LoadFile();
        file.Role = role;
        Save(file);
    }

    public static void SetFingerprintPrefix(string? prefix)
    {
        var file = LoadFile();
        file.Discovery ??= new ConnectionDiscoverySettings();
        file.Discovery.LastFingerprintPrefix = prefix;
        Save(file);
    }

    public static async Task<bool> TestConnectionAsync(string? server = null, string? database = null, CancellationToken ct = default)
    {
        var s = server ?? Server;
        var d = database ?? Database;
        try
        {
            await using var conn = new Microsoft.Data.SqlClient.SqlConnection(BuildConnectionString(s, d));
            await conn.OpenAsync(ct);
            return true;
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, $"Connection test failed for {s}/{d}.", nameof(ConnectionConfig));
            return false;
        }
    }

    private static void Normalize(ConnectionFile file)
    {
        file.Discovery ??= new ConnectionDiscoverySettings();
        if (file.Discovery.UdpPort <= 0)
            file.Discovery.UdpPort = SqlDiscoveryDefaults.UdpPort;

        if (file.Profiles is null || file.Profiles.Count == 0)
        {
            var server = string.IsNullOrWhiteSpace(file.Server) ? DefaultServer : file.Server.Trim();
            var database = string.IsNullOrWhiteSpace(file.Database) ? DefaultDatabase : file.Database.Trim();
            file.Profiles = new List<ConnectionProfile>
            {
                new() { Name = LocalProfileName, Server = DefaultServer, Database = DefaultDatabase },
                new() { Name = SharedProfileName, Server = server, Database = database }
            };
            file.ActiveProfile ??= string.Equals(server, DefaultServer, StringComparison.OrdinalIgnoreCase)
                ? LocalProfileName
                : SharedProfileName;
        }

        file.ActiveProfile ??= LocalProfileName;
        file.Server ??= ActiveOrDefault(file).Server;
        file.Database ??= ActiveOrDefault(file).Database;
    }

    private static ConnectionProfile ActiveOrDefault(ConnectionFile file)
    {
        return file.Profiles?.FirstOrDefault(p =>
                   string.Equals(p.Name, file.ActiveProfile, StringComparison.OrdinalIgnoreCase))
               ?? new ConnectionProfile { Name = LocalProfileName, Server = DefaultServer, Database = DefaultDatabase };
    }

    private static ConnectionFile CreateDefaultFile() => new()
    {
        ActiveProfile = LocalProfileName,
        Role = ConnectionRole.Local,
        Server = DefaultServer,
        Database = DefaultDatabase,
        Profiles = new List<ConnectionProfile>
        {
            new() { Name = LocalProfileName, Server = DefaultServer, Database = DefaultDatabase },
            new() { Name = SharedProfileName, Server = DefaultServer, Database = DefaultDatabase }
        },
        Discovery = new ConnectionDiscoverySettings()
    };

    public sealed class ConnectionFile
    {
        public string? ActiveProfile { get; set; }
        public ConnectionRole Role { get; set; } = ConnectionRole.Local;
        public List<ConnectionProfile>? Profiles { get; set; }
        public ConnectionDiscoverySettings? Discovery { get; set; }

        // Legacy flat fields (still written for compatibility).
        public string? Server { get; set; }
        public string? Database { get; set; }
    }
}

public static class SqlDiscoveryDefaults
{
    public const int UdpPort = 38455;
}
