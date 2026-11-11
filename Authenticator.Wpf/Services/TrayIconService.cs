using Authenticator.Logging;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Authenticator.Services;

/// <summary>
/// Notification-area icon with Open / Exit. Uses WinForms NotifyIcon.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private Forms.NotifyIcon? _icon;
    private bool _disposed;

    public event EventHandler? OpenRequested;
    public event EventHandler? ExitRequested;

    public void Initialize()
    {
        if (_icon is not null)
            return;

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty));

        _icon = new Forms.NotifyIcon
        {
            Text = "Authenticator",
            Visible = true,
            ContextMenuStrip = menu,
            Icon = CreateIcon()
        };
        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
        LoggingConfiguration.LogInfo("Tray icon initialized.", nameof(TrayIconService));
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_icon is not null)
        {
            _icon.Visible = false;
            _icon.Dispose();
            _icon = null;
        }
    }

    private static Drawing.Icon CreateIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exe))
            {
                var extracted = Drawing.Icon.ExtractAssociatedIcon(exe);
                if (extracted is not null)
                    return extracted;
            }
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Failed to extract process icon for tray.", nameof(TrayIconService));
        }

        var bitmap = new Drawing.Bitmap(16, 16);
        using (var g = Drawing.Graphics.FromImage(bitmap))
        {
            g.Clear(Drawing.Color.Transparent);
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var brush = new Drawing.SolidBrush(Drawing.Color.FromArgb(0, 120, 212));
            g.FillEllipse(brush, 1, 1, 14, 14);
        }

        var handle = bitmap.GetHicon();
        return Drawing.Icon.FromHandle(handle);
    }
}
