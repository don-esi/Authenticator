using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Authenticator.Data;
using Authenticator.Data.Models;
using Authenticator.Logging;
using Authenticator.Services;
using Microsoft.Win32;

namespace Authenticator;

public partial class SettingsView : UserControl
{
    private static readonly (int Seconds, string Label)[] LockOptions =
    [
        (30, "30 Seconds"),
        (60, "1 Minute"),
        (120, "2 Minutes"),
        (180, "3 Minutes"),
        (240, "4 Minutes"),
        (300, "5 Minutes"),
        (0, "Never")
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly (AppThemeMode Mode, string Label)[] ThemeOptions =
    [
        (AppThemeMode.System, "System"),
        (AppThemeMode.Light, "Light"),
        (AppThemeMode.Dark, "Dark")
    ];

    private static readonly (string Value, string Label)[] DockOptions =
    [
        ("None", "Off"),
        ("Left", "Left edge"),
        ("Right", "Right edge")
    ];

    private static readonly (string Hex, string Label)[] DockHandleColors =
    [
        ("#E53935", "Red"),
        ("#0078D4", "Blue"),
        ("#43A047", "Green"),
        ("#FB8C00", "Orange"),
        ("#8E24AA", "Purple"),
        ("#546E7A", "Slate")
    ];

    private readonly DatabaseService _database = new();
    private readonly SettingsService _settings = new();
    private readonly CodesUiPreferences _uiPrefs = new();
    private readonly LockCredentialStore _lockStore = new();
    private readonly YubiKeyUnlockService _yubi = new();
    private readonly WindowsHelloUnlockService _hello = new();
    private readonly QrCodeService _qr = new();
    private readonly AccountImportService _import = new();
    private readonly DebugPreferences _debugPrefs = new();
    private readonly VaultItemService _vaultItems = new();
    private bool _loading = true;
    private bool _busy;
    private bool _helloAvailable;
    private int _timeoutSeconds;
    private AppThemeMode _themeMode = AppThemeMode.System;
    private string _dockPosition = "None";

    public event EventHandler<int>? AutoLockChanged;
    public event EventHandler? UnlockMethodsChanged;
    public event EventHandler? DataChanged;
    /// <summary>Developer vault purge completed — vault UI should clear items and Conflicts.</summary>
    public event EventHandler? VaultPurged;
    public event EventHandler? DesktopSettingsChanged;
    public event EventHandler? BackRequested;

    public SettingsView()
    {
        InitializeComponent();
        AppVersionText.Text = $"Authenticator {GetAppVersion()}";
    }

    public async Task ReloadAsync()
    {
        _loading = true;
        try
        {
            AccountCountText.Text = (await _database.GetAccountCountAsync()).ToString();
            FolderCountText.Text = (await _database.GetAllFoldersAsync()).Count.ToString();
            var settings = await _settings.GetSettingsAsync();
            _timeoutSeconds = settings.AutoLockTimeout;
            _dockPosition = NormalizeDock(settings.WindowDockPosition);
            var ui = _uiPrefs.Load();
            _themeMode = ui.ThemeMode;
            _helloAvailable = await _hello.IsAvailableAsync();
            RefreshUnlockMethodUi();
            BuildThemeOptions();
            BuildDockOptions();
            BuildDesktopToggles(ui);
            BuildLockOptions();
            LockMethodStatus.Text = string.Empty;
            AppVersionText.Text = $"Authenticator {GetAppVersion()}";
            RefreshConnectionUi();
            RefreshUnlockSummary();
            RefreshDeveloperUi();
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Failed to load settings.", nameof(SettingsView));
            TransferStatus.Text = ex.Message;
        }
        finally
        {
            _loading = false;
        }
    }

    private void RefreshDeveloperUi()
    {
        var debug = _debugPrefs.Load();
        DeveloperModeToggle.IsChecked = debug.DeveloperMode;
        DeveloperActionsPanel.Visibility = debug.DeveloperMode ? Visibility.Visible : Visibility.Collapsed;
        DeveloperStatus.Text = string.Empty;
    }

    private void DeveloperMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;

        var enabled = DeveloperModeToggle.IsChecked == true;
        _debugPrefs.Save(new DebugPreferencesState { DeveloperMode = enabled });
        DeveloperActionsPanel.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        DeveloperStatus.Text = enabled
            ? "Developer mode on — purge is available."
            : string.Empty;
    }

