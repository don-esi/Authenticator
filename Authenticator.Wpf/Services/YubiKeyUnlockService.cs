using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Authenticator.Logging;

namespace Authenticator.Services;

/// <summary>
/// YubiKey unlock via touch OTP (keyboard wedge).
/// Does not open FIDO2/PCSC and does not reprogram slots.
/// </summary>
public sealed class YubiKeyUnlockService
{
    private static readonly Regex ModHexOtp = new("^[cbdefghijklnrtuv]{32,64}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ModHexText = new("^[cbdefghijklnrtuv]+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public bool IsDevicePresent()
    {
        try
        {
            return Yubico.YubiKey.YubiKeyDevice.FindAll().Any();
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Failed to enumerate YubiKeys.", nameof(YubiKeyUnlockService));
            return false;
        }
    }

    public string? GetDeviceLabel()
    {
        try
        {
            var device = Yubico.YubiKey.YubiKeyDevice.FindAll().FirstOrDefault();
            return device is null ? null : $"YubiKey {device.SerialNumber}";
        }
        catch
        {
            return null;
        }
    }

    public YubiKeyRegistration Enroll(
        Dispatcher dispatcher,
        Window? owner,
        Action<string>? notify = null)
    {
        notify?.Invoke("Touch your YubiKey…");
        var otp = CaptureTouchOtp(dispatcher, owner, "Register YubiKey", "Touch your YubiKey once. Do not type anything.")
            ?? throw new InvalidOperationException("No YubiKey OTP was captured.");

        var publicId = ExtractPublicId(otp);
        int? serial = null;
        try { serial = Yubico.YubiKey.YubiKeyDevice.FindAll().FirstOrDefault()?.SerialNumber; }
        catch { /* optional */ }

        LoggingConfiguration.LogInfo($"YubiKey touch OTP enrolled. PublicId={publicId}.", nameof(YubiKeyUnlockService));
        return new YubiKeyRegistration { PublicId = publicId, SerialNumber = serial };
    }

    public bool TryValidateOtp(YubiKeyRegistration registration, string otp, string? lastOtp, out string normalizedOtp)
    {
        ArgumentNullException.ThrowIfNull(registration);
        normalizedOtp = string.Empty;
        otp = otp.Trim();
        if (!IsOtp(otp))
            return false;

        if (!string.IsNullOrEmpty(lastOtp)
            && string.Equals(otp, lastOtp, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("That OTP was already used. Touch again for a new code.");
        }

        var publicId = ExtractPublicId(otp);
        var ok = string.Equals(publicId, registration.PublicId, StringComparison.OrdinalIgnoreCase);
        normalizedOtp = otp.ToLowerInvariant();
        LoggingConfiguration.LogInfo(
            $"YubiKey touch OTP unlock {(ok ? "succeeded" : "failed")}.",
            nameof(YubiKeyUnlockService));
        return ok;
    }

    public static bool IsOtp(string text)
        => !string.IsNullOrWhiteSpace(text) && ModHexOtp.IsMatch(text.Trim());

    /// <summary>Yubico OTP alphabet only (keyboard-wedge characters).</summary>
    public static bool IsModHex(string text)
        => !string.IsNullOrEmpty(text) && ModHexText.IsMatch(text);

    public static string ExtractPublicId(string otp)
    {
        if (string.IsNullOrWhiteSpace(otp) || otp.Length < 12)
            throw new ArgumentException("OTP is too short.", nameof(otp));
        return otp[..12].ToLowerInvariant();
    }

    private static string? CaptureTouchOtp(Dispatcher dispatcher, Window? owner, string title, string message)
    {
        return dispatcher.Invoke(() =>
        {
            string? result = null;
            var dialog = new Window
            {
                Title = title,
                Width = 380,
                Height = 210,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Owner = owner,
                Background = System.Windows.Media.Brushes.White,
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
                FontSize = 14
            };

            var root = new StackPanel { Margin = new Thickness(16) };
            root.Children.Add(new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            });
            root.Children.Add(new TextBlock
            {
                Text = "Waiting for touch…",
                Foreground = System.Windows.Media.Brushes.Gray,
                Margin = new Thickness(0, 0, 0, 8)
            });

            var box = new TextBox
            {
                Height = 36,
                Padding = new Thickness(8, 6, 8, 6),
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                Opacity = 0.01
            };
            root.Children.Add(box);

            var cancel = new Button
            {
                Content = "Cancel",
                Width = 88,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0),
                IsCancel = true
            };
            cancel.Click += (_, _) => dialog.DialogResult = false;
            root.Children.Add(cancel);
            dialog.Content = root;

            var idle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            idle.Tick += (_, _) =>
            {
                idle.Stop();
                var text = box.Text.Trim();
                if (IsOtp(text))
                {
                    result = text.ToLowerInvariant();
                    dialog.DialogResult = true;
                }
            };

            box.TextChanged += (_, _) =>
            {
                idle.Stop();
                if (box.Text.Length >= 12)
                    idle.Start();
            };

            box.PreviewKeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter)
                    return;
                e.Handled = true;
                var text = box.Text.Trim();
                if (IsOtp(text))
                {
                    result = text.ToLowerInvariant();
                    dialog.DialogResult = true;
                }
            };

            dialog.Loaded += (_, _) => box.Focus();
            dialog.Closed += (_, _) => idle.Stop();
            return dialog.ShowDialog() == true ? result : null;
        });
    }
}
