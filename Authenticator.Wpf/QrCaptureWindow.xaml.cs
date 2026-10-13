using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace Authenticator;

public readonly record struct ScreenRegion(int X, int Y, int Width, int Height);

public partial class QrCaptureWindow : Window
{
    private const uint MonitorDefaultToNearest = 2;

    private Point _start;
    private bool _dragging;
    private ScreenRegion _region;

    public static ScreenRegion? Select()
    {
        var window = new QrCaptureWindow();
        return window.ShowDialog() == true ? window._region : null;
    }

    public QrCaptureWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Activate();
            Keyboard.Focus(this);
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this)?.CompositionTarget is not { } target)
            return;
        if (!TryGetCursorMonitor(out var left, out var top, out var right, out var bottom))
            return;

        var fromDevice = target.TransformFromDevice;
        var topLeft = fromDevice.Transform(new Point(left, top));
        var bottomRight = fromDevice.Transform(new Point(right, bottom));
        Left = topLeft.X;
        Top = topLeft.Y;
        Width = Math.Max(1, bottomRight.X - topLeft.X);
        Height = Math.Max(1, bottomRight.Y - topLeft.Y);
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _start = e.GetPosition(Surface);
        _dragging = true;
        Selection.Visibility = Visibility.Visible;
        Selection.Margin = new Thickness(_start.X, _start.Y, 0, 0);
        Selection.Width = 0;
        Selection.Height = 0;
        CaptureMouse();
    }

    private void Window_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging)
            return;

        var position = e.GetPosition(Surface);
        var x = Math.Min(position.X, _start.X);
        var y = Math.Min(position.Y, _start.Y);
        Selection.Margin = new Thickness(x, y, 0, 0);
        Selection.Width = Math.Abs(position.X - _start.X);
        Selection.Height = Math.Abs(position.Y - _start.Y);
    }

    private void Window_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
            return;

        _dragging = false;
        ReleaseMouseCapture();
        if (Selection.Width < 8 || Selection.Height < 8)
        {
            Selection.Visibility = Visibility.Collapsed;
            return;
        }

        var region = ToDeviceRegion();
        if (region.Width < 2 || region.Height < 2)
        {
            Selection.Visibility = Visibility.Collapsed;
            return;
        }

        _region = region;
        DialogResult = true;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        _dragging = false;
        ReleaseMouseCapture();
        DialogResult = false;
        e.Handled = true;
    }

    private ScreenRegion ToDeviceRegion()
    {
        var toDevice = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        var topLeft = toDevice.Transform(new Point(Selection.Margin.Left, Selection.Margin.Top));
        var bottomRight = toDevice.Transform(new Point(Selection.Margin.Left + Selection.Width, Selection.Margin.Top + Selection.Height));
        var origin = new POINT { X = (int)Math.Round(topLeft.X), Y = (int)Math.Round(topLeft.Y) };
        var end = new POINT { X = (int)Math.Round(bottomRight.X), Y = (int)Math.Round(bottomRight.Y) };
        var hwnd = new WindowInteropHelper(this).Handle;
        ClientToScreen(hwnd, ref origin);
        ClientToScreen(hwnd, ref end);
        return new ScreenRegion(origin.X, origin.Y, Math.Max(1, end.X - origin.X), Math.Max(1, end.Y - origin.Y));
    }

    private static bool TryGetCursorMonitor(out int left, out int top, out int right, out int bottom)
    {
        left = top = right = bottom = 0;
        if (!GetCursorPos(out var cursor))
            return false;

        var monitor = MonitorFromPoint(cursor, MonitorDefaultToNearest);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
            return false;

        left = info.rcMonitor.Left;
        top = info.rcMonitor.Top;
        right = info.rcMonitor.Right;
        bottom = info.rcMonitor.Bottom;
        return right > left && bottom > top;
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
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
}
