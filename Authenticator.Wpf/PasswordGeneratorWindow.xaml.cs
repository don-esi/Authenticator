using System.Windows;
using Authenticator.Services;

namespace Authenticator;

public partial class PasswordGeneratorWindow : Window
{
    public string GeneratedPassword { get; private set; } = "";

    public PasswordGeneratorWindow()
    {
        InitializeComponent();
        SyncfusionBootstrap.ApplyTo(this);
        Regenerate();
    }

    private void Options_Changed(object sender, RoutedEventArgs e) => Regenerate();

    private void LengthBox_ValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => Regenerate();

    private void Regenerate_Click(object sender, RoutedEventArgs e) => Regenerate();

    private void Regenerate()
    {
        if (LengthBox is null) return;
        var length = (int)(LengthBox.Value ?? 20);
        GeneratedPassword = PasswordGeneratorService.Generate(new PasswordGeneratorOptions
        {
            Length = length,
            Upper = UpperCheck.IsChecked == true,
            Lower = LowerCheck.IsChecked == true,
            Digits = DigitsCheck.IsChecked == true,
            Symbols = SymbolsCheck.IsChecked == true,
            ExcludeAmbiguous = AmbiguousCheck.IsChecked == true
        });
        PasswordBox.Text = GeneratedPassword;
    }

    private void Copy_Click(object sender, RoutedEventArgs e) => ClipboardHelper.Copy(GeneratedPassword);

    private void Use_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
