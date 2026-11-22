using System.Windows;
using Authenticator.Data.Models;
using Authenticator.Services;
using Authenticator.Services.Providers;

namespace Authenticator;

public partial class VaultItemEditWindow : Window
{
    private readonly VaultItem _item;
    private readonly VaultItemService _vault = new();
    private readonly bool _isNew;
    private bool _passwordRevealed;
    private bool _passwordLoaded;
    private bool _passwordTouched;
    private bool _hasStoredPassword;
    private string? _sealedPassword;

    public VaultItemEditWindow(VaultItem item, bool isNew = false)
    {
        InitializeComponent();
        _item = item;
        _isNew = isNew;

        // Existing items: keep ciphertext off the UI until Reveal; preserve on save if untouched.
        if (!_isNew && !string.IsNullOrEmpty(item.Password))
        {
            _sealedPassword = item.Password;
            _item.Password = null;
            _hasStoredPassword = true;
        }

        foreach (VaultItemType t in Enum.GetValues(typeof(VaultItemType)))
            TypeBox.Items.Add(t);
        TypeBox.SelectedItem = item.ItemType;
        TitleBox.Text = item.Title;
        UserBox.Text = item.Username ?? "";
        UrlBox.Text = item.Url ?? "";
        NotesBox.Text = item.Notes ?? "";
        TotpBox.Text = item.TotpSecret ?? "";
        LinkedBox.Text = item.LinkedAccountId?.ToString() ?? "";
        FavoriteCheck.IsChecked = item.IsFavorite;
        AutoFillCheck.IsChecked = item.AllowAutoFill;
        AutoLoginCheck.IsChecked = item.AllowAutoLogin;
        SyncAutoLoginEnabled();
        UpdateLinkedSourcesSummary(item);

        PassMasked.PasswordChanged += (_, _) =>
        {
            _passwordTouched = true;
            UpdatePasswordPlaceholder();
        };
        PassPlain.TextChanged += (_, _) => _passwordTouched = true;

        if (_isNew)
        {
            // New entries: show plaintext so the user can type immediately.
            RevealPassButton.Content = "Hide";
            SetPasswordRevealed(true);
        }
        else
        {
            UpdatePasswordPlaceholder();
        }
    }

    private void AutoFillCheck_Changed(object sender, RoutedEventArgs e) => SyncAutoLoginEnabled();

    private void SyncAutoLoginEnabled()
    {
        var fillOn = AutoFillCheck.IsChecked == true;
        AutoLoginCheck.IsEnabled = fillOn;
        if (!fillOn)
            AutoLoginCheck.IsChecked = false;
    }

