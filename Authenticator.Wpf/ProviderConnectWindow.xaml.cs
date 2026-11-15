using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Authenticator.Services;
using Authenticator.Services.Providers;
using Microsoft.Win32;

namespace Authenticator;

public partial class ProviderConnectWindow : Window
{
    private readonly List<IVaultProvider> _providers = VaultProviderCatalog.CreateAll().ToList();
    private readonly ProviderDialogMode _mode;

    public IVaultProvider? ResultProvider { get; private set; }
    public int? ImportedCount { get; private set; }

    public ProviderConnectWindow(ProviderDialogMode mode)
    {
        _mode = mode;
        InitializeComponent();

        Title = mode == ProviderDialogMode.Connect ? "Connect to password manager" : "Import from password manager";
        TitleText.Text = mode == ProviderDialogMode.Connect ? "Connect" : "Import";
        ModeHint.Text = mode == ProviderDialogMode.Connect
            ? "Credentials are stored on this PC (DPAPI) and the provider stays connected until you Disconnect."
            : "One-shot import only. Credentials are not retained after the import finishes.";
        PrimaryButton.Content = mode == ProviderDialogMode.Connect ? "Connect" : "Import";
        AutomationProperties.SetName(PrimaryButton, (string)PrimaryButton.Content);
        ImportAfterConnectCheck.Visibility = mode == ProviderDialogMode.Connect
            ? Visibility.Visible
            : Visibility.Collapsed;

        foreach (var p in _providers)
            ProviderBox.Items.Add(p.DisplayName);
        ProviderBox.SelectedIndex = 0;
        UpdateFieldVisibility();
        PrefillIfUpdatingConnection();
    }

    private IVaultProvider? Selected =>
        ProviderBox.SelectedIndex >= 0 ? _providers[ProviderBox.SelectedIndex] : null;

    private void PrefillIfUpdatingConnection()
    {
        if (_mode != ProviderDialogMode.Connect)
            return;

        var selected = Selected;
        if (selected is null)
            return;

        var saved = ProviderSessionService.Instance.SavedConnections
            .FirstOrDefault(c => string.Equals(c.ProviderId, selected.ProviderId, StringComparison.OrdinalIgnoreCase));
        if (saved is null)
            return;

        UserBox.Text = saved.Username ?? "";
        PathBox.Text = saved.FilePath ?? "";
        KeyBox.Text = saved.KeyFilePath ?? "";
        if (!string.IsNullOrEmpty(saved.Password))
            PassBox.Password = saved.Password;
    }

    private void ProviderBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateFieldVisibility();
        PrefillIfUpdatingConnection();
    }

    private void UpdateFieldVisibility()
    {
        var p = Selected;
        if (p is null)
            return;

        var c = p.Capabilities;
        CapsText.Text =
            $"Favorites={c.SupportsFavorites}, WriteBack={c.SupportsWriteBack}, File={c.RequiresFilePath}, User={c.RequiresUsername}";

        UserLabel.Visibility = c.RequiresUsername ? Visibility.Visible : Visibility.Collapsed;
        UserBox.Visibility = c.RequiresUsername ? Visibility.Visible : Visibility.Collapsed;

        PathLabel.Text = c.RequiresFilePath ? "Vault file" : "JSON export file (optional)";
        KeyFilePanel.Visibility = c.RequiresFilePath ? Visibility.Visible : Visibility.Collapsed;
        if (!c.RequiresFilePath)
            KeyBox.Text = string.Empty;
    }

    private void BrowsePath_Click(object sender, RoutedEventArgs e)
    {
        var requiresVault = Selected?.Capabilities.RequiresFilePath == true;
        var dialog = new OpenFileDialog
        {
            Title = requiresVault ? "Select vault file" : "Select JSON export",
            CheckFileExists = true,
            Multiselect = false,
            Filter = requiresVault
                ? "Vault files (*.kdbx;*.opvault)|*.kdbx;*.opvault|KeePass (*.kdbx)|*.kdbx|All files (*.*)|*.*"
                : "JSON export (*.json)|*.json|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog(this) == true)
            PathBox.Text = dialog.FileName;
    }

    private void BrowseKey_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select key file",
            CheckFileExists = true,
            Multiselect = false,
            Filter = "Key files (*.key;*.keyx)|*.key;*.keyx|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog(this) == true)
            KeyBox.Text = dialog.FileName;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private async void Primary_Click(object sender, RoutedEventArgs e)
    {
        var p = Selected;
        if (p is null) return;

        PrimaryButton.IsEnabled = false;
        try
        {
            var request = new ProviderConnectRequest
            {
                Username = UserBox.Text.Trim(),
                Password = PassBox.Password,
                FilePath = PathBox.Text.Trim(),
                KeyFilePath = string.IsNullOrWhiteSpace(KeyBox.Text) ? null : KeyBox.Text.Trim()
            };

            if (_mode == ProviderDialogMode.Connect)
            {
                var importAfter = ImportAfterConnectCheck.IsChecked == true;
                var (provider, imported) = await ProviderSessionService.Instance.ConnectAndPersistAsync(
                    p, request, importAfter);
                ResultProvider = provider;
                ImportedCount = imported;
            }
            else
            {
                var (provider, imported) = await ProviderSessionService.Instance.ImportOnceAsync(p, request);
                ResultProvider = provider;
                ImportedCount = imported;
            }

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, _mode == ProviderDialogMode.Connect ? "Connect failed" : "Import failed",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            PrimaryButton.IsEnabled = true;
        }
    }
}
