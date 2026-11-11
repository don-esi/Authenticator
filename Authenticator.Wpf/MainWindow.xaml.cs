using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Authenticator.Data;
using Authenticator.Logging;
using Authenticator.Services;

namespace Authenticator;

public partial class MainWindow : Window
{
    private readonly SettingsService _settings = new();
    private readonly CodesUiPreferences _uiPrefs = new();
    private readonly LockCredentialStore _lockStore = new();
    private readonly YubiKeyUnlockService _yubi = new();
    private readonly WindowsHelloUnlockService _hello = new();
    private readonly DispatcherTimer _lockTimer;
    private readonly DispatcherTimer _yubiOtpIdleTimer;
    private DateTime _lastActivityUtc = DateTime.UtcNow;
    private int _timeoutSeconds;
    private bool _locked;
    private bool _codesMasked;
    private bool _hasUnlockMethod;
    private bool _yubiEnabled;
    private bool _helloEnabled;
    private int _lockFocusRetries;
    private bool _pinPanelForcedForYubiEcho;

    private string _dockPosition = "None";
    private bool _isCollapsed;
    private int _handleOffsetY = 120;
    private bool _alwaysOnTopWhenDocked = true;
    private bool _dockPinned;
    private bool _minimizeToTray = true;
    private bool _forceClose;
    private bool _dockAnimating;
    private int _suppressDockAutoHide;
    private DockHandleWindow? _dockHandle;
    private DockAutoHideService? _dockAutoHide;
    private readonly DispatcherTimer _dockMouseLeaveTimer;

    public MainWindow()
    {
        InitializeComponent();
        // Keep pages hidden until Loaded decides lock vs. show — otherwise CodesView
        // starts ReloadAsync on first show and steals keyboard focus from the PIN box.
        PageHost.Visibility = Visibility.Collapsed;
        SettingsPage.AutoLockChanged += (_, seconds) =>
        {
            _timeoutSeconds = seconds;
            _lastActivityUtc = DateTime.UtcNow;
            RefreshUnlockCapabilities();
        };
        SettingsPage.UnlockMethodsChanged += (_, _) =>
        {
            RefreshUnlockCapabilities();
            ConfigureLockOverlay();
        };
        SettingsPage.DataChanged += async (_, _) => await CodesPage.ReloadAsync();
        SettingsPage.DesktopSettingsChanged += (_, _) =>
        {
            LoadUiDesktopPrefs();
            ApplyTopmost();
            App.Current.ApplyHotkeyPreference(_uiPrefs.Load().GlobalHotkeyEnabled);
        };
        _lockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _lockTimer.Tick += (_, _) => CheckAutoLock();
        _yubiOtpIdleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _yubiOtpIdleTimer.Tick += (_, _) => TryConsumeYubiOtp();
        _dockMouseLeaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _dockMouseLeaveTimer.Tick += (_, _) =>
        {
            _dockMouseLeaveTimer.Stop();
            // IsMouseOver is reliable for Topmost dock panels; focus APIs are not.
            if (!IsMouseOver)
                RequestDockAutoHide("MouseLeave");
        };
        MouseEnter += (_, _) => _dockMouseLeaveTimer.Stop();
        MouseLeave += (_, _) =>
        {
            if (IsDocked && !_isCollapsed && IsVisible)
                _dockMouseLeaveTimer.Start();
        };
        _dockAutoHide = new DockAutoHideService(
            this,
            shouldWatch: () => IsDocked && !_isCollapsed && !_forceClose && !_dockPinned && IsVisible,
            canCollapse: () => !_dockAnimating && !_dockPinned && _suppressDockAutoHide == 0 && IsDocked && !_isCollapsed,
            collapseAsync: () => CollapseToDockAsync());
        SourceInitialized += (_, _) =>
        {
            var source = PresentationSource.FromVisual(this) as HwndSource;
            source?.AddHook(DockWndProc);
            UpdateDockAutoHideWatch();
        };
    }

    public bool IsLocked => _locked;
    public bool IsDockCollapsed => _isCollapsed;
    public string DockPosition => _dockPosition;

