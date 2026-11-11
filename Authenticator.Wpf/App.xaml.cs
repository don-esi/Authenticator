using System.Windows;
using Authenticator.Logging;
using Authenticator.Services;

namespace Authenticator;

public partial class App : Application
{
    private TrayIconService? _tray;
    private GlobalHotkeyService? _hotkey;
    private MainWindow? _mainWindow;
    private bool _exiting;

    public App()
    {
        InitializeComponent();
    }

    public new static App Current => (App)Application.Current;

    public MainWindow? MainWindowInstance => _mainWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        LoggingConfiguration.Initialize();
        DispatcherUnhandledException += (_, args) =>
        {
            LoggingConfiguration.LogException(args.Exception, "Unhandled UI exception.", nameof(App));
            MessageBox.Show(args.Exception.Message, "Authenticator", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        var prefs = new CodesUiPreferences().Load();
        ThemeService.Initialize(prefs.ThemeMode);
        LoggingConfiguration.LogInfo("WPF application launch started.", nameof(App));

        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _mainWindow = new MainWindow();
        MainWindow = _mainWindow;
        _mainWindow.Show();

        _tray = new TrayIconService();
        _tray.Initialize();
        _tray.OpenRequested += (_, _) => Dispatcher.Invoke(() => _mainWindow?.TryShowForUser());
        _tray.ExitRequested += (_, _) => Dispatcher.Invoke(RequestExit);

        _hotkey = new GlobalHotkeyService();
        _hotkey.Attach(_mainWindow);
        _hotkey.HotkeyPressed += (_, _) => Dispatcher.Invoke(() => _mainWindow?.TryShowForUser());
        ApplyHotkeyPreference(prefs.GlobalHotkeyEnabled);

        base.OnStartup(e);
    }

    public void ApplyHotkeyPreference(bool enabled)
    {
        if (_hotkey is null)
            return;

        if (enabled)
            _hotkey.TryRegister();
        else
            _hotkey.TryUnregister();
    }

    public void RequestExit()
    {
        if (_exiting)
            return;
        _exiting = true;
        LoggingConfiguration.LogInfo("Application exit requested.", nameof(App));

        try
        {
            _hotkey?.Dispose();
            _hotkey = null;
            _tray?.Dispose();
            _tray = null;
            _mainWindow?.ForceClose();
            _mainWindow = null;
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Error during RequestExit cleanup.", nameof(App));
        }

        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hotkey?.Dispose();
        _tray?.Dispose();
        LoggingConfiguration.LogInfo("WPF application exit.", nameof(App));
        LoggingConfiguration.Shutdown();
        base.OnExit(e);
    }
}
