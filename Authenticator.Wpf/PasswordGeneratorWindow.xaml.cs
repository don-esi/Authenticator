using System.Windows;
using Authenticator.Services;

namespace Authenticator;

public partial class PasswordGeneratorWindow : Window
{
    public string GeneratedPassword { get; private set; } = "";

    public PasswordGeneratorWindow()
    {
        InitializeComponent();
        Regenerate();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Options_Changed(object sender, RoutedEventArgs e) => Regenerate();

    private void LengthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (LengthValueText is not null)
            LengthValueText.Text = ((int)e.NewValue).ToString();
        Regenerate();
    }

    private void Regenerate_Click(object sender, RoutedEventArgs e) => Regenerate();

    private void Regenerate()
    {
        if (LengthSlider is null || PasswordBox is null)
            return;

        var length = (int)LengthSlider.Value;
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
