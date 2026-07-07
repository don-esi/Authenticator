using Authenticator.Data;
using Authenticator.Data.Models;
using Authenticator.Logging;
using Microsoft.EntityFrameworkCore;
using System;
using System.Threading.Tasks;

namespace Authenticator.Services;

/// <summary>
/// Service for managing application settings
/// </summary>
public class SettingsService
{
    private AppSettings? _cachedSettings;

    public async Task<AppSettings> GetSettingsAsync()
    {
        if (_cachedSettings != null)
        {
            LoggingConfiguration.LogDebug("Returning cached AppSettings.", nameof(SettingsService));
            return _cachedSettings;
        }

        LoggingConfiguration.LogDebug("Loading AppSettings from database.", nameof(SettingsService));

        using var context = new AuthenticatorDbContext();
        _cachedSettings = await context.AppSettings.FirstOrDefaultAsync();

        if (_cachedSettings == null)
        {
            LoggingConfiguration.LogInfo("No AppSettings found. Creating default settings.", nameof(SettingsService));

            _cachedSettings = new AppSettings
            {
                AutoLockTimeout = 0,
                SyncTimeWithInternet = false,
                WindowDockPosition = "None",
                IsWindowCollapsed = false,
                WindowX = 100,
                WindowY = 100,
                WindowWidth = 778,
                WindowHeight = 1490
            };
            context.AppSettings.Add(_cachedSettings);
            await context.SaveChangesAsync();

            LoggingConfiguration.LogInfo("Default AppSettings created.", nameof(SettingsService));
        }
        else
        {
            LoggingConfiguration.LogDebug($"AppSettings loaded (Id={_cachedSettings.Id}).", nameof(SettingsService));
        }

        return _cachedSettings;
    }

    public async Task UpdateSettingsAsync(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        LoggingConfiguration.LogInfo($"Updating AppSettings (Id={settings.Id}).", nameof(SettingsService));

        using var context = new AuthenticatorDbContext();
        settings.ModifiedAt = DateTime.UtcNow;
        context.AppSettings.Update(settings);
        await context.SaveChangesAsync();

        _cachedSettings = settings;
        LoggingConfiguration.LogDebug("AppSettings update persisted and cache refreshed.", nameof(SettingsService));
    }

    public async Task SetAutoLockTimeoutAsync(int seconds)
    {
        LoggingConfiguration.LogInfo($"Setting auto-lock timeout to {seconds} second(s).", nameof(SettingsService));
        var settings = await GetSettingsAsync();
        settings.AutoLockTimeout = seconds;
        await UpdateSettingsAsync(settings);
    }

    public async Task SetSyncTimeWithInternetAsync(bool enabled)
    {
        LoggingConfiguration.LogInfo($"Setting SyncTimeWithInternet to {enabled}.", nameof(SettingsService));
        var settings = await GetSettingsAsync();
        settings.SyncTimeWithInternet = enabled;
        await UpdateSettingsAsync(settings);
    }

    public async Task SetWindowPositionAsync(int x, int y, int width, int height)
    {
        LoggingConfiguration.LogDebug($"Updating window position to X={x}, Y={y}, Width={width}, Height={height}.", nameof(SettingsService));
        var settings = await GetSettingsAsync();
        settings.WindowX = x;
        settings.WindowY = y;
        settings.WindowWidth = width;
        settings.WindowHeight = height;
        await UpdateSettingsAsync(settings);
    }

    public async Task SetWindowDockStateAsync(string dockPosition, bool isCollapsed)
    {
        LoggingConfiguration.LogDebug($"Updating dock state to Position='{dockPosition}', IsCollapsed={isCollapsed}.", nameof(SettingsService));
        var settings = await GetSettingsAsync();
        settings.WindowDockPosition = dockPosition;
        settings.IsWindowCollapsed = isCollapsed;
        await UpdateSettingsAsync(settings);
    }
}
