using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Authenticator.Logging;

namespace Authenticator.Services;

/// <summary>
/// Registers Ctrl+Shift+A via Win32 RegisterHotKey on a WPF window HWND.
/// </summary>
public sealed class GlobalHotkeyService : IDisposable
{
    public const int HotkeyId = 0xA11E;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint WmHotkey = 0x0312;
    private const uint VkA = 0x41;

    private HwndSource? _source;
    private IntPtr _hwnd;
    private bool _registered;
    private bool _disposed;

    public event EventHandler? HotkeyPressed;

    public bool IsRegistered => _registered;

    public void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        Detach();

        var helper = new WindowInteropHelper(window);
        helper.EnsureHandle();
        _hwnd = helper.Handle;
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);
    }

    public bool TryRegister()
    {
        if (_hwnd == IntPtr.Zero)
            return false;

        TryUnregister();
        _registered = RegisterHotKey(_hwnd, HotkeyId, ModControl | ModShift, VkA);
        if (_registered)
            LoggingConfiguration.LogInfo("Global hotkey Ctrl+Shift+A registered.", nameof(GlobalHotkeyService));
        else
            LoggingConfiguration.LogInfo("Global hotkey Ctrl+Shift+A registration failed (may be in use).", nameof(GlobalHotkeyService));
        return _registered;
    }

    public void TryUnregister()
    {
        if (!_registered || _hwnd == IntPtr.Zero)
            return;

        UnregisterHotKey(_hwnd, HotkeyId);
        _registered = false;
        LoggingConfiguration.LogDebug("Global hotkey unregistered.", nameof(GlobalHotkeyService));
    }

    public void Detach()
    {
        TryUnregister();
        if (_source is not null)
        {
            _source.RemoveHook(WndProc);
            _source = null;
        }

        _hwnd = IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Detach();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            HotkeyPressed?.Invoke(this, EventArgs.Empty);
        }

        return IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
