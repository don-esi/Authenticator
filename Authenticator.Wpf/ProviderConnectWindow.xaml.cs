using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Authenticator.Services.Providers;
using Microsoft.Win32;
using OneOf;
using PasswordManager.Common;
using PasswordManager.LastPass.Ui;

namespace Authenticator;

public partial class ProviderConnectWindow : Window, IProviderMfaHost
{
    private readonly List<IVaultProvider> _providers = VaultProviderCatalog.CreateAll().ToList();
    private readonly ProviderDialogMode _mode;
    private readonly LastPassInteractiveUi _lastPassUi;
    private readonly CancellationTokenSource _connectCts = new();

    private bool _passwordRevealed;
    private ProviderMfaWindow? _openMfa;

    public IVaultProvider? ResultProvider { get; private set; }
    public int? ImportedCount { get; private set; }

    public ProviderConnectWindow(ProviderDialogMode mode)
    {
        _mode = mode;
        _lastPassUi = new LastPassInteractiveUi(this);
        InitializeComponent();

        Title = mode == ProviderDialogMode.Connect ? "Connect to password manager" : "Import from password manager";
        TitleText.Text = mode == ProviderDialogMode.Connect ? "Connect" : "Import";
        ModeHint.Text = mode == ProviderDialogMode.Connect
            ? "Credentials are stored on this PC (DPAPI) and the provider stays connected until you Disconnect. Enter this provider's own password/file — do not reuse another manager's."
            : "One-shot import only. Credentials are not retained after the import finishes. Enter this provider's own password/file.";
        PrimaryButton.Content = mode == ProviderDialogMode.Connect ? "Connect" : "Import";
        AutomationProperties.SetName(PrimaryButton, (string)PrimaryButton.Content);

        foreach (var p in _providers)
            ProviderBox.Items.Add(p.DisplayName);
        ProviderBox.SelectedIndex = 0;
        UpdateFieldVisibility();
        PrefillIdentityOnly();
    }

    private IVaultProvider? Selected =>
        ProviderBox.SelectedIndex >= 0 ? _providers[ProviderBox.SelectedIndex] : null;

    /// <summary>
    /// Never carry password/file from a previous provider selection or connect attempt.
    /// Username may be restored for the selected provider’s saved Connect record only.
    /// </summary>
    private void ClearCredentialFields()
    {
        PassBox.Clear();
        PassPlain.Text = string.Empty;
        SetPasswordRevealed(false);
        PathBox.Text = string.Empty;
        KeyBox.Text = string.Empty;
        UserBox.Text = string.Empty;
    }

    private string GetEnteredPassword() =>
        _passwordRevealed ? PassPlain.Text : PassBox.Password;

    private void ClearEnteredPassword()
    {
        PassBox.Clear();
        PassPlain.Text = string.Empty;
        SetPasswordRevealed(false);
    }

    private void RevealPass_Click(object sender, RoutedEventArgs e) =>
        SetPasswordRevealed(!_passwordRevealed);

    private void SetPasswordRevealed(bool revealed)
    {
        if (revealed && !_passwordRevealed)
            PassPlain.Text = PassBox.Password;
        else if (!revealed && _passwordRevealed)
            PassBox.Password = PassPlain.Text;

        _passwordRevealed = revealed;
        PassBox.Visibility = revealed ? Visibility.Collapsed : Visibility.Visible;
        PassPlain.Visibility = revealed ? Visibility.Visible : Visibility.Collapsed;
        RevealPassIcon.Text = revealed ? "\uE7B5" : "\uE7B3"; // Hide / RedEye
        RevealPassButton.ToolTip = revealed ? "Hide password" : "Show password";
        AutomationProperties.SetName(RevealPassButton, revealed ? "Hide password" : "Show password");
    }

    private void PrefillIdentityOnly()
    {
        if (_mode != ProviderDialogMode.Connect)
            return;

        var selected = Selected;
        if (selected is null)
            return;

        var saved = ProviderSessionService.Instance.SavedConnections
            .FirstOrDefault(c => string.Equals(c.ProviderId, selected.ProviderId, StringComparison.OrdinalIgnoreCase));
        if (saved is null)
            return;

        // Username only — never restore password or file paths from a prior connect.
        UserBox.Text = saved.Username ?? "";
    }

