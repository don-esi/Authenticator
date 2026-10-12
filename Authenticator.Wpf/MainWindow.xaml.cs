using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Authenticator.Logging;
using Authenticator.Services;

namespace Authenticator;

public partial class MainWindow : Window
{
    private readonly SettingsService _settings = new();
    private readonly DispatcherTimer _lockTimer;
    private DateTime _lastActivityUtc = DateTime.UtcNow;
    private int _timeoutSeconds;
    private bool _locked;

    public MainWindow()
    {
        InitializeComponent();
        SettingsPage.AutoLockChanged += (_, seconds) =>
        {
            _timeoutSeconds = seconds;
            _lastActivityUtc = DateTime.UtcNow;
        };
        SettingsPage.DataChanged += async (_, _) => await CodesPage.ReloadAsync();
        _lockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _lockTimer.Tick += (_, _) => CheckAutoLock();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = await _settings.GetSettingsAsync();
            _timeoutSeconds = settings.AutoLockTimeout;
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Failed to load auto-lock settings.", nameof(MainWindow));
        }

        _lastActivityUtc = DateTime.UtcNow;
        _lockTimer.Start();
        ShowPage(CodesPage);
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _lockTimer.Stop();
    }

    private void Window_PreviewActivity(object sender, InputEventArgs e)
    {
        if (!_locked)
            _lastActivityUtc = DateTime.UtcNow;
    }

    private void CheckAutoLock()
    {
        if (_locked || _timeoutSeconds <= 0)
            return;

        if (DateTime.UtcNow - _lastActivityUtc >= TimeSpan.FromSeconds(_timeoutSeconds))
            Lock();
    }

    private void Lock()
    {
        _locked = true;
        PageHost.Visibility = Visibility.Collapsed;
        LockOverlay.Visibility = Visibility.Visible;
        LoggingConfiguration.LogInfo("Auto-lock engaged.", nameof(MainWindow));
    }

    private void Unlock_Click(object sender, RoutedEventArgs e)
    {
        _locked = false;
        _lastActivityUtc = DateTime.UtcNow;
        LockOverlay.Visibility = Visibility.Collapsed;
        PageHost.Visibility = Visibility.Visible;
        if (CodesPage.Visibility == Visibility.Visible)
            CodesPage.RefreshCodes();
        LoggingConfiguration.LogInfo("Auto-lock cleared.", nameof(MainWindow));
    }

    private void CodesNav_Click(object sender, RoutedEventArgs e) => ShowPage(CodesPage);

    private async void BackupNav_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(BackupPage);
        await BackupPage.ReloadAsync();
    }

    private async void SettingsNav_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(SettingsPage);
        await SettingsPage.ReloadAsync();
    }

    private void ShowPage(UIElement page)
    {
        CodesPage.Visibility = page == CodesPage ? Visibility.Visible : Visibility.Collapsed;
        BackupPage.Visibility = page == BackupPage ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == SettingsPage ? Visibility.Visible : Visibility.Collapsed;
        SetNavBrush(CodesNavButton, page == CodesPage);
        SetNavBrush(BackupNavButton, page == BackupPage);
        SetNavBrush(SettingsNavButton, page == SettingsPage);
    }

    private static void SetNavBrush(System.Windows.Controls.Button button, bool selected)
    {
        button.Foreground = selected
            ? new SolidColorBrush(Color.FromRgb(0, 120, 212))
            : new SolidColorBrush(Color.FromRgb(107, 114, 128));
    }
}
