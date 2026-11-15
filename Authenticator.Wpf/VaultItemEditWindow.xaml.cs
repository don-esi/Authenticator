using System.Windows;
using Authenticator.Data.Models;
using Authenticator.Services;

namespace Authenticator;

public partial class VaultItemEditWindow : Window
{
    private readonly VaultItem _item;
    private readonly VaultItemService _vault = new();
    private readonly bool _isNew;
    private bool _passwordRevealed;
    private bool _passwordLoaded;
    private bool _passwordTouched;
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

        PassMasked.PasswordChanged += (_, _) => _passwordTouched = true;
        PassPlain.TextChanged += (_, _) => _passwordTouched = true;

        if (_isNew)
        {
            // New entries: show plaintext so the user can type immediately.
            RevealPassButton.Content = "Hide";
            SetPasswordRevealed(true);
        }
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
    }

    private void Generate_Click(object sender, RoutedEventArgs e)
    {
        var gen = new PasswordGeneratorWindow { Owner = this };
        if (gen.ShowDialog() != true)
            return;

        _passwordTouched = true;
        _passwordLoaded = true;
        if (_passwordRevealed)
            PassPlain.Text = gen.GeneratedPassword;
        else
            PassMasked.Password = gen.GeneratedPassword;
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
            MessageBox.Show(this, "Title is required.", "Vault");
            return;
        }

        await _vault.SaveAsync(_item);
        DialogResult = true;
        Close();
    }

    private void SetPasswordRevealed(bool revealed)
    {
        _passwordRevealed = revealed;
        PassMasked.Visibility = revealed ? Visibility.Collapsed : Visibility.Visible;
        PassPlain.Visibility = revealed ? Visibility.Visible : Visibility.Collapsed;
    }
}
