using System.IO;
using System.Text.Json;
using System.Windows;
using Authenticator.Logging;
using Syncfusion.Licensing;
using Syncfusion.SfSkinManager;
using Syncfusion.Windows.Tools.Controls;

namespace Authenticator.Services;

/// <summary>
/// Registers Syncfusion license and applies Windows11 skin (matches WealthForge).
/// </summary>
public static class SyncfusionBootstrap
{
    public static void Initialize()
    {
        try
        {
            SfSkinManager.ApplyStylesOnApplication = true;
            RegisterLicense();
            ApplyThemeForCurrentMode();
            SuppressDefaultButtonAdvIcons();
            ThemeService.ThemeChanged += (_, _) => ApplyThemeForCurrentMode();
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Syncfusion bootstrap failed.", nameof(SyncfusionBootstrap));
        }
    }

    /// <summary>
    /// ButtonAdv paints a built-in placeholder glyph when SmallIcon is unset; skin styles can also
    /// revive it after App.xaml setters. Collapse icon chrome unless the control opted into one.
    /// </summary>
    private static void SuppressDefaultButtonAdvIcons()
    {
        EventManager.RegisterClassHandler(
            typeof(ButtonAdv),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler((_, args) =>
            {
                if (args.OriginalSource is not ButtonAdv button)
                    return;
                if (button.SmallIcon is not null || button.LargeIcon is not null || button.IconTemplate is not null)
                    return;

                button.IconWidth = 0;
                button.IconHeight = 0;
            }));
    }

    public static void ApplyThemeForCurrentMode()
    {
        var dark = ThemeService.CurrentMode switch
        {
            AppThemeMode.Dark => true,
            AppThemeMode.Light => false,
            _ => IsSystemDarkSafe()
        };

        var themeName = dark ? "Windows11Dark" : "Windows11Light";
        SfSkinManager.ApplicationTheme = new Theme(themeName);

        if (Application.Current?.MainWindow is Window main)
            SfSkinManager.SetTheme(main, new Theme(themeName));
    }

    public static void ApplyTo(DependencyObject target)
    {
        if (target is null)
            return;
        var dark = ThemeService.CurrentMode == AppThemeMode.Dark
                   || (ThemeService.CurrentMode == AppThemeMode.System && IsSystemDarkSafe());
        SfSkinManager.SetTheme(target, new Theme(dark ? "Windows11Dark" : "Windows11Light"));
    }

    private static void RegisterLicense()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (!File.Exists(path))
                return;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("SyncFusion", out var syncfusion)
                || !syncfusion.TryGetProperty("LicenseKey", out var licenseElement))
                return;

            var licenseKey = licenseElement.GetString();
            if (!string.IsNullOrWhiteSpace(licenseKey))
                SyncfusionLicenseProvider.RegisterLicense(licenseKey);
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Failed to register Syncfusion license.", nameof(SyncfusionBootstrap));
        }
    }

    private static bool IsSystemDarkSafe()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            return value is int light && light == 0;
        }
        catch
        {
            return false;
        }
    }
}
