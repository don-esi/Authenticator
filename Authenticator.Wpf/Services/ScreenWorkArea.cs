using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

namespace Authenticator.Services;

/// <summary>
/// Work-area bounds in WPF DIPs for the monitor under a point (excludes taskbar).
/// Win32 MONITORINFO is physical pixels — always convert via DPI scale.
/// </summary>
public static class ScreenWorkArea
{
    private const uint MonitorDefaultToNearest = 2;

    public static Rect FromPoint(double xDip, double yDip)
    {
        var (scaleX, scaleY) = GetDpiScale();
        var pt = new POINT
        {
            X = (int)Math.Round(xDip * scaleX),
            Y = (int)Math.Round(yDip * scaleY)
        };
        var monitor = MonitorFromPoint(pt, MonitorDefaultToNearest);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
        {
            return new Rect(
                SystemParameters.WorkArea.Left,
                SystemParameters.WorkArea.Top,
                SystemParameters.WorkArea.Width,
                SystemParameters.WorkArea.Height);
        }

        return new Rect(
            info.rcWork.Left / scaleX,
            info.rcWork.Top / scaleY,
            (info.rcWork.Right - info.rcWork.Left) / scaleX,
            (info.rcWork.Bottom - info.rcWork.Top) / scaleY);
    }

    public static Rect FromWindow(Window window)
    {
        var left = double.IsNaN(window.Left) ? 0 : window.Left;
        var top = double.IsNaN(window.Top) ? 0 : window.Top;
        var width = window.ActualWidth > 0 ? window.ActualWidth : window.Width;
        var height = window.ActualHeight > 0 ? window.ActualHeight : window.Height;
        if (double.IsNaN(width) || width <= 0)
            width = 420;
        if (double.IsNaN(height) || height <= 0)
            height = 900;
        return FromPoint(left + width / 2, top + height / 2);
    }

    /// <summary>Primary work area in DIPs (safe fallback / primary-monitor dock).</summary>
    public static Rect Primary()
        => new(
            SystemParameters.WorkArea.Left,
            SystemParameters.WorkArea.Top,
            SystemParameters.WorkArea.Width,
            SystemParameters.WorkArea.Height);

    private static (double ScaleX, double ScaleY) GetDpiScale()
    {
        try
        {
            if (Application.Current?.MainWindow is { } main
                && PresentationSource.FromVisual(main) is { CompositionTarget: { } target })
            {
                var m = target.TransformToDevice;
                return (m.M11, m.M22);
            }
        }
        catch
        {
            // Fall through to system DPI.
        }

        // System DPI before any window exists (or if PresentationSource unavailable).
        var dpiX = 96.0;
        var dpiY = 96.0;
        var desktop = GetDC(IntPtr.Zero);
        if (desktop != IntPtr.Zero)
        {
            try
            {
                dpiX = GetDeviceCaps(desktop, 88); // LOGPIXELSX
                dpiY = GetDeviceCaps(desktop, 90); // LOGPIXELSY
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, desktop);
            }
        }

        return (dpiX / 96.0, dpiY / 96.0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);

    [DllImport("gdi32.dll")]
    private static extern int GetDeviceCaps(IntPtr hdc, int index);
}
