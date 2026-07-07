using System;
using Authenticator.Data;
using Microsoft.UI.Xaml;

namespace Authenticator;

public partial class App : Application
{
    public static Window? MainWindow
    {
        get; private set;
    }

    public App()
    {
        InitializeComponent();
    }

    protected async override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        Authenticator.Logging.LoggingConfiguration.Initialize();
        Authenticator.Logging.LoggingConfiguration.LogInfo("Application launch started.", nameof(App));

        // Initialize database
        try
        {
            Authenticator.Logging.LoggingConfiguration.LogDebug("Initializing database.", nameof(App));
            await DatabaseInitializer.InitializeDatabaseAsync();
            Authenticator.Logging.LoggingConfiguration.LogInfo("Database initialization completed.", nameof(App));
        }
        catch (Exception ex)
        {
            Authenticator.Logging.LoggingConfiguration.LogException(ex, "Database initialization failed.", nameof(App));
        }

        MainWindow = new MainWindow();
        MainWindow.Activate();

        Authenticator.Logging.LoggingConfiguration.LogInfo("Main window activated.", nameof(App));
    }
}
