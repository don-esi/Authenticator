using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Authenticator.Logging;

namespace Authenticator.Services;

/// <summary>
/// Collapses a docked main window when focus leaves it.
/// Thread-pool timer (not DispatcherTimer) so polling continues while inactive.
/// Does not cancel on WPF Activated — Topmost often re-fires Activated spuriously.
/// </summary>
public sealed class DockAutoHideService : IDisposable
{
    private readonly Window _window;
    private readonly Func<bool> _shouldWatch;
    private readonly Func<bool> _canCollapse;
    private readonly Func<Task> _collapseAsync;
    private readonly Dispatcher _dispatcher;
    private Timer? _timer;
    private int _collapseGate;
    private int _tickQueued;
    private bool _disposed;

    public DockAutoHideService(
        Window window,
        Func<bool> shouldWatch,
        Func<bool> canCollapse,
        Func<Task> collapseAsync)
    {
        _window = window;
        _shouldWatch = shouldWatch;
        _canCollapse = canCollapse;
        _collapseAsync = collapseAsync;
        _dispatcher = window.Dispatcher;
    }

    public void Start()
    {
        if (_disposed)
            return;

        // 150ms flooded the UI dispatcher with Background invokes while docked.
        if (_timer is null)
            _timer = new Timer(static state => ((DockAutoHideService)state!).OnTick(), this, 400, 400);
        else
            _timer.Change(400, 400);
    }

    public void Stop()
    {
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        Interlocked.Exchange(ref _tickQueued, 0);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _timer?.Dispose();
        _timer = null;
    }

    private void OnTick()
    {
        if (_disposed)
            return;

        // Coalesce: skip if a UI tick is already pending.
        if (Interlocked.CompareExchange(ref _tickQueued, 1, 0) != 0)
            return;

        try
        {
            _ = _dispatcher.BeginInvoke(() =>
            {
                try
                {
                    OnTickCore();
                }
                finally
                {
                    Interlocked.Exchange(ref _tickQueued, 0);
                }
            }, DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _tickQueued, 0);
            LoggingConfiguration.LogException(ex, "Dock auto-hide tick failed.", nameof(DockAutoHideService));
        }
    }

    private void OnTickCore()
    {
        if (_disposed || !_shouldWatch())
            return;

        var mainHwnd = new WindowInteropHelper(_window).Handle;
        if (mainHwnd == IntPtr.Zero)
            return;

        if (IsFocusWithWindow(_window))
            return;

        if (!_canCollapse())
            return;

        if (Interlocked.CompareExchange(ref _collapseGate, 1, 0) != 0)
            return;

        LoggingConfiguration.LogInfo("Dock auto-hide: focus left the main window.", nameof(DockAutoHideService));
        _ = CollapseGuardedAsync();
    }

    /// <summary>
    /// True when the foreground HWND is this window, a child, or a Win32-owned popup
    /// (WPF ContextMenu / Popup / owned dialog).
    /// </summary>
    public static bool IsFocusWithWindow(Window window)
    {
        var mainHwnd = new WindowInteropHelper(window).Handle;
        if (mainHwnd == IntPtr.Zero)
            return true; // not ready — don't hide

        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero)
            return true; // ambiguous — don't hide

        if (foreground == mainHwnd)
            return true;

        // Focus on a child of our window.
        if (IsChild(mainHwnd, foreground))
            return true;

        // MessageBox / ContextMenu popup / common dialogs owned by us.
        if (GetWindow(foreground, GwOwner) == mainHwnd)
            return true;

        // Same process but not our dialog — e.g. NotifyIcon message window counts as "not us"
        // for hide purposes (we want to collapse). Only stay if it's a visible WPF owned dialog
        // already handled above.
        return false;
    }

    private async Task CollapseGuardedAsync()
    {
        try
        {
            await _collapseAsync();
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Dock auto-hide collapse failed.", nameof(DockAutoHideService));
        }
        finally
        {
            Interlocked.Exchange(ref _collapseGate, 0);
        }
    }

    private const uint GwOwner = 4;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    private static extern bool IsChild(IntPtr hWndParent, IntPtr hWnd);
}
