using System.Windows;
using System.Windows.Media;
using Authenticator.Logging;
using Microsoft.Win32;

namespace Authenticator.Services;

/// <summary>
/// Applies light/dark/system chrome by swapping shared DynamicResource brushes.
/// Prefs are stored with <see cref="CodesUiPreferences"/> (LocalAppData).
/// </summary>
public static class ThemeService
{
    private static AppThemeMode _mode = AppThemeMode.System;
    private static bool _listening;

    public static AppThemeMode CurrentMode => _mode;

    public static event EventHandler? ThemeChanged;

    public static void Initialize(AppThemeMode mode)
    {
        _mode = mode;
        EnsureSystemListener();
        Apply();
    }

    public static void SetMode(AppThemeMode mode)
    {
        if (_mode == mode)
        {
            Apply();
            return;
        }

        _mode = mode;
        EnsureSystemListener();
        Apply();
        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    public static void Apply()
    {
        var dark = ResolveDark();
        var app = Application.Current;
        if (app is null)
            return;

        SetBrush(app, "PageBackgroundBrush", dark ? "#121417" : "#F8F9FA");
        SetBrush(app, "CardBackgroundBrush", dark ? "#1E222A" : "#F4F6FC");
        SetBrush(app, "SecondaryBackgroundBrush", dark ? "#2A2F3A" : "#F3F4F6");
        SetBrush(app, "PrimaryTextBrush", dark ? "#F3F4F6" : "#1F2937");
        SetBrush(app, "SecondaryTextBrush", dark ? "#9CA3AF" : "#6B7280");
        SetBrush(app, "BrandBlueBrush", dark ? "#3B9AE8" : "#0078D4");
        SetBrush(app, "BrandBlueHoverBrush", dark ? "#2B7FC0" : "#005A9E");
        SetBrush(app, "BorderBrushColor", dark ? "#374151" : "#E5E7EB");
        SetBrush(app, "NavBarBackgroundBrush", dark ? "#1A1D24" : "#FFFFFF");
        SetBrush(app, "ToastBackgroundBrush", dark ? "#0B0D10" : "#1F2937");
        SetBrush(app, "IconHoverBrush", dark ? "#374151" : "#E5E7EB");
        SetBrush(app, "ProgressTrackBrush", dark ? "#374151" : "#E5E7EB");
        SetBrush(app, "SwitchTrackBrush", dark ? "#4B5563" : "#D1D5DB");
        SetBrush(app, "DangerBrush", "#B00020");
        SetBrush(app, "ChipIdleBrush", dark ? "#243044" : "#E8F0FE");
        SetBrush(app, "ChipIdleTextBrush", dark ? "#F3F4F6" : "#1F2937");
        SetBrush(app, "ChipIdleMutedBrush", dark ? "#9CA3AF" : "#4B5563");
    }

    private static bool ResolveDark()
    {
        if (_mode == AppThemeMode.Dark)
            return true;
        if (_mode == AppThemeMode.Light)
            return false;
        return IsSystemDark();
    }

    private static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            return value is int light && light == 0;
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Failed to read system theme.", nameof(ThemeService));
            return false;
        }
    }

    private static void EnsureSystemListener()
    {
        if (_listening)
            return;
        _listening = true;
        SystemEvents.UserPreferenceChanged += (_, args) =>
        {
            if (_mode != AppThemeMode.System)
                return;
            if (args.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color))
                return;
            Application.Current?.Dispatcher.Invoke(Apply);
        };
    }

    private static void SetBrush(Application app, string key, string hex)
    {
        var color = (Color)ColorConverter.ConvertFromString(hex);
        app.Resources[key] = new SolidColorBrush(color);
    }
}