    private void UpdateLinkedSourcesSummary(VaultItem item)
    {
        if (LinkedSourcesText is null)
            return;

        var links = item.ExternalLinks?
            .Where(l => !string.IsNullOrWhiteSpace(l.Provider))
            .OrderBy(l => l.Provider, StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];

        if (links.Count == 0)
        {
            LinkedSourcesText.Text = "None — saves to Shared only until imported/synced from a provider.";
            return;
        }

        LinkedSourcesText.Text = string.Join(
            Environment.NewLine,
            links.Select(l =>
                $"{l.Provider}: {l.ExternalId}" + (l.IsMismatch ? " (conflict)" : "")));
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private async void RevealPass_Click(object sender, RoutedEventArgs e)
    {
        if (_passwordRevealed)
        {
            PassMasked.Password = PassPlain.Text;
            SetPasswordRevealed(false);
            RevealPassButton.Content = "Reveal";
            UpdatePasswordPlaceholder();
            return;
        }

        if (!_passwordLoaded && !_isNew)
        {
            RevealPassButton.IsEnabled = false;
            try
            {
                var password = await _vault.GetPasswordAsync(_item.Id);
                _item.Password = password;
                _sealedPassword = null;
                _hasStoredPassword = !string.IsNullOrEmpty(password);
                _passwordLoaded = true;
                // Don't mark touched — loading existing value is not an edit.
                var priorTouched = _passwordTouched;
                PassPlain.Text = password ?? "";
                _passwordTouched = priorTouched;
            }
            finally
            {
                RevealPassButton.IsEnabled = true;
            }
        }
        else if (string.IsNullOrEmpty(PassPlain.Text) && PassMasked.SecurePassword.Length > 0)
        {
            PassPlain.Text = PassMasked.Password;
        }

        SetPasswordRevealed(true);
        RevealPassButton.Content = "Hide";
        UpdatePasswordPlaceholder();
    }

    private void Generate_Click(object sender, RoutedEventArgs e)
    {
        var gen = new PasswordGeneratorWindow { Owner = this };
        if (gen.ShowDialog() != true)
            return;

        _passwordTouched = true;
        _passwordLoaded = true;
        _hasStoredPassword = true;
        if (_passwordRevealed)
            PassPlain.Text = gen.GeneratedPassword;
        else
            PassMasked.Password = gen.GeneratedPassword;
        UpdatePasswordPlaceholder();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        _item.ItemType = TypeBox.SelectedItem is VaultItemType t ? t : VaultItemType.Login;
        _item.Title = TitleBox.Text.Trim();
        _item.Username = UserBox.Text.Trim();
        _item.Url = UrlBox.Text.Trim();
        _item.Notes = NotesBox.Text;
        _item.TotpSecret = string.IsNullOrWhiteSpace(TotpBox.Text) ? null : TotpBox.Text.Trim();
        _item.IsFavorite = FavoriteCheck.IsChecked == true;
        _item.AllowAutoFill = AutoFillCheck.IsChecked == true;
        _item.AllowAutoLogin = _item.AllowAutoFill && AutoLoginCheck.IsChecked == true;
        if (int.TryParse(LinkedBox.Text.Trim(), out var linked))
            _item.LinkedAccountId = linked;
        else
            _item.LinkedAccountId = null;

        if (_isNew || _passwordTouched || _passwordLoaded)
            _item.Password = _passwordRevealed ? PassPlain.Text : PassMasked.Password;
        else
            _item.Password = _sealedPassword;

        if (string.IsNullOrWhiteSpace(_item.Title))
        {
            AppDialog.Alert(this, "Title is required.", "Vault", AppDialogKind.Warning);
            return;
        }

        await _vault.SaveAsync(_item);

        // Push Shared edits to every linked origin store (ExternalLink.ExternalId).
        // Re-load with full secret reveal so write-back never sends sealed ciphertext.
        if (!_isNew)
        {
            var forPush = await _vault.GetByIdAsync(_item.Id) ?? _item;
            var push = await new VaultImportService().PushToLinkedProvidersAsync(forPush);
            if (push.Attempted)
            {
                var detail = push.Messages.Count > 0
                    ? "\n\n" + string.Join("\n", push.Messages.Take(6))
                    : "";
                if (push.Failed > 0 || push.Skipped > 0 || push.Updated == 0)
                {
                AppDialog.Alert(
                    this,
                    $"Saved to Shared. Updated {push.Updated} connected source(s); " +
                    $"{push.Skipped} skipped, {push.Failed} failed.{detail}",
                    "Vault",
                    push.Failed > 0 ? AppDialogKind.Warning : AppDialogKind.Info);
                }
            }
        }

        DialogResult = true;
        Close();
    }

    private void SetPasswordRevealed(bool revealed)
    {
        _passwordRevealed = revealed;
        PassMasked.Visibility = revealed ? Visibility.Collapsed : Visibility.Visible;
        PassPlain.Visibility = revealed ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdatePasswordPlaceholder()
    {
        if (PassPlaceholder is null)
            return;

        // Bullets only when a stored password exists and the masked box is still untouched/empty.
        var show = !_passwordRevealed
                   && !_passwordTouched
                   && !_passwordLoaded
                   && _hasStoredPassword
                   && PassMasked.SecurePassword.Length == 0;
        PassPlaceholder.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }
}
