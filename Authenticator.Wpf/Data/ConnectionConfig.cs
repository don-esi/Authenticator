using System.IO;
using System.Text.Json;
using Authenticator.Logging;

namespace Authenticator.Data;

/// <summary>
/// Resolves the SQL Server connection from %LocalAppData%\Authenticator\connection.json,
/// defaulting to .\SQLEXPRESS / Authenticator.
/// </summary>
public static class ConnectionConfig
{
    public const string DefaultServer = ".\\SQLEXPRESS";
    public const string DefaultDatabase = "Authenticator";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private static string? _cached;
    private static string? _cachedServer;
    private static string? _cachedDatabase;

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

    public static string Server => Resolve().Server;

    public static string Database => Resolve().Database;

    public static string ConnectionString => Resolve().ConnectionString;

    /// <summary>Writes connection.json (used by the installer and optional local overrides).</summary>
    public static void Write(string server, string database)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(server);
        ArgumentException.ThrowIfNullOrWhiteSpace(database);

        var dto = new ConnectionFile
        {
            Server = server.Trim(),
            Database = database.Trim()
        };
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(dto, JsonOptions));
        _cached = null;
        _cachedServer = null;
        _cachedDatabase = null;
    }

    private static (string Server, string Database, string ConnectionString) Resolve()
    {
        if (_cached is not null && _cachedServer is not null && _cachedDatabase is not null)
            return (_cachedServer, _cachedDatabase, _cached);

        var server = DefaultServer;
        var database = DefaultDatabase;

        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var file = JsonSerializer.Deserialize<ConnectionFile>(json, JsonOptions);
                if (!string.IsNullOrWhiteSpace(file?.Server))
                    server = file.Server.Trim();
                if (!string.IsNullOrWhiteSpace(file?.Database))
                    database = file.Database.Trim();
            }
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Failed to read connection.json; using defaults.", nameof(ConnectionConfig));
        }

        var connectionString =
            $"Server={server};Database={database};Trusted_Connection=True;TrustServerCertificate=True";
        _cachedServer = server;
        _cachedDatabase = database;
        _cached = connectionString;
        return (server, database, connectionString);
    }

    private sealed class ConnectionFile
    {
        public string? Server { get; set; }
        public string? Database { get; set; }
    }
}
