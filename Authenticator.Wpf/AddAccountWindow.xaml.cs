using System.Windows;
using Authenticator.Data.Models;
using OtpNet;

namespace Authenticator;

public partial class AddAccountWindow : Window
{
    public Account? AccountToSave { get; private set; }

    public AddAccountWindow()
    {
        InitializeComponent();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var serviceName = ServiceNameBox.Text.Trim();
        var username = UsernameBox.Text.Trim();
        var secretKey = NormalizeSecret(SecretKeyBox.Text);

        if (string.IsNullOrWhiteSpace(serviceName))
        {
            ErrorText.Text = "Service name is required.";
            return;
        }

        if (string.IsNullOrWhiteSpace(username))
        {
            ErrorText.Text = "Username is required.";
            return;
        }

        if (string.IsNullOrWhiteSpace(secretKey))
        {
            ErrorText.Text = "Secret key is required.";
            return;
        }

        try
        {
            _ = Base32Encoding.ToBytes(secretKey);
        }
        catch (Exception)
        {
            ErrorText.Text = "Secret key must be base32.";
            return;
        }

        AccountToSave = new Account
        {
            ServiceName = serviceName,
            Username = username,
            SecretKey = secretKey,
            Algorithm = "SHA1",
            Digits = 6,
            Period = 30
        };

        DialogResult = true;
    }

    private static string NormalizeSecret(string secretKey)
    {
        return secretKey.Replace(" ", string.Empty, StringComparison.Ordinal)
                        .Replace("-", string.Empty, StringComparison.Ordinal)
                        .Trim()
                        .ToUpperInvariant();
    }
}
