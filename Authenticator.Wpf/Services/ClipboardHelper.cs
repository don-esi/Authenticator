using System.Windows;
using System.Windows.Threading;

namespace Authenticator.Services;

public static class ClipboardHelper
{
    private static DispatcherTimer? _clearTimer;
    private static string? _lastSet;

    public static void Copy(string? text, int clearAfterSeconds = 30)
    {
        if (string.IsNullOrEmpty(text))
            return;

        Clipboard.SetText(text);
        _lastSet = text;
        _clearTimer?.Stop();
        if (clearAfterSeconds <= 0)
            return;

        _clearTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(clearAfterSeconds) };
        _clearTimer.Tick += (_, _) =>
        {
            _clearTimer.Stop();
            try
            {
                if (Clipboard.ContainsText() && Clipboard.GetText() == _lastSet)
                    Clipboard.Clear();
            }
            catch
            {
                // ignore clipboard races
            }
        };
        _clearTimer.Start();
    }
}
