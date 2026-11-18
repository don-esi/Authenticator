using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OneOf;
using PasswordManager.Common;
using PasswordManager.LastPass.Ui;

namespace Authenticator;

/// <summary>
/// Modal MFA prompt for provider Connect/Import (YubiKey, authenticator codes, method switch).
/// Kept separate from <see cref="ProviderConnectWindow"/> so the credential form does not shift.
/// </summary>
public partial class ProviderMfaWindow : Window
{
    /// <summary>LastPass Yubico OTP is exactly 44 modhex chars — do not use unlock IsOtp (32–64).</summary>
    private static readonly Regex LastPassYubiOtp = new(
        @"^[cbdefghijklnrtuv]{44}$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly bool _yubiKey;
    private readonly bool _otpMode;
    private readonly MfaMethod[] _otherMethods;
    private bool _completed;

    public OneOf<Otp, MfaMethod, Canceled>? OtpOutcome { get; private set; }
    public OneOf<Otp, WaitForOutOfBand, MfaMethod, Canceled>? AuthOutcome { get; private set; }

    public ProviderMfaWindow(
        string prompt,
        int attempt,
        MfaMethod[] otherMethods,
        bool yubiKey,
        bool otpMode)
    {
        _yubiKey = yubiKey;
        _otpMode = otpMode;
        _otherMethods = otherMethods ?? Array.Empty<MfaMethod>();
        InitializeComponent();

        MfaStatus.Text = attempt > 0 ? $"{prompt} (attempt {attempt + 1})" : prompt;
        if (_yubiKey && attempt == 0)
            MfaStatus.Text = "Click the OTP box, then touch your YubiKey";

        FillAltMethods();
        Loaded += (_, _) =>
        {
            Keyboard.Focus(MfaBox);
            MfaBox.Focus();
        };
    }

    private void FillAltMethods()
    {
        AltMethods.Children.Clear();
        var any = false;
        foreach (var method in _otherMethods)
        {
            if (method is MfaMethod.U2F or MfaMethod.Fido2 or MfaMethod.WebAuthn or MfaMethod.None)
                continue;

            var button = new Button
            {
                Content = method.GetName(),
                Tag = method,
                Margin = new Thickness(0, 0, 8, 4),
                Padding = new Thickness(8, 4, 8, 4),
                Style = (Style)FindResource("SecondaryButton")
            };
            button.Click += AltMethod_Click;
            AltMethods.Children.Add(button);
            any = true;
        }

        AltMethodsLabel.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AltMethod_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MfaMethod method })
            return;

        if (_otpMode)
            OtpOutcome = method;
        else
            AuthOutcome = method;
        CompleteAndClose();
    }

    private void MfaBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        TrySubmitCode();
    }

    private void MfaBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_yubiKey)
            return;

        var code = MfaBox.Text.Trim();
        MfaStatus.Text = code.Length == 0
            ? "Click the OTP box, then touch your YubiKey"
            : LastPassYubiOtp.IsMatch(code)
                ? $"YubiKey OTP complete ({code.Length}/44)"
                : $"YubiKey keystrokes ({code.Length}/44)";

        if (LastPassYubiOtp.IsMatch(code))
            TrySubmitCode();
    }

    private void Submit_Click(object sender, RoutedEventArgs e) => TrySubmitCode();

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_otpMode)
            OtpOutcome = new Canceled("User cancelled");
        else
            AuthOutcome = new Canceled("User cancelled");
        CompleteAndClose();
    }

    private void TrySubmitCode()
    {
        var code = MfaBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(code))
        {
            MfaStatus.Text = _yubiKey
                ? "Click the OTP box, then touch your YubiKey"
                : "Enter the authenticator code";
            return;
        }

        if (_yubiKey && !LastPassYubiOtp.IsMatch(code))
        {
            MfaStatus.Text = $"Waiting for the full YubiKey code ({code.Length}/44)";
            return;
        }

        var otp = new Otp(code, MfaRemember.IsChecked == true);
        if (_otpMode)
            OtpOutcome = otp;
        else
            AuthOutcome = otp;
        CompleteAndClose();
    }

    private void CompleteAndClose()
    {
        _completed = true;
        try
        {
            DialogResult = true;
        }
        catch
        {
            // DialogResult only valid for ShowDialog; Close is enough.
        }

        Close();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_completed)
            return;

        // Title-bar close / Esc without Submit.
        if (_otpMode)
            OtpOutcome ??= new Canceled("User cancelled");
        else
            AuthOutcome ??= new Canceled("User cancelled");
    }
}
