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

            await EnsureBackupCodeColumnWidthAsync(context);
            await EnsureDockHandleOffsetColumnAsync(context);

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

    /// <summary>
    /// Phase C ciphertext for backup codes exceeds the legacy nvarchar(50) column.
    /// </summary>
    private static async Task EnsureBackupCodeColumnWidthAsync(AuthenticatorDbContext context)
    {
        try
        {
            await context.Database.ExecuteSqlRawAsync(
                """
                IF EXISTS (
                    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
                    WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = 'BackupCodes' AND COLUMN_NAME = 'Code'
                      AND CHARACTER_MAXIMUM_LENGTH IS NOT NULL AND CHARACTER_MAXIMUM_LENGTH < 512)
                BEGIN
                    ALTER TABLE dbo.BackupCodes ALTER COLUMN Code nvarchar(512) NOT NULL;
                END
                """);
            LoggingConfiguration.LogInfo("BackupCodes.Code column width ensured (>= 512).", nameof(DatabaseInitializer));
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Failed to widen BackupCodes.Code column.", nameof(DatabaseInitializer));
            throw;
        }
    }

    /// <summary>
    /// Phase E dock handle Y offset on existing AppSettings rows.
    /// </summary>
    private static async Task EnsureDockHandleOffsetColumnAsync(AuthenticatorDbContext context)
    {
        try
        {
            await context.Database.ExecuteSqlRawAsync(
                """
                IF NOT EXISTS (
                    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
                    WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = 'AppSettings' AND COLUMN_NAME = 'WindowDockHandleOffsetY')
                BEGIN
                    ALTER TABLE dbo.AppSettings ADD WindowDockHandleOffsetY int NOT NULL CONSTRAINT DF_AppSettings_WindowDockHandleOffsetY DEFAULT 120;
                END
                """);
            LoggingConfiguration.LogInfo("AppSettings.WindowDockHandleOffsetY column ensured.", nameof(DatabaseInitializer));
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Failed to ensure WindowDockHandleOffsetY column.", nameof(DatabaseInitializer));
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
                WindowDockHandleOffsetY = 120,
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
