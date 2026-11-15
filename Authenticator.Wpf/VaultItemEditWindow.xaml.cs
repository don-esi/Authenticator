using System.Windows;
using Authenticator.Data.Models;
using Authenticator.Services;

namespace Authenticator;

public partial class VaultItemEditWindow : Window
{
    private readonly VaultItem _item;
    private readonly VaultItemService _vault = new();
    private bool _passRevealed;

    public VaultItemEditWindow(VaultItem item)
    {
        InitializeComponent();
        _item = item;
        foreach (VaultItemType t in Enum.GetValues(typeof(VaultItemType)))
            TypeBox.Items.Add(t);
        TypeBox.SelectedItem = item.ItemType;
        TitleBox.Text = item.Title;
        UserBox.Text = item.Username ?? "";
        PassBox.Text = item.Password ?? "";
        UrlBox.Text = item.Url ?? "";
        NotesBox.Text = item.Notes ?? "";
        TotpBox.Text = item.TotpSecret ?? "";
        LinkedBox.Text = item.LinkedAccountId?.ToString() ?? "";
        FavoriteCheck.IsChecked = item.IsFavorite;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void RevealPass_Click(object sender, RoutedEventArgs e)
    {
        _passRevealed = !_passRevealed;
        RevealPassButton.Content = _passRevealed ? "Hide" : "Reveal";
        MessageBox.Show(this, _passRevealed ? "Password field visible." : "Toggle noted.", "Un-hide");
    }

    private void Generate_Click(object sender, RoutedEventArgs e)
    {
        var gen = new PasswordGeneratorWindow { Owner = this };
        if (gen.ShowDialog() == true)
            PassBox.Text = gen.GeneratedPassword;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        _item.ItemType = TypeBox.SelectedItem is VaultItemType t ? t : VaultItemType.Login;
        _item.Title = TitleBox.Text.Trim();
        _item.Username = UserBox.Text.Trim();
        _item.Password = PassBox.Text;
        _item.Url = UrlBox.Text.Trim();
        _item.Notes = NotesBox.Text;
        _item.TotpSecret = string.IsNullOrWhiteSpace(TotpBox.Text) ? null : TotpBox.Text.Trim();
        _item.IsFavorite = FavoriteCheck.IsChecked == true;
        if (int.TryParse(LinkedBox.Text.Trim(), out var linked))
            _item.LinkedAccountId = linked;
        else
            _item.LinkedAccountId = null;

        if (string.IsNullOrWhiteSpace(_item.Title))
        {
            MessageBox.Show(this, "Title is required.", "Vault");
            return;
        }

        await _vault.SaveAsync(_item);
        DialogResult = true;
        Close();
    }
}
