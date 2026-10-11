using System.Windows;
using Authenticator.Logging;

namespace Authenticator;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        LoggingConfiguration.Initialize();
        DispatcherUnhandledException += (_, args) =>
        {
            LoggingConfiguration.LogException(args.Exception, "Unhandled UI exception.", nameof(App));
            MessageBox.Show(args.Exception.Message, "Authenticator", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        LoggingConfiguration.LogInfo("WPF application launch started.", nameof(App));
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        LoggingConfiguration.LogInfo("WPF application exit.", nameof(App));
        LoggingConfiguration.Shutdown();
        base.OnExit(e);
    }
}