    private void ProviderBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ClearCredentialFields();
        UpdateFieldVisibility();
        PrefillIdentityOnly();
    }

    private void UpdateFieldVisibility()
    {
        var p = Selected;
        if (p is null)
            return;

        var c = p.Capabilities;
        CapsText.Text =
            $"Favorites={c.SupportsFavorites}, WriteBack={c.SupportsWriteBack}, File={c.RequiresFilePath}, User={c.RequiresUsername}";

        UserLabel.Visibility = c.RequiresUsername ? Visibility.Visible : Visibility.Collapsed;
        UserBox.Visibility = c.RequiresUsername ? Visibility.Visible : Visibility.Collapsed;

        PathLabel.Text = c.RequiresFilePath ? "Vault file" : "JSON export file (optional)";
        KeyFilePanel.Visibility = c.RequiresFilePath ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BrowsePath_Click(object sender, RoutedEventArgs e)
    {
        var requiresVault = Selected?.Capabilities.RequiresFilePath == true;
        var dialog = new OpenFileDialog
        {
            Title = requiresVault ? "Select vault file" : "Select JSON export",
            CheckFileExists = true,
            Multiselect = false,
            Filter = requiresVault
                ? "Vault files (*.kdbx;*.opvault)|*.kdbx;*.opvault|KeePass (*.kdbx)|*.kdbx|All files (*.*)|*.*"
                : "JSON export (*.json)|*.json|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog(this) == true)
            PathBox.Text = dialog.FileName;
    }

    private void BrowseKey_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select key file",
            CheckFileExists = true,
            Multiselect = false,
            Filter = "Key files (*.key;*.keyx)|*.key;*.keyx|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog(this) == true)
            KeyBox.Text = dialog.FileName;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _connectCts.Cancel();
        CloseOpenMfa();
        if (DialogResult is null)
            DialogResult = false;
        if (IsLoaded)
            Close();
    }

    private async void Primary_Click(object sender, RoutedEventArgs e)
    {
        var p = Selected;
        if (p is null) return;

        PrimaryButton.IsEnabled = false;
        ProviderBox.IsEnabled = false;
        SetBusy(true, _mode == ProviderDialogMode.Connect ? "Signing in…" : "Importing…");
        try
        {
            var request = new ProviderConnectRequest
            {
                Username = UserBox.Text.Trim(),
                Password = GetEnteredPassword(),
                FilePath = PathBox.Text.Trim(),
                KeyFilePath = string.IsNullOrWhiteSpace(KeyBox.Text) ? null : KeyBox.Text.Trim(),
                LastPassUi = string.Equals(p.ProviderId, "LastPass", StringComparison.OrdinalIgnoreCase)
                    ? _lastPassUi
                    : null
            };

            if (_mode == ProviderDialogMode.Connect)
            {
                // Connect only — VaultView asks "Sync now?" afterward so import is opt-in with its own spinner.
                var (provider, imported) = await ProviderSessionService.Instance.ConnectAndPersistAsync(
                    p, request, importAfterConnect: false, _connectCts.Token);
                ResultProvider = provider;
                ImportedCount = imported;
            }
            else
            {
                SetBusy(true, "Importing…");
                var (provider, imported) = await ProviderSessionService.Instance.ImportOnceAsync(
                    p, request, _connectCts.Token);
                ResultProvider = provider;
                ImportedCount = imported;
            }

            ClearEnteredPassword();
            DialogResult = true;
            Close();
        }
        catch (Exception ex) when (IsUserCancel(ex))
        {
            CloseOpenMfa();
            if (IsLoaded && DialogResult is null)
            {
                DialogResult = false;
                Close();
            }
        }
        catch (Exception ex)
        {
            ResultProvider = null;
            ImportedCount = null;
            CloseOpenMfa();
            if (!IsLoaded)
                return;

            AppDialog.Alert(
                this,
                FormatConnectError(ex),
                _mode == ProviderDialogMode.Connect ? "Connect failed" : "Import failed",
                AppDialogKind.Warning);
        }
        finally
        {
            CloseOpenMfa();
            SetBusy(false);
            if (IsLoaded)
            {
                PrimaryButton.IsEnabled = true;
                ProviderBox.IsEnabled = true;
            }
        }
    }

    private void SetBusy(bool busy, string? header = null)
    {
        if (!IsLoaded)
            return;
        if (header is not null)
            BusyIndicator.Header = header;
        BusyOverlay.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        BusyIndicator.IsBusy = busy;
    }

    private static bool IsUserCancel(Exception ex) =>
        ex is PasswordManager.Common.CanceledMultiFactorException
            or PasswordManager.Common.CanceledSsoLoginException
            or OperationCanceledException
        || (ex is AggregateException agg && agg.Flatten().InnerExceptions.Any(IsUserCancel));

    private static string FormatConnectError(Exception ex)
    {
        var parts = new List<string>();
        for (var cur = ex; cur is not null; cur = cur.InnerException)
        {
            if (!string.IsNullOrWhiteSpace(cur.Message) &&
                (parts.Count == 0 || !parts[^1].Contains(cur.Message, StringComparison.Ordinal)))
                parts.Add(cur.Message);
            if (parts.Count >= 3)
                break;
        }

        return string.Join(Environment.NewLine + Environment.NewLine, parts);
    }

    // —— IProviderMfaHost: separate modal dialog (not an in-pane shift) ——

    public Task<OneOf<Otp, MfaMethod, Canceled>> PromptOtpAsync(
        string prompt,
        int attempt,
        MfaMethod[] otherMethods,
        bool yubiKey,
        CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<OneOf<Otp, MfaMethod, Canceled>>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        _ = Dispatcher.InvokeAsync(() =>
        {
            if (cancellationToken.IsCancellationRequested || _connectCts.IsCancellationRequested)
            {
                tcs.TrySetResult(new Canceled("cancelled"));
                return;
            }

            try
            {
                CloseOpenMfa();
                var win = new ProviderMfaWindow(prompt, attempt, otherMethods, yubiKey, otpMode: true);
                AssignMfaOwner(win);
                _openMfa = win;
                using var reg = cancellationToken.Register(() =>
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (ReferenceEquals(_openMfa, win))
                            win.Close();
                    }));
                win.ShowDialog();
                tcs.TrySetResult(win.OtpOutcome ?? new Canceled("User cancelled"));
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
            finally
            {
                _openMfa = null;
            }
        });

        return tcs.Task;
    }

    public Task<OneOf<Otp, WaitForOutOfBand, MfaMethod, Canceled>> PromptLastPassAuthAsync(
        int attempt,
        MfaMethod[] otherMethods,
        CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<OneOf<Otp, WaitForOutOfBand, MfaMethod, Canceled>>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        _ = Dispatcher.InvokeAsync(() =>
        {
            if (cancellationToken.IsCancellationRequested || _connectCts.IsCancellationRequested)
            {
                tcs.TrySetResult(new Canceled("cancelled"));
                return;
            }

            try
            {
                CloseOpenMfa();
                var win = new ProviderMfaWindow(
                    "Enter LastPass Authenticator code",
                    attempt,
                    otherMethods,
                    yubiKey: false,
                    otpMode: false);
                AssignMfaOwner(win);
                _openMfa = win;
                using var reg = cancellationToken.Register(() =>
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (ReferenceEquals(_openMfa, win))
                            win.Close();
                    }));
                win.ShowDialog();
                tcs.TrySetResult(win.AuthOutcome ?? new Canceled("User cancelled"));
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
            finally
            {
                _openMfa = null;
            }
        });

        return tcs.Task;
    }

    private void AssignMfaOwner(Window mfa)
    {
        try
        {
            if (IsLoaded)
                mfa.Owner = this;
        }
        catch
        {
            // Connect window may already be closing.
        }
    }

    private void CloseOpenMfa()
    {
        var win = _openMfa;
        _openMfa = null;
        if (win is null)
            return;
        try
        {
            if (win.IsLoaded)
                win.Close();
        }
        catch
        {
            // Ignore close races.
        }
    }
}
