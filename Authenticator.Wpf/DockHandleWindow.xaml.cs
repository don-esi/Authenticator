using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Authenticator.Services;

namespace Authenticator;

public partial class DockHandleWindow : Window
{
    private const double FlipThresholdPx = 48;
    private const double ClickSlopPx = 6;

    private string _side = "Left";
    private double _offsetY = 120;
    private Point _dragStartScreen;
    private double _dragStartTop;
    private string _dragStartSide = "Left";
    private bool _dragging;
    private bool _moved;
    private bool _suppressHoverExpand;
    private readonly DispatcherTimer _hoverExpandTimer;
    private readonly DispatcherTimer _hoverSuppressTimer;
    private SolidColorBrush _fillBrush = new(Color.FromRgb(0xE5, 0x39, 0x35));

    public event EventHandler? ActivatedByUser;
    public event EventHandler<(string Side, int OffsetY)>? PlacementChanged;

    public DockHandleWindow()
    {
        InitializeComponent();
        _hoverExpandTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        _hoverExpandTimer.Tick += (_, _) =>
        {
            _hoverExpandTimer.Stop();
            if (_dragging || _suppressHoverExpand || !IsMouseOver)
                return;
            ActivatedByUser?.Invoke(this, EventArgs.Empty);
        };
        _hoverSuppressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _hoverSuppressTimer.Tick += (_, _) =>
        {
            _hoverSuppressTimer.Stop();
            _suppressHoverExpand = false;
        };
        HandleChrome.Background = _fillBrush;
    }

    public string Side => _side;
    public double OffsetY => _offsetY;

    public void ApplyAppearance(string? hexColor, double opacity)
    {
        if (!TryParseHexColor(hexColor, out var color))
            color = Color.FromRgb(0xE5, 0x39, 0x35);

        var alpha = (byte)Math.Clamp((int)Math.Round(Math.Clamp(opacity, 0.2, 1.0) * 255), 51, 255);
        color.A = alpha;
        _fillBrush = new SolidColorBrush(color);
        HandleChrome.Background = _fillBrush;
    }

    public void ShowAt(string side, double offsetY)
    {
        _side = NormalizeSide(side);
        _offsetY = offsetY;
        ApplyPlacement();
        UpdateHandleVisual();
        SuppressHoverExpandBriefly();
        if (!IsVisible)
            Show();
        else
            Activate();
    }

    private void SuppressHoverExpandBriefly()
    {
        _suppressHoverExpand = true;
        _hoverExpandTimer.Stop();
        _hoverSuppressTimer.Stop();
        _hoverSuppressTimer.Start();
    }

    private void ApplyPlacement()
    {
        // Prefer the primary work area in DIPs; FromPoint also converts Win32→DIP.
        var probeX = _side == "Right"
            ? SystemParameters.WorkArea.Right - 1
            : SystemParameters.WorkArea.Left + 1;
        var work = ScreenWorkArea.FromPoint(probeX, _offsetY + Height / 2);
        if (work.Width <= 0 || work.Height <= 0)
            work = ScreenWorkArea.Primary();

        var handleHeight = Height > 0 ? Height : 56;
        var handleWidth = Width > 0 ? Width : 28;
        var maxTop = Math.Max(work.Top, work.Bottom - handleHeight);
        var top = Math.Clamp(_offsetY, work.Top, maxTop);
        _offsetY = top;

        Left = _side == "Left" ? work.Left : work.Right - handleWidth;
        Top = top;
    }

    private void UpdateHandleVisual()
    {
        // Left edge: flat on left, round toward desktop. Right edge: mirrored.
        HandleChrome.CornerRadius = _side == "Left"
            ? new CornerRadius(0, 28, 28, 0)
            : new CornerRadius(28, 0, 0, 28);
    }

    private void Handle_MouseEnter(object sender, MouseEventArgs e)
    {
        if (_dragging || _suppressHoverExpand)
            return;
        _hoverExpandTimer.Stop();
        _hoverExpandTimer.Start();
    }

    private void Handle_MouseLeave(object sender, MouseEventArgs e)
    {
        _hoverExpandTimer.Stop();
    }

    private void Handle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _hoverExpandTimer.Stop();
        _dragging = true;
        _moved = false;
        _dragStartScreen = PointToScreen(e.GetPosition(this));
        _dragStartTop = Top;
        _dragStartSide = _side;
        ((UIElement)sender).CaptureMouse();
        e.Handled = true;
    }

    private void Handle_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || e.LeftButton != MouseButtonState.Pressed)
            return;

        var screen = PointToScreen(e.GetPosition(this));
        // PointToScreen returns device pixels — convert to DIPs for work-area math.
        var (scaleX, scaleY) = GetDipScale();
        var screenDip = new Point(screen.X / scaleX, screen.Y / scaleY);
        var startDip = new Point(_dragStartScreen.X / scaleX, _dragStartScreen.Y / scaleY);
        var dx = screenDip.X - startDip.X;
        var dy = screenDip.Y - startDip.Y;
        if (Math.Abs(dx) > ClickSlopPx || Math.Abs(dy) > ClickSlopPx)
            _moved = true;

        var work = ScreenWorkArea.FromPoint(screenDip.X, screenDip.Y);
        if (work.Width <= 0 || work.Height <= 0)
            work = ScreenWorkArea.Primary();

        var handleHeight = Height > 0 ? Height : 56;
        var handleWidth = Width > 0 ? Width : 28;
        var maxTop = Math.Max(work.Top, work.Bottom - handleHeight);
        Top = Math.Clamp(_dragStartTop + dy, work.Top, maxTop);
        _offsetY = Top;

        if (_dragStartSide == "Left" && dx > FlipThresholdPx)
        {
            _side = "Right";
            UpdateHandleVisual();
        }
        else if (_dragStartSide == "Right" && dx < -FlipThresholdPx)
        {
            _side = "Left";
            UpdateHandleVisual();
        }

        Left = _side == "Left" ? work.Left : work.Right - handleWidth;
        e.Handled = true;
    }

    private void Handle_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
            return;

        _dragging = false;
        ((UIElement)sender).ReleaseMouseCapture();

        if (_moved)
            PlacementChanged?.Invoke(this, (_side, (int)Math.Round(_offsetY)));
        else
            ActivatedByUser?.Invoke(this, EventArgs.Empty);

        e.Handled = true;
    }

    private (double ScaleX, double ScaleY) GetDipScale()
    {
        if (PresentationSource.FromVisual(this) is { CompositionTarget: { } target })
        {
            var m = target.TransformToDevice;
            return (m.M11, m.M22);
        }

        return (1.0, 1.0);
    }

    private static string NormalizeSide(string side)
        => string.Equals(side, "Right", StringComparison.OrdinalIgnoreCase) ? "Right" : "Left";

    private static bool TryParseHexColor(string? hex, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(hex))
            return false;

        hex = hex.Trim();
        if (hex.StartsWith('#'))
            hex = hex[1..];
        if (hex.Length == 8)
            hex = hex[2..];
        if (hex.Length != 6)
            return false;

        try
        {
            var r = Convert.ToByte(hex[..2], 16);
            var g = Convert.ToByte(hex[2..4], 16);
            var b = Convert.ToByte(hex[4..6], 16);
            color = Color.FromRgb(r, g, b);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