    private async void PurgeVault_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;

        var confirm = MessageBox.Show(
            "Hard-delete ALL Shared vault logins, custom fields, provider links, and conflicts?\n\n" +
            "2FA accounts, folders, unlock methods, and provider Connect credentials are kept.\n" +
            "You can re-import from KeePass / LastPass afterward.",
            "Purge Shared vault",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes)
            return;

        _busy = true;
        PurgeVaultButton.IsEnabled = false;
        DeveloperStatus.Text = "Purging…";
        try
        {
            var result = await _vaultItems.PurgeAllVaultDataAsync();
            DeveloperStatus.Text =
                $"Purged {result.ItemsDeleted} items and {result.LinksDeleted} links; " +
                $"remaining={result.RemainingItems} (conflicts cleared).";
            LoggingConfiguration.LogInfo(
                $"Settings purge: deleted items={result.ItemsDeleted}, links={result.LinksDeleted}, remaining={result.RemainingItems}.",
                nameof(SettingsView));
            VaultPurged?.Invoke(this, EventArgs.Empty);
            DataChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Vault purge failed.", nameof(SettingsView));
            DeveloperStatus.Text = ex.Message;
            MessageBox.Show(ex.Message, "Purge failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            PurgeVaultButton.IsEnabled = true;
            _busy = false;
        }
    }

