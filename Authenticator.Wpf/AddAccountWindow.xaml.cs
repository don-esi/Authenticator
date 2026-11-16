using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Authenticator.Data;
using Authenticator.Data.Models;
using Authenticator.Logging;
using Authenticator.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Win32;
using OtpNet;

namespace Authenticator;

public partial class AddAccountWindow : Window
{
    private readonly DatabaseService _database = new();
    private readonly QrCodeService _qr = new();
    private readonly Account? _existing;
    private int? _folderId;
    private bool _loadingFolders;
    private string _algorithm = "SHA1";
    private int _digits = 6;
    private int _period = 30;
    private bool _busy;

    public AddAccountWindow(Account? existing, int? defaultFolderId)
    {
        _existing = existing;
        _folderId = existing?.FolderId ?? defaultFolderId;
        InitializeComponent();
        SyncfusionBootstrap.ApplyTo(this);

        if (existing is null)
            return;

        Title = "Edit account";
        TitleText.Text = "Edit account";
        ServiceNameBox.Text = existing.ServiceName;
        ServiceUrlBox.Text = existing.ServiceUrl ?? string.Empty;
        UsernameBox.Text = existing.Username;
        SecretKeyBox.Text = existing.SecretKey;
        _algorithm = NormalizeAlgorithm(existing.Algorithm);
        _digits = existing.Digits == 8 ? 8 : 6;
        _period = existing.Period is 15 or 30 or 60 or 120 ? existing.Period : 30;
        BackupCodesBox.Text = string.Join(Environment.NewLine,
            (existing.BackupCodes ?? [])
                .Where(code => !code.IsDeleted)
                .OrderBy(code => code.CreatedAt)
                .ThenBy(code => code.Id)
                .Select(code => code.Code));
        DeleteButton.Visibility = Visibility.Visible;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        BuildSegments();
        var desiredFolderId = _folderId;
        _loadingFolders = true;
        try
        {
            var folders = await _database.GetAllFoldersAsync();
            FolderBox.Items.Clear();
            FolderBox.Items.Add(new ComboBoxItem { Content = "None", Tag = null });
            foreach (var folder in folders.OrderBy(folder => folder.Name, StringComparer.OrdinalIgnoreCase))
                FolderBox.Items.Add(new ComboBoxItem { Content = folder.Name, Tag = folder.Id });

            var matched = false;
            foreach (ComboBoxItem item in FolderBox.Items)
            {
                int? id = item.Tag is int value ? value : null;
                if (id == desiredFolderId)
                {
                    FolderBox.SelectedItem = item;
                    matched = true;
                    break;
                }
            }

            if (!matched)
                FolderBox.SelectedIndex = 0;

            _folderId = matched ? desiredFolderId : null;
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Failed to load folders for the account form.", nameof(AddAccountWindow));
            ErrorText.Text = ex.Message;
        }
        finally
        {
            _loadingFolders = false;
        }
    }

