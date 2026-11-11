using System.IO;
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
using Microsoft.EntityFrameworkCore;
using Microsoft.Win32;
using OtpNet;

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
    private bool _loading = true;
    private bool _busy;
    private bool _helloAvailable;
    private int _timeoutSeconds;
    private AppThemeMode _themeMode = AppThemeMode.System;
    private string _dockPosition = "None";

    public event EventHandler<int>? AutoLockChanged;
    public event EventHandler? UnlockMethodsChanged;
    public event EventHandler? DataChanged;
    public event EventHandler? DesktopSettingsChanged;

    public SettingsView()
    {
        InitializeComponent();
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
            MessageBox.Show(
                Window.GetWindow(this),
                "Set a PIN, register a YubiKey, or enable Windows Hello before enabling auto-lock.",
                "Auto-lock",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
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
            MessageBox.Show(
                Window.GetWindow(this),
                "Cannot clear the last unlock method while secrets are encrypted. Add another unlock method first.",
                "Clear PIN",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (!credentials.YubiKeyEnabled && !credentials.HelloUnlockEnabled && _timeoutSeconds > 0)
        {
            var confirm = MessageBox.Show(
                Window.GetWindow(this),
                "Clearing the only unlock method will also turn auto-lock off. Continue?",
                "Clear PIN",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes)
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
            MessageBox.Show(
                Window.GetWindow(this),
                "Cannot remove the last unlock method while secrets are encrypted. Add another unlock method first.",
                "Remove YubiKey",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (!credentials.PinEnabled && !credentials.HelloUnlockEnabled && _timeoutSeconds > 0)
        {
            var confirm = MessageBox.Show(
                Window.GetWindow(this),
                "Removing the only unlock method will also turn auto-lock off. Continue?",
                "Remove YubiKey",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes)
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
            MessageBox.Show(
                Window.GetWindow(this),
                "Cannot remove the last unlock method while secrets are encrypted. Add another unlock method first.",
                "Remove Windows Hello",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (!credentials.PinEnabled && !credentials.YubiKeyEnabled && _timeoutSeconds > 0)
        {
            var confirm = MessageBox.Show(
                Window.GetWindow(this),
                "Removing the only unlock method will also turn auto-lock off. Continue?",
                "Remove Windows Hello",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes)
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

            var result = await ImportAsync(file);
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

        var result = await ImportGoogleAccountsAsync(parsed);
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

    private async Task<(int Accounts, int Duplicates, int Unsupported)> ImportGoogleAccountsAsync(GoogleMigrationResult payload)
    {
        var existing = await _database.GetAllAccountsAsync();
        var known = existing
            .Select(account => Key(account.ServiceName, account.Username, account.SecretKey))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var added = 0;
        var duplicates = 0;
        foreach (var account in payload.Accounts)
        {
            var service = account.ServiceName.Trim();
            var username = account.Username.Trim();
            var secret = AccountEdits.NormalizeSecret(account.SecretKey);
            if (string.IsNullOrWhiteSpace(service) || string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(secret)
                || !IsValidBase32(secret))
            {
                // Count as unsupported alongside HOTP/invalid from the parser.
                continue;
            }

            if (!known.Add(Key(service, username, secret)))
            {
                duplicates++;
                continue;
            }

            await _database.AddAccountAsync(new Account
            {
                ServiceName = service,
                Username = username,
                SecretKey = secret,
                Algorithm = NormalizeAlgorithm(account.Algorithm),
                Digits = account.Digits == 8 ? 8 : 6,
                Period = NormalizePeriod(account.Period)
            });
            added++;
        }

        // Accounts rejected after parse (bad base32 / empty) fold into unsupported.
        var rejected = payload.Accounts.Count - added - duplicates;
        return (added, duplicates, payload.UnsupportedCount + Math.Max(0, rejected));
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

    private async Task<(int Accounts, int Folders, int Duplicates, int Invalid)> ImportAsync(AuthenticatorExport file)
    {
        var existingFolders = await _database.GetAllFoldersAsync();
        var folderIds = existingFolders.ToDictionary(folder => folder.Name, folder => folder.Id, StringComparer.OrdinalIgnoreCase);
        var foldersAdded = 0;

        foreach (var folder in file.Folders ?? [])
        {
            if (string.IsNullOrWhiteSpace(folder.Name) || folderIds.ContainsKey(folder.Name))
                continue;

            var created = await _database.AddFolderAsync(new Folder
            {
                Name = folder.Name.Trim(),
                Color = string.IsNullOrWhiteSpace(folder.Color) ? "#4ECDC4" : folder.Color.Trim()
            });
            folderIds[created.Name] = created.Id;
            foldersAdded++;
        }

        var existing = await _database.GetAllAccountsAsync();
        var known = existing
            .Select(account => Key(account.ServiceName, account.Username, account.SecretKey))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var accountsAdded = 0;
        var duplicates = 0;
        var invalid = 0;
        foreach (var account in file.Accounts)
        {
            var secret = AccountEdits.NormalizeSecret(account.SecretKey ?? string.Empty);
            var service = account.ServiceName?.Trim() ?? string.Empty;
            var username = account.Username?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(service) || string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(secret)
                || !IsValidBase32(secret))
            {
                invalid++;
                continue;
            }

            if (!known.Add(Key(service, username, secret)))
            {
                duplicates++;
                continue;
            }

            int? folderId = null;
            if (!string.IsNullOrWhiteSpace(account.FolderName) && folderIds.TryGetValue(account.FolderName, out var id))
                folderId = id;

            var saved = await _database.AddAccountAsync(new Account
            {
                ServiceName = service,
                ServiceUrl = string.IsNullOrWhiteSpace(account.ServiceUrl) ? null : account.ServiceUrl.Trim(),
                Username = username,
                SecretKey = secret,
                Algorithm = NormalizeAlgorithm(account.Algorithm),
                Digits = account.Digits == 8 ? 8 : 6,
                Period = NormalizePeriod(account.Period),
                FolderId = folderId,
                IsFavorite = account.IsFavorite
            });

            var codes = account.BackupCodes?
                .Where(code => !string.IsNullOrWhiteSpace(code.Code))
                .ToList();
            if (codes is { Count: > 0 })
                await AddImportedCodesAsync(saved.Id, codes);

            accountsAdded++;
        }

        return (accountsAdded, foldersAdded, duplicates, invalid);
    }

    private static async Task AddImportedCodesAsync(int accountId, List<BackupCodeExport> codes)
    {
        await using var context = new AuthenticatorDbContext();
        foreach (var code in codes)
        {
            context.BackupCodes.Add(new BackupCode
            {
                AccountId = accountId,
                Code = code.Code.Trim(),
                IsUsed = code.IsUsed,
                CreatedAt = DateTime.UtcNow,
                UsedAt = code.IsUsed ? DateTime.UtcNow : null
            });
        }

        await context.SaveChangesAsync();
    }

    private static string Key(string service, string username, string secret)
        => $"{service.Trim()}|{username.Trim()}|{AccountEdits.NormalizeSecret(secret)}";

    private static bool IsValidBase32(string secret)
    {
        try
        {
            _ = Base32Encoding.ToBytes(secret);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string NormalizeAlgorithm(string? algorithm)
    {
        return algorithm?.Trim().ToUpperInvariant() switch
        {
            "SHA256" => "SHA256",
            "SHA512" => "SHA512",
            _ => "SHA1"
        };
    }

    private static int NormalizePeriod(int period)
        => period is 15 or 30 or 60 or 120 ? period : 30;
}

public sealed class AuthenticatorExport
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; set; } = CurrentFormatVersion;
    public List<FolderExport> Folders { get; set; } = [];
    public List<AccountExport> Accounts { get; set; } = [];
}

public sealed class FolderExport
{
    public string Name { get; set; } = string.Empty;
    public string Color { get; set; } = "#4ECDC4";
}

public sealed class AccountExport
{
    public string ServiceName { get; set; } = string.Empty;
    public string? ServiceUrl { get; set; }
    public string Username { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;
    public string Algorithm { get; set; } = "SHA1";
    public int Digits { get; set; } = 6;
    public int Period { get; set; } = 30;
    public string? FolderName { get; set; }
    public bool IsFavorite { get; set; }
    public List<BackupCodeExport> BackupCodes { get; set; } = [];
}

public sealed class BackupCodeExport
{
    public string Code { get; set; } = string.Empty;
    public bool IsUsed { get; set; }
}
