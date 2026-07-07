using System;
using System.Linq;
using System.Threading.Tasks;
using Authenticator.Logging;
using Authenticator.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Authenticator.Views;

public sealed partial class SettingsPage : Page
{
    private readonly DatabaseService _databaseService = new();
    private readonly SettingsService _settingsService = new();
    private bool _isInitializing;

    public SettingsPage()
    {
        LoggingConfiguration.LogInfo("SettingsPage initialization started.", nameof(SettingsPage));
        InitializeComponent();
        Loaded += SettingsPage_Loaded;
        LoggingConfiguration.LogInfo("SettingsPage initialization completed.", nameof(SettingsPage));
    }

    private async void SettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _isInitializing = true;

        var settings = await _settingsService.GetSettingsAsync();
        AccountsCountTextBlock.Text = (await _databaseService.GetAccountCountAsync()).ToString();
        BackupCodesCountTextBlock.Text = (await _databaseService.GetTotalBackupCodeCountAsync()).ToString();
        TrashCountTextBlock.Text = (await _databaseService.GetTrashCountAsync()).ToString();

        SyncTimeToggleSwitch.IsOn = settings.SyncTimeWithInternet;

        foreach (var item in AutoLockComboBox.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag?.ToString() == settings.AutoLockTimeout.ToString())
            {
                AutoLockComboBox.SelectedItem = item;
                break;
            }
        }

        if (AutoLockComboBox.SelectedItem is null && AutoLockComboBox.Items.Count > 0)
        {
            AutoLockComboBox.SelectedIndex = 0;
        }

        _isInitializing = false;
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await LoadAsync();
    }

    private async void AutoLockComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || AutoLockComboBox.SelectedItem is not ComboBoxItem selectedItem)
        {
            return;
        }

        if (int.TryParse(selectedItem.Tag?.ToString(), out var seconds))
        {
            await _settingsService.SetAutoLockTimeoutAsync(seconds);
        }
    }

    private async void SyncTimeToggleSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        await _settingsService.SetSyncTimeWithInternetAsync(SyncTimeToggleSwitch.IsOn);
    }

    private async void EmptyTrashButton_Click(object sender, RoutedEventArgs e)
    {
        await _databaseService.EmptyTrashAsync();
        await LoadAsync();
    }
}