    private void BuildSegments()
    {
        FillChoices(AlgorithmPanel, "Algorithm", ["SHA1", "SHA256", "SHA512"], _algorithm, value => _algorithm = value);
        FillChoices(DigitsPanel, "Digits", ["6", "8"], _digits.ToString(), value => _digits = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture));
        FillChoices(PeriodPanel, "Period", ["15", "30", "60", "120"], _period.ToString(), value => _period = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture));
    }

    private void FillChoices(StackPanel panel, string group, string[] values, string selected, Action<string> choose)
    {
        panel.Children.Clear();
        foreach (var value in values)
        {
            var isSelected = string.Equals(value, selected, StringComparison.OrdinalIgnoreCase);
            var button = new Button
            {
                Content = value,
                Tag = value,
                Margin = new Thickness(2),
                Style = (Style)FindResource("SegmentButton"),
                Foreground = isSelected ? Brushes.White : AccountEdits.BrushFromHex("#1F2937"),
                Background = isSelected ? AccountEdits.BrushFromHex("#0078D4") : Brushes.Transparent
            };
            AutomationProperties.SetName(button, $"{group} {value}");
            button.Click += (_, _) =>
            {
                choose(value);
                FillChoices(panel, group, values, value, choose);
            };
            panel.Children.Add(button);
        }
    }

    private void FolderBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingFolders)
            return;

        _folderId = FolderBox.SelectedItem is ComboBoxItem { Tag: int id } ? id : null;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;

        if (!TryReadForm(out var service, out var url, out var username, out var secret, out var backupLines, out var error))
        {
            ErrorText.Text = error;
            return;
        }

        _busy = true;
        SaveButton.IsEnabled = false;
        ErrorText.Text = string.Empty;
        try
        {
            int accountId;
            if (_existing is null)
            {
                var account = new Account
                {
                    ServiceName = service,
                    ServiceUrl = url,
                    Username = username,
                    SecretKey = secret,
                    Algorithm = _algorithm,
                    Digits = _digits,
                    Period = _period,
                    FolderId = _folderId
                };
                var saved = await _database.AddAccountAsync(account);
                accountId = saved.Id;
            }
            else
            {
                var updated = AccountEdits.CopyScalars(_existing);
                updated.ServiceName = service;
                updated.ServiceUrl = url;
                updated.Username = username;
                updated.SecretKey = secret;
                updated.Algorithm = _algorithm;
                updated.Digits = _digits;
                updated.Period = _period;
                updated.FolderId = _folderId;
                await _database.UpdateAccountAsync(updated);
                accountId = updated.Id;
            }

            await SyncBackupCodesAsync(accountId, backupLines);
            LoggingConfiguration.LogInfo($"Account saved. Id={accountId}.", nameof(AddAccountWindow));
            if (IsLoaded)
                DialogResult = true;
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Failed to save account.", nameof(AddAccountWindow));
            ErrorText.Text = ex.Message;
            _busy = false;
            SaveButton.IsEnabled = true;
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_existing is null || _busy)
            return;

        if (!AppDialog.Confirm(
            this,
            $"Delete \"{_existing.ServiceName}\"?",
            "Delete account",
            confirmText: "Delete",
            kind: AppDialogKind.Warning))
            return;

        _busy = true;
        DeleteButton.IsEnabled = false;
        try
        {
            await _database.DeleteAccountAsync(_existing.Id);
            LoggingConfiguration.LogInfo($"Account deleted. Id={_existing.Id}.", nameof(AddAccountWindow));
            if (IsLoaded)
                DialogResult = true;
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, $"Failed to delete account id {_existing.Id}.", nameof(AddAccountWindow));
            ErrorText.Text = ex.Message;
            _busy = false;
            DeleteButton.IsEnabled = true;
        }
    }

    private void FillFromLink_Click(object sender, RoutedEventArgs e)
    {
        var text = OtpAuthBox.Text.Trim();
        if (GoogleMigrationParser.IsMigrationUri(text))
        {
            ErrorText.Text = "Google Authenticator transfer QRs belong in Settings → Export and Import.";
            return;
        }

        var parsed = _qr.ParseOtpAuthUri(text);
        if (parsed is null)
        {
            ErrorText.Text = "Paste an otpauth://totp/ link.";
            return;
        }

        ApplyQr(parsed);
    }

    private void DecodeImage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a QR image",
            Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            using var source = new System.Drawing.Bitmap(dialog.FileName);
            using var bitmap = new System.Drawing.Bitmap(source.Width, source.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
                graphics.DrawImage(source, 0, 0, source.Width, source.Height);

            var text = _qr.DecodeQrText(bitmap);
            if (string.IsNullOrWhiteSpace(text))
            {
                ErrorText.Text = "No QR code found in that image.";
                return;
            }

            if (GoogleMigrationParser.IsMigrationUri(text))
            {
                ErrorText.Text = "Google Authenticator transfer QRs belong in Settings → Export and Import.";
                return;
            }

            var parsed = _qr.ParseOtpAuthUri(text);
            if (parsed is null)
            {
                ErrorText.Text = "No otpauth://totp QR code found in that image.";
                return;
            }

            ApplyQr(parsed);
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Failed to decode a QR image.", nameof(AddAccountWindow));
            ErrorText.Text = "Could not read that image.";
        }
    }

    private async void CaptureQr_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;

        _busy = true;
        // Hide() ends ShowDialog, so Save can no longer set DialogResult.
        // Stay visible to WPF and move off the monitor until the capture finishes.
        var restoreOpacity = Opacity;
        var restoreLeft = Left;
        var restoreTop = Top;
        Opacity = 0;
        if (!double.IsNaN(restoreLeft))
            Left = -20000;
        if (!double.IsNaN(restoreTop))
            Top = -20000;
        try
        {
            var region = QrCaptureWindow.Select();
            if (region is null)
                return;

            await Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(100);

            using var bitmap = await _qr.CaptureScreenRegionAsync(region.Value.X, region.Value.Y, region.Value.Width, region.Value.Height);
            if (bitmap is null)
            {
                ErrorText.Text = "Could not capture that region.";
                return;
            }

            var text = _qr.DecodeQrText(bitmap);
            if (string.IsNullOrWhiteSpace(text))
            {
                ErrorText.Text = "No QR code found in that region.";
                return;
            }

            if (GoogleMigrationParser.IsMigrationUri(text))
            {
                ErrorText.Text = "Google Authenticator transfer QRs belong in Settings → Export and Import.";
                return;
            }

            var parsed = _qr.ParseOtpAuthUri(text);
            if (parsed is null)
            {
                ErrorText.Text = "No otpauth://totp QR code found in that region.";
                return;
            }

            ApplyQr(parsed);
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Failed to capture a QR region.", nameof(AddAccountWindow));
            ErrorText.Text = "Could not capture that region.";
        }
        finally
        {
            _busy = false;
            Opacity = restoreOpacity;
            if (!double.IsNaN(restoreLeft))
                Left = restoreLeft;
            if (!double.IsNaN(restoreTop))
                Top = restoreTop;
            if (IsLoaded)
                Activate();
        }
    }

    private void ApplyQr(QrCodeResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.Issuer))
            ServiceNameBox.Text = result.Issuer.Trim();
        if (!string.IsNullOrWhiteSpace(result.Account))
            UsernameBox.Text = result.Account.Trim();

        SecretKeyBox.Text = AccountEdits.NormalizeSecret(result.Secret);
        _algorithm = NormalizeAlgorithm(result.Algorithm);
        _digits = result.Digits == 8 ? 8 : 6;
        _period = result.Period is 15 or 30 or 60 or 120 ? result.Period : 30;
        BuildSegments();
        OtpAuthBox.Text = string.Empty;
        ErrorText.Text = string.Empty;
        LoggingConfiguration.LogInfo("Filled the account form from a QR code.", nameof(AddAccountWindow));
    }

    private bool TryReadForm(out string service, out string? url, out string username, out string secret, out List<string> backupLines, out string error)
    {
        service = ServiceNameBox.Text.Trim();
        username = UsernameBox.Text.Trim();
        secret = AccountEdits.NormalizeSecret(SecretKeyBox.Text);
        var rawUrl = ServiceUrlBox.Text.Trim();
        url = string.IsNullOrWhiteSpace(rawUrl) ? null : rawUrl;
        backupLines = [];
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(service))
        {
            error = "Service name is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(username))
        {
            error = "Username is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(secret))
        {
            error = "Secret key is required.";
            return false;
        }

        try
        {
            _ = Base32Encoding.ToBytes(secret);
        }
        catch (Exception)
        {
            error = "Secret key must be base32.";
            return false;
        }

        backupLines = BackupCodesBox.Text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();
        if (backupLines.Any(line => line.Length > 50))
        {
            error = "Each backup code must be 50 characters or fewer.";
            return false;
        }

        return true;
    }

    private static async Task SyncBackupCodesAsync(int accountId, IReadOnlyList<string> desired)
    {
        await using var context = new AuthenticatorDbContext();
        var existing = await context.BackupCodes
            .Where(code => code.AccountId == accountId && !code.IsDeleted)
            .ToListAsync();
        var unmatched = existing.ToList();
        var added = 0;

        foreach (var line in desired)
        {
            var match = unmatched.FirstOrDefault(code => string.Equals(code.Code, line, StringComparison.Ordinal));
            if (match is null)
            {
                context.BackupCodes.Add(new BackupCode
                {
                    AccountId = accountId,
                    Code = line,
                    CreatedAt = DateTime.UtcNow
                });
                added++;
            }
            else
            {
                unmatched.Remove(match);
            }
        }

        foreach (var code in unmatched)
            code.IsDeleted = true;

        await context.SaveChangesAsync();
        LoggingConfiguration.LogInfo($"Backup codes synced for account id {accountId}. Added {added}, removed {unmatched.Count}.", nameof(AddAccountWindow));
    }

    private static string NormalizeAlgorithm(string? algorithm)
    {
        return algorithm?.Trim().ToUpperInvariant() switch
        {
            "SHA256" => "SHA256",
            "SHA512" => "SHA512",
            _ => "SHA1"
        };
    }
}