    public void TryShowForUser()
    {
        if (IsDocked && _isCollapsed)
        {
            _ = ExpandFromDockAsync();
            return;
        }

        if (!IsVisible)
            Show();

        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;

        ApplyTopmost();
        Activate();
        if (_locked)
            FocusLockInput();
    }

    public void ForceClose()
    {
        _forceClose = true;
        CloseDockHandle();
        Close();
    }

    /// <summary>Suppress dock auto-hide while system UI (e.g. Windows Hello) steals focus.</summary>
    public void BeginDockAutoHideSuppress() => _suppressDockAutoHide++;

    public void EndDockAutoHideSuppress()
    {
        if (_suppressDockAutoHide > 0)
            _suppressDockAutoHide--;
    }

    private bool IsDocked =>
        string.Equals(_dockPosition, "Left", StringComparison.OrdinalIgnoreCase)
        || string.Equals(_dockPosition, "Right", StringComparison.OrdinalIgnoreCase);

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // Lock before any awaits so the PIN box can take focus without racing
        // settings load or CodesView initialization.
        RefreshUnlockCapabilities();
        ConfigureLockOverlay();
        _lastActivityUtc = DateTime.UtcNow;

        if (_hasUnlockMethod)
            Lock();
        // else: keep PageHost collapsed until first-run wizard completes

        var databaseReady = false;
        try
        {
            await DatabaseInitializer.InitializeDatabaseAsync();
            databaseReady = true;
            var settings = await _settings.GetSettingsAsync();
            _timeoutSeconds = settings.AutoLockTimeout;
            if (!_hasUnlockMethod && _timeoutSeconds > 0)
                _timeoutSeconds = 0;

            _dockPosition = NormalizeDockPosition(settings.WindowDockPosition);
            _isCollapsed = settings.IsWindowCollapsed && IsDocked;
            _handleOffsetY = settings.WindowDockHandleOffsetY;
            LoadUiDesktopPrefs();
            UpdateCollapseButton();
            ApplyTopmost();

            if (IsDocked && _isCollapsed)
                await CollapseToDockAsync(persist: false, animate: false);
            else if (IsDocked)
                SnapExpandedToDockEdge();

            UpdateDockAutoHideWatch();
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Failed to load auto-lock / dock settings.", nameof(MainWindow));
            MessageBox.Show(
                this,
                $"Could not connect to SQL Server ({ConnectionConfig.Server}, database {ConnectionConfig.Database}).\n\n" +
                "Install SQL Server Express with the SQLEXPRESS instance, or set Server/Database in:\n" +
                $"{ConnectionConfig.ConfigPath}\n\n{ex.Message}",
                "Authenticator — database unavailable",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }

        if (databaseReady && !_hasUnlockMethod)
            await RunFirstRunWizardAsync();

        _lockTimer.Start();
    }

    private async Task RunFirstRunWizardAsync()
    {
        var wizard = new FirstRunWindow { Owner = this };
        var completed = wizard.ShowDialog() == true;
        RefreshUnlockCapabilities();
        ConfigureLockOverlay();

        if (!completed || !_hasUnlockMethod)
        {
            MessageBox.Show(
                this,
                "Authenticator needs a PIN or Windows Hello before you can use codes. Opening Settings.",
                "Setup required",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            PageHost.Visibility = Visibility.Visible;
            ShowPage(SettingsPage);
            await SettingsPage.ReloadAsync();
            return;
        }

        if (VaultKeyService.Instance.IsUnlocked)
            VaultKeyService.Instance.Clear();

        PageHost.Visibility = Visibility.Visible;
        Lock();
        await CodesPage.ReloadAsync();
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_forceClose)
            return;

        e.Cancel = true;
        if (IsDocked && !_dockPinned)
        {
            _ = CollapseToDockAsync();
            return;
        }

        if (_minimizeToTray)
        {
            Hide();
            return;
        }

        App.Current.RequestExit();
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _dockMouseLeaveTimer.Stop();
        _dockAutoHide?.Dispose();
        _dockAutoHide = null;
        _lockTimer.Stop();
        _yubiOtpIdleTimer.Stop();
        CloseDockHandle();
    }