    private void RefreshConnectionUi()
    {
        var active = ConnectionConfig.ActiveProfile;
        ConnectionStatusText.Text =
            $"Active: {ConnectionConfig.ActiveProfileName} → {active.Server} / {active.Database}" +
            (ConnectionConfig.IsOfflineMode ? " (OFFLINE)" : "");
        ConnectionSummaryText.Text =
            $"{ConnectionConfig.Role} · {ConnectionConfig.ActiveProfileName} · {active.Server}" +
            (ConnectionConfig.IsOfflineMode ? " · OFFLINE" : "");
        ProfileCombo.Items.Clear();
        foreach (var p in ConnectionConfig.Profiles)
            ProfileCombo.Items.Add(p.Name);
        ProfileCombo.SelectedItem = ConnectionConfig.ActiveProfileName;
        ServerBox.Text = active.Server;
        DatabaseBox.Text = active.Database;
        foreach (ComboBoxItem item in RoleCombo.Items)
        {
            if (string.Equals(item.Tag?.ToString(), ConnectionConfig.Role.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                RoleCombo.SelectedItem = item;
                break;
            }
        }
    }

    private void RefreshUnlockSummary()
    {
        var credentials = _lockStore.Load();
        var parts = new List<string>();
        if (credentials.PinEnabled) parts.Add("PIN");
        if (credentials.YubiKeyEnabled) parts.Add("YubiKey");
        if (credentials.HelloUnlockEnabled) parts.Add("Hello");
        UnlockSummaryText.Text = parts.Count == 0 ? "No unlock method set" : string.Join(" · ", parts);
    }

    private void ProfileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ProfileCombo.SelectedItem is not string name)
            return;
        var profile = ConnectionConfig.Profiles.FirstOrDefault(p => p.Name == name);
        if (profile is null) return;
        ServerBox.Text = profile.Server;
        DatabaseBox.Text = profile.Database;
    }

    private void SaveConnection_Click(object sender, RoutedEventArgs e)
    {
        var name = ProfileCombo.SelectedItem as string ?? ConnectionConfig.SharedProfileName;
        ConnectionConfig.UpsertProfile(new ConnectionProfile
        {
            Name = name,
            Server = ServerBox.Text.Trim(),
            Database = string.IsNullOrWhiteSpace(DatabaseBox.Text) ? "Authenticator" : DatabaseBox.Text.Trim()
        });
        ConnectionConfig.SetActiveProfile(name);
        if (RoleCombo.SelectedItem is ComboBoxItem roleItem &&
            Enum.TryParse<ConnectionRole>(roleItem.Tag?.ToString(), true, out var role))
            ConnectionConfig.SetRole(role);
        ConnectionActionStatus.Text = "Saved. Restart or unlock to refresh DB connection.";
        RefreshConnectionUi();
    }

    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        ConnectionActionStatus.Text = "Testing…";
        var ok = await ConnectionConfig.TestConnectionAsync(ServerBox.Text.Trim(), DatabaseBox.Text.Trim());
        ConnectionActionStatus.Text = ok ? "Connection OK." : "Connection failed.";
        await SharedConnectionMonitor.Instance.RefreshAsync();
        RefreshConnectionUi();
    }

    private async void Discover_Click(object sender, RoutedEventArgs e)
    {
        ConnectionActionStatus.Text = "Discovering on LAN…";
        var found = await SqlDiscoveryService.ProbeAsync();
        DiscoveryList.ItemsSource = found.ToList();
        ConnectionActionStatus.Text = found.Count == 0
            ? "No hosts found. Check firewall UDP 38455 or enter server manually."
            : $"Found {found.Count}. Double-click a row to apply.";
    }

    private void DiscoveryList_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DiscoveryList.SelectedItem is not SqlDiscoveryAdvertisement ad
            || string.IsNullOrWhiteSpace(ad.Server))
            return;

        ServerBox.Text = ad.Server;
        DatabaseBox.Text = string.IsNullOrWhiteSpace(ad.Database) ? "Authenticator" : ad.Database;
        ConnectionConfig.UpsertProfile(new ConnectionProfile
        {
            Name = ConnectionConfig.SharedProfileName,
            Server = ad.Server,
            Database = DatabaseBox.Text,
            Discovered = true
        });
        ConnectionConfig.SetActiveProfile(ConnectionConfig.SharedProfileName);
        ConnectionConfig.SetRole(ConnectionRole.Join);
        if (!string.IsNullOrWhiteSpace(ad.VdekFingerprintPrefix))
            ConnectionConfig.SetFingerprintPrefix(ad.VdekFingerprintPrefix);
        ConnectionActionStatus.Text = $"Applied discovered host {ad.Server}.";
        RefreshConnectionUi();
    }

    private async void EnrollRecovery_Click(object sender, RoutedEventArgs e)
    {
        var key = RecoveryKeyBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(key))
        {
            ConnectionActionStatus.Text = "Enter recovery key.";
            return;
        }

        var ok = await new VaultCryptoService().EnrollWithRecoveryKeyAsync(key);
        ConnectionActionStatus.Text = ok
            ? "VDEK enrolled in session. Set/confirm PIN or Hello while unlocked to wrap for this machine."
            : "Enrollment failed (wrong key or VaultCrypto missing).";
    }

    private async void ReconcileOffline_Click(object sender, RoutedEventArgs e)
    {
        await SharedConnectionMonitor.Instance.RefreshAsync();
        if (ConnectionConfig.IsOfflineMode)
        {
            ConnectionActionStatus.Text = "Still offline.";
            return;
        }

        var n = await new OfflineVaultService().ReconcileToSharedAsync();
        ConnectionActionStatus.Text = $"Reconciled {n} queued change(s).";
        RefreshConnectionUi();
    }

    private void RefreshUnlockMethodUi()
    {
        var credentials = _lockStore.Load();
        PinStatusText.Text = credentials.PinEnabled ? "PIN: set" : "PIN: not set";
        ClearPinButton.IsEnabled = credentials.PinEnabled;

        if (credentials.YubiKeyEnabled)
        {
            var label = _yubi.GetDeviceLabel();
            YubiStatusText.Text = label is null ? "YubiKey: registered" : $"YubiKey: registered ({label})";
        }
        else
        {
            YubiStatusText.Text = "YubiKey: not registered";
        }

        ClearYubiButton.IsEnabled = credentials.YubiKeyEnabled;

        HelloPanel.Visibility = _helloAvailable || credentials.HelloUnlockEnabled
            ? Visibility.Visible
            : Visibility.Collapsed;
        HelloStatusText.Text = credentials.HelloUnlockEnabled ? "Windows Hello: enabled" : "Windows Hello: not enabled";
        EnableHelloButton.IsEnabled = _helloAvailable && !credentials.HelloUnlockEnabled;
        ClearHelloButton.IsEnabled = credentials.HelloUnlockEnabled;
        RefreshUnlockSummary();
    }

    private void BuildThemeOptions()
    {
        ThemePanel.Children.Clear();
        foreach (var option in ThemeOptions)
        {
            var selected = option.Mode == _themeMode;
            var button = new Button
            {
                Content = selected ? $"{option.Label}    ✓" : option.Label,
                Tag = option.Mode,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 2, 0, 2),
                Style = (Style)FindResource("SecondaryButton"),
                Background = System.Windows.Media.Brushes.Transparent,
                FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal
            };
            button.Click += Theme_Click;
            ThemePanel.Children.Add(button);
        }
    }

    private void BuildLockOptions()
    {
        AutoLockPanel.Children.Clear();
        var known = LockOptions.Select(option => option.Seconds).ToHashSet();
        var options = LockOptions.ToList();
        if (!known.Contains(_timeoutSeconds))
            options.Insert(0, (_timeoutSeconds, $"{_timeoutSeconds} seconds"));

        foreach (var option in options)
        {
            var selected = option.Seconds == _timeoutSeconds;
            var button = new Button
            {
                Content = selected ? $"{option.Label}    ✓" : option.Label,
                Tag = option.Seconds,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 2, 0, 2),
                Style = (Style)FindResource("SecondaryButton"),
                Background = System.Windows.Media.Brushes.Transparent,
                FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal
            };
            button.Click += AutoLock_Click;
            AutoLockPanel.Children.Add(button);
        }
    }

    private void BuildDockOptions()
    {
        DockPanel.Children.Clear();
        foreach (var option in DockOptions)
        {
            var selected = string.Equals(option.Value, _dockPosition, StringComparison.OrdinalIgnoreCase);
            var button = new Button
            {
                Content = selected ? $"{option.Label}    ✓" : option.Label,
                Tag = option.Value,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 2, 0, 2),
                Style = (Style)FindResource("SecondaryButton"),
                Background = System.Windows.Media.Brushes.Transparent,
                FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal
            };
            button.Click += Dock_Click;
            DockPanel.Children.Add(button);
        }
    }

    private void BuildDesktopToggles(CodesUiPreferencesState ui)
    {
        AlwaysOnTopToggle.IsChecked = ui.AlwaysOnTopWhenDocked;
        MinimizeToTrayToggle.IsChecked = ui.MinimizeToTray;
        GlobalHotkeyToggle.IsChecked = ui.GlobalHotkeyEnabled;
        BuildDockHandleColorOptions(ui.DockHandleColor);
        var opacity = Math.Clamp(ui.DockHandleOpacity, 0.25, 1.0);
        DockHandleOpacitySlider.Value = opacity;
        DockHandleOpacityLabel.Text = $"{(int)Math.Round(opacity * 100)}%";
    }

    private void BuildDockHandleColorOptions(string? selectedHex)
    {
        DockHandleColorPanel.Children.Clear();
        var selected = NormalizeHandleColor(selectedHex);
        foreach (var option in DockHandleColors)
        {
            var isSelected = string.Equals(option.Hex, selected, StringComparison.OrdinalIgnoreCase);
            var swatch = new Button
            {
                Width = 28,
                Height = 28,
                Margin = new Thickness(0, 0, 8, 4),
                Tag = option.Hex,
                ToolTip = option.Label,
                Background = (Brush)new BrushConverter().ConvertFromString(option.Hex)!,
                BorderBrush = isSelected
                    ? (Brush)FindResource("BrandBlueBrush")
                    : (Brush)FindResource("BorderBrushColor"),
                BorderThickness = new Thickness(isSelected ? 2.5 : 1),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            swatch.Click += DockHandleColor_Click;
            DockHandleColorPanel.Children.Add(swatch);
        }
    }

    private void DockHandleColor_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not Button { Tag: string hex })
            return;

        var state = _uiPrefs.Load();
        state.DockHandleColor = NormalizeHandleColor(hex);
        _uiPrefs.Save(state);
        BuildDockHandleColorOptions(state.DockHandleColor);
        DesktopSettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void DockHandleOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || DockHandleOpacityLabel is null)
            return;

        var opacity = Math.Clamp(e.NewValue, 0.25, 1.0);
        DockHandleOpacityLabel.Text = $"{(int)Math.Round(opacity * 100)}%";

        var state = _uiPrefs.Load();
        if (Math.Abs(state.DockHandleOpacity - opacity) < 0.001)
            return;

        state.DockHandleOpacity = opacity;
        _uiPrefs.Save(state);
        DesktopSettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string NormalizeHandleColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return "#E53935";
        hex = hex.Trim();
        if (!hex.StartsWith('#'))
            hex = "#" + hex;
        foreach (var option in DockHandleColors)
        {
            if (string.Equals(option.Hex, hex, StringComparison.OrdinalIgnoreCase))
                return option.Hex;
        }
        return hex.Length is 7 or 9 ? hex.ToUpperInvariant() : "#E53935";
    }

    private async void Dock_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not Button { Tag: string position })
            return;

        var normalized = NormalizeDock(position);
        if (string.Equals(normalized, _dockPosition, StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            var settings = await _settings.GetSettingsAsync();
            await _settings.SetWindowDockStateAsync(normalized, isCollapsed: false, settings.WindowDockHandleOffsetY);
            _dockPosition = normalized;
            BuildDockOptions();

            if (Window.GetWindow(this) is MainWindow main)
                await main.SetDockPositionFromSettingsAsync(normalized);

            DesktopSettingsChanged?.Invoke(this, EventArgs.Empty);
            LoggingConfiguration.LogInfo($"Dock position set to {normalized}.", nameof(SettingsView));
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Failed to save dock position.", nameof(SettingsView));
            TransferStatus.Text = ex.Message;
        }
    }

    private void DesktopToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;

        var state = _uiPrefs.Load();
        state.AlwaysOnTopWhenDocked = AlwaysOnTopToggle.IsChecked == true;
        state.MinimizeToTray = MinimizeToTrayToggle.IsChecked == true;
        state.GlobalHotkeyEnabled = GlobalHotkeyToggle.IsChecked == true;
        _uiPrefs.Save(state);

        App.Current.ApplyHotkeyPreference(state.GlobalHotkeyEnabled);
        DesktopSettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Back_Click(object sender, RoutedEventArgs e)
        => BackRequested?.Invoke(this, EventArgs.Empty);

    private void ExitApp_Click(object sender, RoutedEventArgs e)
    {
        App.Current.RequestExit();
    }

    private static string NormalizeDock(string? position)
    {
        if (string.Equals(position, "Left", StringComparison.OrdinalIgnoreCase))
            return "Left";
        if (string.Equals(position, "Right", StringComparison.OrdinalIgnoreCase))
            return "Right";
        return "None";
    }

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not Button { Tag: AppThemeMode mode } || mode == _themeMode)
            return;

        _themeMode = mode;
        var state = _uiPrefs.Load();
        state.ThemeMode = _themeMode;
        _uiPrefs.Save(state);
        ThemeService.SetMode(_themeMode);
        BuildThemeOptions();
    }

    private async void AutoLock_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not Button { Tag: int seconds } || seconds == _timeoutSeconds)
            return;

        if (seconds > 0 && !_lockStore.Load().HasUnlockMethod)
        {
            AppDialog.Alert(
                Window.GetWindow(this),
                "Set a PIN, register a YubiKey, or enable Windows Hello before enabling auto-lock.",
                "Auto-lock");
            return;
        }

        try
        {
            await _settings.SetAutoLockTimeoutAsync(seconds);
            _timeoutSeconds = seconds;
            BuildLockOptions();
            AutoLockChanged?.Invoke(this, seconds);
            LoggingConfiguration.LogInfo($"Auto-lock timeout set to {seconds} second(s).", nameof(SettingsView));
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Failed to save auto-lock timeout.", nameof(SettingsView));
            TransferStatus.Text = ex.Message;
        }
    }

    private void SetPin_Click(object sender, RoutedEventArgs e)
    {
        var pin = PinBox.Password;
        var confirm = PinConfirmBox.Password;
        if (pin.Length < 4)
        {
            LockMethodStatus.Text = "PIN must be at least 4 characters.";
            return;
        }

        if (!string.Equals(pin, confirm, StringComparison.Ordinal))
        {
            LockMethodStatus.Text = "PIN and confirmation do not match.";
            return;
        }

        try
        {
            _lockStore.SetPin(pin);
            PinBox.Clear();
            PinConfirmBox.Clear();
            _ = EncryptSecretsAfterUnlockMethodChangeAsync("PIN saved. Secrets encrypted at rest.");
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Failed to set PIN.", nameof(SettingsView));
            LockMethodStatus.Text = ex.Message;
        }
    }

    private async void ClearPin_Click(object sender, RoutedEventArgs e)
    {
        var credentials = _lockStore.Load();
        if (!credentials.PinEnabled)
            return;

        if (credentials.UnlockMethodCount <= 1 && credentials.HasEncryptedVault)
        {
            AppDialog.Alert(
                Window.GetWindow(this),
                "Cannot clear the last unlock method while secrets are encrypted. Add another unlock method first.",
                "Clear PIN",
                AppDialogKind.Warning);
            return;
        }

        if (!credentials.YubiKeyEnabled && !credentials.HelloUnlockEnabled && _timeoutSeconds > 0)
        {
            if (!AppDialog.Confirm(
                Window.GetWindow(this),
                "Clearing the only unlock method will also turn auto-lock off. Continue?",
                "Clear PIN",
                confirmText: "Clear PIN",
                kind: AppDialogKind.Confirm))
                return;
        }

        try
        {
            _lockStore.ClearPin();
        }
        catch (Exception ex)
        {
            LockMethodStatus.Text = ex.Message;
            return;
        }

        RefreshUnlockMethodUi();
        if (!_lockStore.Load().HasUnlockMethod && _timeoutSeconds > 0)
        {
            await _settings.SetAutoLockTimeoutAsync(0);
            _timeoutSeconds = 0;
            BuildLockOptions();
            AutoLockChanged?.Invoke(this, 0);
        }

        UnlockMethodsChanged?.Invoke(this, EventArgs.Empty);
        LockMethodStatus.Text = "PIN cleared.";
    }

    private void RegisterYubiKey_Click(object sender, RoutedEventArgs e)
    {
        LockMethodStatus.Text = "Touch your YubiKey…";
        try
        {
            var registration = _yubi.Enroll(
                Dispatcher,
                Window.GetWindow(this),
                message => LockMethodStatus.Text = message);
            _lockStore.SetYubiKeyRegistration(registration);
            _ = EncryptSecretsAfterUnlockMethodChangeAsync("YubiKey registered. Secrets encrypted at rest.");
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Failed to register YubiKey.", nameof(SettingsView));
            LockMethodStatus.Text = ex.Message;
        }
    }

    private async void ClearYubiKey_Click(object sender, RoutedEventArgs e)
    {
        var credentials = _lockStore.Load();
        if (!credentials.YubiKeyEnabled)
            return;

        if (credentials.UnlockMethodCount <= 1 && credentials.HasEncryptedVault)
        {
            AppDialog.Alert(
                Window.GetWindow(this),
                "Cannot remove the last unlock method while secrets are encrypted. Add another unlock method first.",
                "Remove YubiKey",
                AppDialogKind.Warning);
            return;
        }

        if (!credentials.PinEnabled && !credentials.HelloUnlockEnabled && _timeoutSeconds > 0)
        {
            if (!AppDialog.Confirm(
                Window.GetWindow(this),
                "Removing the only unlock method will also turn auto-lock off. Continue?",
                "Remove YubiKey",
                confirmText: "Remove",
                kind: AppDialogKind.Confirm))
                return;
        }

        try
        {
            _lockStore.ClearYubiKey();
        }
        catch (Exception ex)
        {
            LockMethodStatus.Text = ex.Message;
            return;
        }

        RefreshUnlockMethodUi();
        if (!_lockStore.Load().HasUnlockMethod && _timeoutSeconds > 0)
        {
            await _settings.SetAutoLockTimeoutAsync(0);
            _timeoutSeconds = 0;
            BuildLockOptions();
            AutoLockChanged?.Invoke(this, 0);
        }

        UnlockMethodsChanged?.Invoke(this, EventArgs.Empty);
        LockMethodStatus.Text = "YubiKey registration removed from this app.";
    }

    private async void EnableHello_Click(object sender, RoutedEventArgs e)
    {
        if (!_helloAvailable)
        {
            LockMethodStatus.Text = "Windows Hello is not available on this device.";
            return;
        }

        var main = Window.GetWindow(this) as MainWindow;
        main?.BeginDockAutoHideSuppress();
        try
        {
            var consented = await _hello.RequestConsentAsync("Enable Windows Hello unlock for Authenticator");
            if (!consented)
            {
                LockMethodStatus.Text = "Windows Hello was cancelled or failed.";
                return;
            }

            _lockStore.SetHello();
            await EncryptSecretsAfterUnlockMethodChangeAsync("Windows Hello enabled. Secrets encrypted at rest.");
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Failed to enable Windows Hello.", nameof(SettingsView));
            LockMethodStatus.Text = ex.Message;
        }
        finally
        {
            main?.EndDockAutoHideSuppress();
        }
    }

    private async void ClearHello_Click(object sender, RoutedEventArgs e)
    {
        var credentials = _lockStore.Load();
        if (!credentials.HelloUnlockEnabled)
            return;

        if (credentials.UnlockMethodCount <= 1 && credentials.HasEncryptedVault)
        {
            AppDialog.Alert(
                Window.GetWindow(this),
                "Cannot remove the last unlock method while secrets are encrypted. Add another unlock method first.",
                "Remove Windows Hello",
                AppDialogKind.Warning);
            return;
        }

        if (!credentials.PinEnabled && !credentials.YubiKeyEnabled && _timeoutSeconds > 0)
        {
            if (!AppDialog.Confirm(
                Window.GetWindow(this),
                "Removing the only unlock method will also turn auto-lock off. Continue?",
                "Remove Windows Hello",
                confirmText: "Remove",
                kind: AppDialogKind.Confirm))
                return;
        }

        try
        {
            _lockStore.ClearHello();
        }
        catch (Exception ex)
        {
            LockMethodStatus.Text = ex.Message;
            return;
        }

        RefreshUnlockMethodUi();
        if (!_lockStore.Load().HasUnlockMethod && _timeoutSeconds > 0)
        {
            await _settings.SetAutoLockTimeoutAsync(0);
            _timeoutSeconds = 0;
            BuildLockOptions();
            AutoLockChanged?.Invoke(this, 0);
        }

        UnlockMethodsChanged?.Invoke(this, EventArgs.Empty);
        LockMethodStatus.Text = "Windows Hello unlock removed.";
    }

    private async Task EncryptSecretsAfterUnlockMethodChangeAsync(string successMessage)
    {
        try
        {
            if (VaultKeyService.Instance.IsUnlocked)
                await new SecretMigrationService().EncryptLegacySecretsAsync();

            RefreshUnlockMethodUi();
            UnlockMethodsChanged?.Invoke(this, EventArgs.Empty);
            LockMethodStatus.Text = successMessage;
            DataChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Failed to encrypt secrets after unlock method change.", nameof(SettingsView));
            LockMethodStatus.Text = ex.Message;
        }
    }

    private static string DownloadsFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "JSON files (*.json)|*.json",
            FileName = "authenticator-export.json",
            InitialDirectory = DownloadsFolder
        };
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var export = await BuildExportAsync();
            await File.WriteAllTextAsync(dialog.FileName, JsonSerializer.Serialize(export, JsonOptions));
            TransferStatus.Text = $"Exported {export.Accounts.Count} accounts and {export.Folders.Count} folders (format v{export.FormatVersion}).";
            LoggingConfiguration.LogInfo($"Exported {export.Accounts.Count} account(s) and {export.Folders.Count} folder(s).", nameof(SettingsView));
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Export failed.", nameof(SettingsView));
            TransferStatus.Text = ex.Message;
        }
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "JSON files (*.json)|*.json",
            InitialDirectory = DownloadsFolder
        };
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var json = await File.ReadAllTextAsync(dialog.FileName);
            var file = JsonSerializer.Deserialize<AuthenticatorExport>(json, JsonOptions);
            if (file?.Accounts is null)
            {
                TransferStatus.Text = "The file has no accounts.";
                return;
            }

            var result = await _import.ImportAsync(file);
            TransferStatus.Text =
                $"Imported {result.Accounts} accounts and {result.Folders} folders. Skipped {result.Duplicates} duplicates, {result.Invalid} invalid.";
            LoggingConfiguration.LogInfo(
                $"Imported {result.Accounts} account(s) and {result.Folders} folder(s). Skipped {result.Duplicates} duplicate(s), {result.Invalid} invalid.",
                nameof(SettingsView));
            await ReloadAsync();
            DataChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Import failed.", nameof(SettingsView));
            TransferStatus.Text = "Could not read that file.";
        }
    }

    private async void ImportGoogleCapture_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;

        var owner = Window.GetWindow(this);
        _busy = true;
        double restoreOpacity = 1;
        double restoreLeft = double.NaN;
        double restoreTop = double.NaN;
        if (owner is not null)
        {
            restoreOpacity = owner.Opacity;
            restoreLeft = owner.Left;
            restoreTop = owner.Top;
            owner.Opacity = 0;
            if (!double.IsNaN(restoreLeft))
                owner.Left = -20000;
            if (!double.IsNaN(restoreTop))
                owner.Top = -20000;
        }

        try
        {
            var region = QrCaptureWindow.Select();
            if (region is null)
                return;

            await Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(100);

            using var bitmap = await _qr.CaptureScreenRegionAsync(region.Value.X, region.Value.Y, region.Value.Width, region.Value.Height);
            if (bitmap is null)
            {
                TransferStatus.Text = "Could not capture that region.";
                return;
            }

            var text = _qr.DecodeQrText(bitmap);
            if (string.IsNullOrWhiteSpace(text))
            {
                TransferStatus.Text = "No QR code found in that region.";
                return;
            }

            await ImportGoogleMigrationTextAsync(text);
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Google QR capture import failed.", nameof(SettingsView));
            TransferStatus.Text = "Could not capture that region.";
        }
        finally
        {
            if (owner is not null)
            {
                owner.Opacity = restoreOpacity;
                if (!double.IsNaN(restoreLeft))
                    owner.Left = restoreLeft;
                if (!double.IsNaN(restoreTop))
                    owner.Top = restoreTop;
                owner.Activate();
            }

            _busy = false;
        }
    }

    private async void ImportGoogleImage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a Google Authenticator QR image",
            Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;

        try
        {
            using var source = new System.Drawing.Bitmap(dialog.FileName);
            using var bitmap = new System.Drawing.Bitmap(source.Width, source.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
                graphics.DrawImage(source, 0, 0, source.Width, source.Height);

            var text = _qr.DecodeQrText(bitmap);
            if (string.IsNullOrWhiteSpace(text))
            {
                TransferStatus.Text = "No QR code found in that image.";
                return;
            }

            await ImportGoogleMigrationTextAsync(text);
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Google QR image import failed.", nameof(SettingsView));
            TransferStatus.Text = "Could not read that image.";
        }
    }

    private async void ImportGoogleUri_Click(object sender, RoutedEventArgs e)
    {
        var text = GoogleMigrationBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            TransferStatus.Text = "Paste an otpauth-migration:// link first.";
            return;
        }

        await ImportGoogleMigrationTextAsync(text);
    }

    private async Task ImportGoogleMigrationTextAsync(string text)
    {
        var parsed = GoogleMigrationParser.Parse(text);
        if (parsed is null)
        {
            TransferStatus.Text = "Could not parse that Google Authenticator export. Use a Transfer accounts QR or otpauth-migration:// link.";
            return;
        }

        if (parsed.Accounts.Count == 0 && parsed.UnsupportedCount == 0)
        {
            TransferStatus.Text = "That export contained no accounts.";
            return;
        }

        var result = await _import.ImportGoogleAccountsAsync(parsed);
        var batch = parsed.BatchSize > 0
            ? $" QR {parsed.BatchIndex + 1} of {parsed.BatchSize}."
            : string.Empty;
        TransferStatus.Text =
            $"Imported {result.Accounts} Google accounts. Skipped {result.Duplicates} duplicates, {result.Unsupported} unsupported.{batch}";
        LoggingConfiguration.LogInfo(
            $"Google import: added {result.Accounts}, duplicates {result.Duplicates}, unsupported {result.Unsupported}.{batch}",
            nameof(SettingsView));
        GoogleMigrationBox.Text = string.Empty;
        await ReloadAsync();
        DataChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task<AuthenticatorExport> BuildExportAsync()
    {
        var folders = await _database.GetAllFoldersAsync();
        var accounts = await _database.GetAllAccountsAsync();
        return new AuthenticatorExport
        {
            FormatVersion = AuthenticatorExport.CurrentFormatVersion,
            Folders = folders.Select(folder => new FolderExport
            {
                Name = folder.Name,
                Color = folder.Color
            }).ToList(),
            Accounts = accounts.Select(account => new AccountExport
            {
                ServiceName = account.ServiceName,
                ServiceUrl = account.ServiceUrl,
                Username = account.Username,
                SecretKey = account.SecretKey,
                Algorithm = account.Algorithm,
                Digits = account.Digits,
                Period = account.Period,
                FolderName = account.Folder?.Name,
                IsFavorite = account.IsFavorite,
                BackupCodes = account.BackupCodes
                    .Where(code => !code.IsDeleted)
                    .Select(code => new BackupCodeExport { Code = code.Code, IsUsed = code.IsUsed })
                    .ToList()
            }).ToList()
        };
    }

    private static string GetAppVersion()
    {
        var informational = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+');
            return plus > 0 ? informational[..plus] : informational;
        }

        return Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
    }
}
