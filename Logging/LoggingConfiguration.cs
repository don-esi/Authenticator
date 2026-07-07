using System;
using System.IO;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Authenticator.Logging;

/// <summary>
/// Centralized application diagnostics logger based on Serilog.
/// Writes to both Visual Studio Debug output and rolling log files.
/// </summary>
public static class LoggingConfiguration
{
    private static readonly object Sync = new();
    private static bool _isInitialized;
    private static bool _isShuttingDown;
    private static string _logFilePath = string.Empty;

    public static string LogFilePath => _logFilePath;

    public static void Initialize(string applicationName = "Authenticator")
    {
        lock (Sync)
        {
            if (_isInitialized || _isShuttingDown)
                return;

            var logDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            Directory.CreateDirectory(logDirectory);

            _logFilePath = Path.Combine(logDirectory, "log-.txt");

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                .Enrich.FromLogContext()
                .WriteTo.Debug(outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}")
                .WriteTo.File(
                    path: _logFilePath,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14,
                    shared: false,
                    outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}")
                .CreateLogger();

            _isInitialized = true;
            _isShuttingDown = false;
        }

        LogInformation("Logging initialized", nameof(LoggingConfiguration));
        LogInformation($"Log file pattern: {_logFilePath}", nameof(LoggingConfiguration));
    }

    public static void Shutdown()
    {
        lock (Sync)
        {
            if (!_isInitialized || _isShuttingDown)
                return;

            _isShuttingDown = true;
            _isInitialized = false;
            Log.Logger = Logger.None;
            _isShuttingDown = false;
        }
    }

    public static void LogDebug(string message, string? source = null)
    {
        if (!EnsureInitialized())
            return;

        Log.ForContext("SourceContext", source ?? "General").Debug("{Message}", message);
    }

    public static void LogInfo(string message, string? source = null) => LogInformation(message, source);

    public static void LogInformation(string message, string? source = null)
    {
        if (!EnsureInitialized())
            return;

        Log.ForContext("SourceContext", source ?? "General").Information("{Message}", message);
    }

    public static void LogWarning(string message, string? source = null)
    {
        if (!EnsureInitialized())
            return;

        Log.ForContext("SourceContext", source ?? "General").Warning("{Message}", message);
    }

    public static void LogError(string message, Exception? exception = null, string? source = null)
    {
        if (!EnsureInitialized())
            return;

        var logger = Log.ForContext("SourceContext", source ?? "General");

        if (exception is null)
            logger.Error("{Message}", message);
        else
            logger.Error(exception, "{Message}", message);
    }

    public static void LogException(Exception exception, string message, string? source = null)
        => LogError(message, exception, source);

    private static bool EnsureInitialized()
    {
        lock (Sync)
        {
            if (_isShuttingDown)
                return false;

            if (_isInitialized)
                return true;
        }

        Initialize();

        lock (Sync)
        {
            return _isInitialized && !_isShuttingDown;
        }
    }

}
