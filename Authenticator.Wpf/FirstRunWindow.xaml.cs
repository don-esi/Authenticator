using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using Authenticator.Logging;
using Authenticator.Services;
using Microsoft.Win32;

namespace Authenticator;

public partial class FirstRunWindow : Window
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly LockCredentialStore _lockStore = new();
    private readonly WindowsHelloUnlockService _hello = new();
    private readonly AccountImportService _import = new();
    private bool _helloAvailable;

    public FirstRunWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        _helloAvailable = await _hello.IsAvailableAsync();
        HelloPanel.Visibility = _helloAvailable ? Visibility.Visible : Visibility.Collapsed;
        RefreshUnlockUi();
    }

    private void RefreshUnlockUi()
    {
        var credentials = _lockStore.Load();
        PinStatusText.Text = credentials.PinEnabled ? "PIN: set" : "PIN: not set";
        HelloStatusText.Text = credentials.HelloUnlockEnabled ? "Windows Hello: enabled" : "Windows Hello: not enabled";
        ContinueButton.IsEnabled = credentials.HasUnlockMethod;
    }

    private async void SetPin_Click(object sender, RoutedEventArgs e)
    {
        var pin = PinBox.Password;
        var confirm = PinConfirmBox.Password;
        if (pin.Length < 4)
        {
            StatusText.Text = "PIN must be at least 4 characters.";
            return;
        }

        if (!string.Equals(pin, confirm, StringComparison.Ordinal))
        {
            StatusText.Text = "PIN and confirmation do not match.";
            return;
        }

        try
        {
            _lockStore.SetPin(pin);
            PinBox.Clear();
            PinConfirmBox.Clear();
            if (VaultKeyService.Instance.IsUnlocked)
                await new SecretMigrationService().EncryptLegacySecretsAsync();
            StatusText.Text = "PIN saved. Secrets will be encrypted at rest.";
            RefreshUnlockUi();
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "First-run PIN failed.", nameof(FirstRunWindow));
            StatusText.Text = ex.Message;
        }
    }

    private async void EnableHello_Click(object sender, RoutedEventArgs e)
    {
        if (!_helloAvailable)
        {
            StatusText.Text = "Windows Hello is not available on this device.";
            return;
        }

        try
        {
            var consented = await _hello.RequestConsentAsync("Enable Windows Hello unlock for Authenticator");
            if (!consented)
            {
                StatusText.Text = "Windows Hello was cancelled or failed.";
                return;
            }

            _lockStore.SetHello();
            if (VaultKeyService.Instance.IsUnlocked)
                await new SecretMigrationService().EncryptLegacySecretsAsync();
            StatusText.Text = "Windows Hello enabled.";
            RefreshUnlockUi();
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "First-run Hello failed.", nameof(FirstRunWindow));
            StatusText.Text = ex.Message;
        }
    }

    private async void ImportJson_Click(object sender, RoutedEventArgs e)
    {
        if (!_lockStore.Load().HasUnlockMethod)
        {
            ImportStatusText.Text = "Set a PIN or enable Windows Hello before importing.";
            return;
        }

        var dialog = new OpenFileDialog
        {
            Filter = "JSON files (*.json)|*.json",
            InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            var json = await File.ReadAllTextAsync(dialog.FileName);
            var file = JsonSerializer.Deserialize<AuthenticatorExport>(json, JsonOptions);
            if (file?.Accounts is null)
            {
                ImportStatusText.Text = "The file has no accounts.";
                return;
            }

            var result = await _import.ImportAsync(file);
            ImportStatusText.Text =
                $"Imported {result.Accounts} accounts and {result.Folders} folders. Skipped {result.Duplicates} duplicates, {result.Invalid} invalid.";
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "First-run JSON import failed.", nameof(FirstRunWindow));
            ImportStatusText.Text = "Could not read that file.";
        }
    }

    private async void ImportGoogleUri_Click(object sender, RoutedEventArgs e)
    {
        if (!_lockStore.Load().HasUnlockMethod)
        {
            ImportStatusText.Text = "Set a PIN or enable Windows Hello before importing.";
            return;
        }

        var text = GoogleMigrationBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            ImportStatusText.Text = "Paste an otpauth-migration:// link first.";
            return;
        }

        var parsed = GoogleMigrationParser.Parse(text);
        if (parsed is null)
        {
            ImportStatusText.Text = "Could not parse that Google Authenticator export.";
            return;
        }

        try
        {
            var result = await _import.ImportGoogleAccountsAsync(parsed);
            ImportStatusText.Text =
                $"Imported {result.Accounts} Google accounts. Skipped {result.Duplicates} duplicates, {result.Unsupported} unsupported.";
            GoogleMigrationBox.Text = string.Empty;
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "First-run Google import failed.", nameof(FirstRunWindow));
            ImportStatusText.Text = ex.Message;
        }
    }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        if (!_lockStore.Load().HasUnlockMethod)
        {
            StatusText.Text = "Set a PIN or enable Windows Hello to continue.";
            return;
        }

        DialogResult = true;
        Close();
    }
}
