using Authenticator.Logging;
using Microsoft.EntityFrameworkCore;
using System;
using System.Threading.Tasks;

namespace Authenticator.Data;

/// <summary>
/// Handles database initialization and migrations
/// </summary>
public static class DatabaseInitializer
{
    /// <summary>
    /// Ensures the database is created and migrated to the latest version
    /// </summary>
    public static async Task InitializeDatabaseAsync()
    {
        LoggingConfiguration.LogInfo("Database initialization started.", nameof(DatabaseInitializer));

        try
        {
            using var context = new AuthenticatorDbContext();

            // Create database if it doesn't exist based on the model
            // Note: EnsureCreatedAsync does not use migrations. For production apps,
            // use migrations with MigrateAsync() instead.
            var created = await context.Database.EnsureCreatedAsync();
            LoggingConfiguration.LogInfo($"EnsureCreatedAsync completed. DatabaseCreated={created}.", nameof(DatabaseInitializer));

            // Ensure at least one AppSettings record exists
            await EnsureDefaultSettingsAsync(context);
            LoggingConfiguration.LogInfo("Database initialization finished successfully.", nameof(DatabaseInitializer));
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Database initialization failed.", nameof(DatabaseInitializer));
            throw;
        }
    }

    private static async Task EnsureDefaultSettingsAsync(AuthenticatorDbContext context)
    {
        var settings = await context.AppSettings.FirstOrDefaultAsync();
        if (settings == null)
        {
            LoggingConfiguration.LogInfo("No AppSettings record found. Creating default settings.", nameof(DatabaseInitializer));

            context.AppSettings.Add(new Models.AppSettings
            {
                AutoLockTimeout = 0,
                SyncTimeWithInternet = false,
                WindowDockPosition = "None",
                IsWindowCollapsed = false,
                WindowX = 100,
                WindowY = 100,
                WindowWidth = 778,
                WindowHeight = 1490
            });
            await context.SaveChangesAsync();

            LoggingConfiguration.LogInfo("Default AppSettings record created.", nameof(DatabaseInitializer));
        }
        else
        {
            LoggingConfiguration.LogDebug($"Existing AppSettings record found (Id={settings.Id}).", nameof(DatabaseInitializer));
        }
    }
}