    private void Window_Activated(object sender, EventArgs e) => UpdateDockAutoHideWatch();

    private void Window_Deactivated(object sender, EventArgs e)
    {
        UpdateDockAutoHideWatch();
        // Immediate path — do not wait for foreground HWND polling.
        RequestDockAutoHide("Deactivated");
    }

    private void UpdateDockAutoHideWatch()
    {
        if (_dockAutoHide is null)
            return;

        if (IsDocked && !_isCollapsed && !_forceClose && IsVisible)
            _dockAutoHide.Start();
        else
            _dockAutoHide.Stop();
    }

    private IntPtr DockWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int wmActivateApp = 0x001C;
        const int wmActivate = 0x0006;
        const int waInactive = 0;

        // Entire app lost focus to another process.
        if (msg == wmActivateApp && wParam == IntPtr.Zero)
            RequestDockAutoHide("WM_ACTIVATEAPP");

        // This window became inactive (WA_INACTIVE in low word).
        if (msg == wmActivate && (wParam.ToInt32() & 0xFFFF) == waInactive)
            RequestDockAutoHide("WM_ACTIVATE");

        return IntPtr.Zero;
    }

    private void RequestDockAutoHide(string reason)
    {
        if (!IsDocked || _isCollapsed || _dockAnimating || _forceClose || _dockPinned || _suppressDockAutoHide > 0 || !IsVisible)
        {
            LoggingConfiguration.LogInfo(
                $"Dock auto-hide skipped ({reason}): docked={IsDocked} collapsed={_isCollapsed} animating={_dockAnimating} forceClose={_forceClose} pinned={_dockPinned} suppress={_suppressDockAutoHide} visible={IsVisible} pos={_dockPosition}",
                nameof(MainWindow));
            return;
        }

        LoggingConfiguration.LogInfo($"Dock auto-hide collapsing via {reason}.", nameof(MainWindow));
        _ = CollapseToDockAsync();
    }

    private void Window_PreviewActivity(object sender, InputEventArgs e)
    {
        if (_locked)
            return;

        _lastActivityUtc = DateTime.UtcNow;
        if (_codesMasked)
            SetCodesMasked(false);
    }

    private void CollapseDock_Click(object sender, RoutedEventArgs e)
    {
        if (IsDocked && !_dockPinned)
            _ = CollapseToDockAsync();
    }

    private void PinDock_Changed(object sender, RoutedEventArgs e)
    {
        _dockPinned = PinDockButton.IsChecked == true;
        UpdatePinDockUi();
        PersistDockPinned();
        UpdateDockAutoHideWatch();
    }

    /// <summary>Called from Settings when the user picks Off / Left / Right.</summary>
    public async Task SetDockPositionFromSettingsAsync(string position)
    {
        _dockPosition = NormalizeDockPosition(position);
        _isCollapsed = false;
        UpdateCollapseButton();
        ApplyTopmost();

        if (!IsDocked)
        {
            CloseDockHandle();
            if (!IsVisible)
                Show();
            Topmost = false;
            UpdateDockAutoHideWatch();
            await PersistDockStateAsync();
            return;
        }

        if (!IsVisible)
            Show();
        SnapExpandedToDockEdge();
        CloseDockHandle();
        UpdateDockAutoHideWatch();
        await PersistDockStateAsync();
    }

    private void LoadUiDesktopPrefs()
    {
        var prefs = _uiPrefs.Load();
        _minimizeToTray = prefs.MinimizeToTray;
        _alwaysOnTopWhenDocked = prefs.AlwaysOnTopWhenDocked;
        _dockPinned = prefs.DockPinned;
        PinDockButton.IsChecked = _dockPinned;
        UpdatePinDockUi();
        ApplyDockHandleAppearance(prefs);
    }

    private void PersistDockPinned()
    {
        var prefs = _uiPrefs.Load();
        prefs.DockPinned = _dockPinned;
        _uiPrefs.Save(prefs);
    }

    private void UpdatePinDockUi()
    {
        PinDockIcon.Text = _dockPinned ? "\uE77A" : "\uE718";
        PinDockButton.ToolTip = _dockPinned
            ? "Unpin (allow auto-collapse)"
            : "Keep open (do not auto-collapse)";
        CollapseDockButton.IsEnabled = !_dockPinned;
        CollapseDockButton.Opacity = _dockPinned ? 0.45 : 1;
    }

    private void ApplyDockHandleAppearance(CodesUiPreferencesState? prefs = null)
    {
        prefs ??= _uiPrefs.Load();
        _dockHandle?.ApplyAppearance(prefs.DockHandleColor, prefs.DockHandleOpacity);
    }

    private void ApplyTopmost()
    {
        Topmost = IsDocked && !_isCollapsed && _alwaysOnTopWhenDocked;
    }

    private void UpdateCollapseButton()
    {
        var docked = IsDocked ? Visibility.Visible : Visibility.Collapsed;
        CollapseDockButton.Visibility = docked;
        PinDockButton.Visibility = docked;
        UpdatePinDockUi();
    }

    private Rect GetDockWorkArea()
    {
        var probeY = !double.IsNaN(Top) ? Top + 40 : _handleOffsetY;
        var probeX = string.Equals(_dockPosition, "Right", StringComparison.OrdinalIgnoreCase)
            ? SystemParameters.WorkArea.Right - 1
            : SystemParameters.WorkArea.Left + 1;
        var work = ScreenWorkArea.FromPoint(probeX, probeY);
        return work.Width > 0 && work.Height > 0 ? work : ScreenWorkArea.Primary();
    }

    private (double Width, double Height) GetWindowSize()
    {
        var width = ActualWidth > 1 ? ActualWidth : Width;
        var height = ActualHeight > 1 ? ActualHeight : Height;
        if (double.IsNaN(width) || width < 1)
            width = 420;
        if (double.IsNaN(height) || height < 1)
            height = 900;
        return (width, height);
    }

    private void SnapExpandedToDockEdge()
    {
        var work = GetDockWorkArea();
        var (width, height) = GetWindowSize();
        Left = string.Equals(_dockPosition, "Right", StringComparison.OrdinalIgnoreCase)
            ? work.Right - width
            : work.Left;
        var maxTop = Math.Max(work.Top, work.Bottom - height);
        var top = double.IsNaN(Top) ? work.Top + 40 : Top;
        Top = Math.Clamp(top, work.Top, maxTop);
        ApplyTopmost();
    }

    private async Task ExpandFromDockAsync()
    {
        if (!IsDocked || _dockAnimating)
            return;

        _dockAnimating = true;
        try
        {
            var work = GetDockWorkArea();
            var (width, height) = GetWindowSize();
            var targetLeft = string.Equals(_dockPosition, "Right", StringComparison.OrdinalIgnoreCase)
                ? work.Right - width
                : work.Left;
            var startLeft = string.Equals(_dockPosition, "Right", StringComparison.OrdinalIgnoreCase)
                ? work.Right
                : work.Left - width;
            var maxTop = Math.Max(work.Top, work.Bottom - height);
            var top = Math.Clamp(_handleOffsetY - 40, work.Top, maxTop);

            Opacity = 0;
            Left = startLeft;
            Top = top;
            Show();
            CloseDockHandle();

            await AnimateWindowAsync(startLeft, targetLeft, fromOpacity: 0, toOpacity: 1);

            _isCollapsed = false;
            ApplyTopmost();
            Activate();
            UpdateDockAutoHideWatch();
            if (_locked)
                FocusLockInput();
            await PersistDockStateAsync();
        }
        finally
        {
            _dockAnimating = false;
        }
    }

    private async Task CollapseToDockAsync(bool persist = true, bool animate = true)
    {
        if (!IsDocked || _dockAnimating)
            return;

        _dockAnimating = true;
        try
        {
            var work = GetDockWorkArea();
            var (width, _) = GetWindowSize();
            var startLeft = double.IsNaN(Left) ? work.Left : Left;
            var endLeft = string.Equals(_dockPosition, "Right", StringComparison.OrdinalIgnoreCase)
                ? work.Right
                : work.Left - width;

            if (animate && IsVisible)
                await AnimateWindowAsync(startLeft, endLeft, fromOpacity: Opacity, toOpacity: 0);

            Hide();
            Opacity = 1;
            _isCollapsed = true;
            Topmost = false;
            EnsureDockHandle();
            _dockHandle!.ShowAt(_dockPosition, _handleOffsetY);

            if (persist)
                await PersistDockStateAsync();
        }
        finally
        {
            _dockAnimating = false;
            UpdateDockAutoHideWatch();
        }
    }

    private Task AnimateWindowAsync(double fromLeft, double toLeft, double fromOpacity, double toOpacity)
    {
        var tcs = new TaskCompletionSource();
        var duration = TimeSpan.FromMilliseconds(180);
        var leftAnim = new DoubleAnimation(fromLeft, toLeft, duration)
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        var opacityAnim = new DoubleAnimation(fromOpacity, toOpacity, duration)
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };

        leftAnim.Completed += (_, _) =>
        {
            BeginAnimation(LeftProperty, null);
            BeginAnimation(OpacityProperty, null);
            Left = toLeft;
            Opacity = toOpacity;
            tcs.TrySetResult();
        };
        BeginAnimation(LeftProperty, leftAnim);
        BeginAnimation(OpacityProperty, opacityAnim);
        return tcs.Task;
    }

    private void EnsureDockHandle()
    {
        if (_dockHandle is not null)
            return;

        _dockHandle = new DockHandleWindow();
        ApplyDockHandleAppearance();
        _dockHandle.ActivatedByUser += (_, _) => _ = ExpandFromDockAsync();
        _dockHandle.PlacementChanged += async (_, placement) =>
        {
            _dockPosition = NormalizeDockPosition(placement.Side);
            _handleOffsetY = placement.OffsetY;
            await PersistDockStateAsync();
        };
    }

    private void CloseDockHandle()
    {
        if (_dockHandle is null)
            return;

        try
        {
            _dockHandle.Close();
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Failed to close dock handle.", nameof(MainWindow));
        }

        _dockHandle = null;
    }

    private async Task PersistDockStateAsync()
    {
        try
        {
            await _settings.SetWindowDockStateAsync(_dockPosition, _isCollapsed && IsDocked, _handleOffsetY);
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Failed to persist dock state.", nameof(MainWindow));
        }
    }

    private static string NormalizeDockPosition(string? position)
    {
        if (string.Equals(position, "Left", StringComparison.OrdinalIgnoreCase))
            return "Left";
        if (string.Equals(position, "Right", StringComparison.OrdinalIgnoreCase))
            return "Right";
        return "None";
    }

    private void RefreshUnlockCapabilities()
    {
        var credentials = _lockStore.Load();
        _hasUnlockMethod = credentials.HasUnlockMethod;
        _yubiEnabled = credentials.YubiKeyEnabled;
        _helloEnabled = credentials.HelloUnlockEnabled;
        if (!_hasUnlockMethod && _timeoutSeconds > 0)
        {
            _timeoutSeconds = 0;
            _ = _settings.SetAutoLockTimeoutAsync(0);
        }
    }

    private void ConfigureLockOverlay()
    {
        var credentials = _lockStore.Load();
        _yubiEnabled = credentials.YubiKeyEnabled;
        _helloEnabled = credentials.HelloUnlockEnabled;
        PinUnlockPanel.Visibility = credentials.PinEnabled ? Visibility.Visible : Visibility.Collapsed;
        YubiUnlockPanel.Visibility = credentials.YubiKeyEnabled ? Visibility.Visible : Visibility.Collapsed;
        HelloUnlockPanel.Visibility = credentials.HelloUnlockEnabled ? Visibility.Visible : Visibility.Collapsed;
        UnlockErrorText.Text = string.Empty;

        UnlockHintText.Text = BuildUnlockHint(credentials);
    }

    private static string BuildUnlockHint(LockCredentials credentials)
    {
        var parts = new List<string>();
        if (credentials.PinEnabled)
            parts.Add("PIN");
        if (credentials.YubiKeyEnabled)
            parts.Add("YubiKey");
        if (credentials.HelloUnlockEnabled)
            parts.Add("Windows Hello");

        return parts.Count switch
        {
            0 => "Codes are hidden until you unlock.",
            1 => parts[0] switch
            {
                "PIN" => "Enter your PIN to unlock.",
                "YubiKey" => "Touch your YubiKey to unlock.",
                _ => "Use Windows Hello to unlock."
            },
            _ => "Unlock with " + string.Join(", ", parts.Take(parts.Count - 1)) + ", or " + parts[^1] + "."
        };
    }

    private void CheckAutoLock()
    {
        if (_locked || _timeoutSeconds <= 0 || !_hasUnlockMethod)
            return;

        var idle = DateTime.UtcNow - _lastActivityUtc;
        if (idle >= TimeSpan.FromSeconds(_timeoutSeconds))
        {
            SetCodesMasked(false);
            Lock();
            return;
        }

        // Soft-mask codes at half the auto-lock timeout (DEK stays in memory).
        var maskAfterSeconds = Math.Max(1, _timeoutSeconds / 2);
        if (!_codesMasked && idle >= TimeSpan.FromSeconds(maskAfterSeconds))
            SetCodesMasked(true);
    }

    private void SetCodesMasked(bool masked)
    {
        if (_codesMasked == masked)
            return;

        _codesMasked = masked;
        CodesPage.SetCodesMasked(masked);
        BackupPage.SetCodesMasked(masked);
        LoggingConfiguration.LogInfo(masked ? "Idle code mask engaged." : "Idle code mask cleared.", nameof(MainWindow));
    }

    private void Lock()
    {
        if (!_hasUnlockMethod)
            return;

        _locked = true;
        _codesMasked = false;
        CodesPage.SetCodesMasked(false);
        BackupPage.SetCodesMasked(false);
        VaultKeyService.Instance.Clear();
        PageHost.Visibility = Visibility.Collapsed;
        LockOverlay.Visibility = Visibility.Visible;
        UnlockPinBox.Clear();
        YubiOtpCaptureBox.Clear();
        _pinPanelForcedForYubiEcho = false;
        UnlockErrorText.Text = string.Empty;
        ConfigureLockOverlay();
        Activate();
        FocusLockInput();
        LoggingConfiguration.LogInfo("Auto-lock engaged.", nameof(MainWindow));
    }

    private void FocusLockInput()
    {
        _lockFocusRetries = 0;
        // CodesView may still finish an in-flight ReloadAsync after Lock() and steal
        // focus — retry until the target actually has keyboard focus.
        Dispatcher.BeginInvoke(ApplyLockInputFocus, DispatcherPriority.Loaded);
        Dispatcher.BeginInvoke(ApplyLockInputFocus, DispatcherPriority.ApplicationIdle);
    }

    private void ApplyLockInputFocus()
    {
        if (!_locked)
            return;

        // Don't steal activation back while docked — that blocks auto-hide on blur.
        if (IsDocked && !IsActive)
            return;

        UIElement? target = null;
        if (PinUnlockPanel.Visibility == Visibility.Visible)
            target = UnlockPinBox;
        else if (_yubiEnabled)
            target = YubiOtpCaptureBox;
        else if (_helloEnabled)
            target = HelloUnlockPanel;

        if (target is null)
            return;

        Activate();
        FocusManager.SetFocusedElement(LockOverlay, target);
        target.Focus();
        Keyboard.Focus(target);

        if (target.IsKeyboardFocusWithin)
            return;

        if (_lockFocusRetries++ < 25)
            Dispatcher.BeginInvoke(ApplyLockInputFocus, DispatcherPriority.ApplicationIdle);
    }

    private void UnlockPinBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Keep caret on PIN while locked (allow focus on the Unlock button).
        if (!_locked)
            return;

        // Allow focus on Unlock / Hello buttons; otherwise keep caret on PIN when PIN is enabled.
        if (e.NewFocus is System.Windows.Controls.Button)
            return;

        if (PinUnlockPanel.Visibility == Visibility.Visible)
        {
            _lockFocusRetries = 0;
            Dispatcher.BeginInvoke(ApplyLockInputFocus, DispatcherPriority.Input);
        }
        else if (_yubiEnabled)
        {
            _lockFocusRetries = 0;
            Dispatcher.BeginInvoke(ApplyLockInputFocus, DispatcherPriority.Input);
        }
    }

    private void UnlockPinBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            UnlockPin_Click(sender, e);
        }
    }

    private void UnlockPin_Click(object sender, RoutedEventArgs e)
    {
        // Yubi OTP is echoed into the PIN box — never treat that capture as a PIN.
        if (_yubiEnabled && YubiOtpCaptureBox.Text.Length > 0)
        {
            if (YubiKeyUnlockService.IsOtp(YubiOtpCaptureBox.Text))
                TryConsumeYubiOtp();
            return;
        }

        if (_lockStore.TryUnlockWithPin(UnlockPinBox.Password))
        {
            _ = CompleteUnlockAsync();
            return;
        }

        UnlockErrorText.Text = "Incorrect PIN.";
        UnlockPinBox.Clear();
        _lockFocusRetries = 0;
        Keyboard.Focus(UnlockPinBox);
    }

    private async void UnlockHello_Click(object sender, RoutedEventArgs e)
    {
        if (!_locked || !_helloEnabled)
            return;

        UnlockErrorText.Text = string.Empty;
        BeginDockAutoHideSuppress();
        try
        {
            var consented = await _hello.RequestConsentAsync("Unlock Authenticator");
            if (!consented)
            {
                UnlockErrorText.Text = "Windows Hello was cancelled or failed.";
                FocusLockInput();
                return;
            }

            if (!_lockStore.TryUnlockWithHello())
            {
                UnlockErrorText.Text = "Could not unlock the vault with Windows Hello.";
                FocusLockInput();
                return;
            }

            await CompleteUnlockAsync();
        }
        finally
        {
            EndDockAutoHideSuppress();
        }
    }

    /// <summary>
    /// PIN keeps keyboard focus for typing; Yubi OTP (modhex wedge) is diverted here
    /// so both unlock methods work without stealing the caret.
    /// </summary>
    private void LockOverlay_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (!_locked || !_yubiEnabled || string.IsNullOrEmpty(e.Text))
            return;

        if (!YubiKeyUnlockService.IsModHex(e.Text))
        {
            if (!YubiKeyUnlockService.IsOtp(YubiOtpCaptureBox.Text))
            {
                YubiOtpCaptureBox.Clear();
                ClearYubiPinEcho();
            }
            return;
        }

        e.Handled = true;
        YubiOtpCaptureBox.Text += e.Text.ToLowerInvariant();
        SyncYubiPinEcho();
    }

    /// <summary>
    /// Mirror OTP keystrokes into the PIN PasswordBox as masked bullets so a YubiKey
    /// touch is visibly doing something while the real capture stays in YubiOtpCaptureBox.
    /// </summary>
    private void SyncYubiPinEcho()
    {
        var otp = YubiOtpCaptureBox.Text;
        if (otp.Length == 0)
        {
            ClearYubiPinEcho();
            return;
        }

        if (PinUnlockPanel.Visibility != Visibility.Visible)
        {
            PinUnlockPanel.Visibility = Visibility.Visible;
            _pinPanelForcedForYubiEcho = true;
        }

        UnlockPinBox.Password = otp;
    }

    private void ClearYubiPinEcho()
    {
        if (_pinPanelForcedForYubiEcho)
        {
            _pinPanelForcedForYubiEcho = false;
            var pinEnabled = _lockStore.Load().PinEnabled;
            PinUnlockPanel.Visibility = pinEnabled ? Visibility.Visible : Visibility.Collapsed;
        }

        UnlockPinBox.Clear();
    }

    private void LockOverlay_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_locked || !_yubiEnabled || e.Key != Key.Enter)
            return;

        if (!YubiKeyUnlockService.IsOtp(YubiOtpCaptureBox.Text))
            return;

        e.Handled = true;
        TryConsumeYubiOtp();
    }

    private void YubiOtpCaptureBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!_locked || !_yubiEnabled)
            return;

        SyncYubiPinEcho();
        _yubiOtpIdleTimer.Stop();
        if (YubiOtpCaptureBox.Text.Length >= 12)
            _yubiOtpIdleTimer.Start();
    }

    private void YubiOtpCaptureBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_locked || !_yubiEnabled || e.Key != Key.Enter)
            return;

        e.Handled = true;
        TryConsumeYubiOtp();
    }

    private void TryConsumeYubiOtp()
    {
        _yubiOtpIdleTimer.Stop();
        if (!_locked || !_yubiEnabled)
            return;

        var text = YubiOtpCaptureBox.Text.Trim();
        if (!YubiKeyUnlockService.IsOtp(text))
            return;

        var registration = _lockStore.GetYubiKeyRegistration();
        if (registration is null)
        {
            UnlockErrorText.Text = "No YubiKey is registered.";
            YubiOtpCaptureBox.Clear();
            ClearYubiPinEcho();
            return;
        }

        try
        {
            var lastOtp = _lockStore.Load().YubiKeyLastOtp;
            if (_yubi.TryValidateOtp(registration, text, lastOtp, out var normalized))
            {
                _lockStore.RememberYubiKeyOtp(normalized);
                if (!_lockStore.TryUnlockWithYubi())
                {
                    UnlockErrorText.Text = "Could not unlock the vault with YubiKey.";
                    YubiOtpCaptureBox.Clear();
                    ClearYubiPinEcho();
                    return;
                }

                _ = CompleteUnlockAsync();
                return;
            }

            UnlockErrorText.Text = "YubiKey unlock failed. Wrong key or OTP.";
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "YubiKey unlock failed.", nameof(MainWindow));
            UnlockErrorText.Text = ex.Message;
        }

        YubiOtpCaptureBox.Clear();
        ClearYubiPinEcho();
        FocusLockInput();
    }

    private async Task CompleteUnlockAsync()
    {
        try
        {
            var migrated = await new SecretMigrationService().EncryptLegacySecretsAsync();
            if (migrated.Accounts > 0 || migrated.BackupCodes > 0)
            {
                LoggingConfiguration.LogInfo(
                    $"Post-unlock migration encrypted accounts={migrated.Accounts}, backupCodes={migrated.BackupCodes}.",
                    nameof(MainWindow));
            }
        }
        catch (Exception ex)
        {
            VaultKeyService.Instance.Clear();
            LoggingConfiguration.LogException(ex, "Secret migration failed after unlock.", nameof(MainWindow));
            UnlockErrorText.Text = "Unlocked, but secret migration failed. See logs.";
            return;
        }

        _locked = false;
        _codesMasked = false;
        _lockFocusRetries = 0;
        _yubiOtpIdleTimer.Stop();
        _lastActivityUtc = DateTime.UtcNow;
        UnlockPinBox.Clear();
        YubiOtpCaptureBox.Clear();
        _pinPanelForcedForYubiEcho = false;
        UnlockErrorText.Text = string.Empty;
        LockOverlay.Visibility = Visibility.Collapsed;
        PageHost.Visibility = Visibility.Visible;
        // Defer page show/reload so lock focus teardown cannot race CodesView ReloadAsync.
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        ShowPage(CodesPage);
        CodesPage.SetCodesMasked(false);
        BackupPage.SetCodesMasked(false);
        await CodesPage.ReloadAsync();
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
