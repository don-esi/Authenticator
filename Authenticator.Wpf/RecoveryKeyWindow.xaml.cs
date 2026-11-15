using System.Windows;
using System.Windows.Input;
using Authenticator.Services;

namespace Authenticator;

public partial class RecoveryKeyWindow : Window
{
    public RecoveryKeyWindow(string recoveryKey)
    {
        InitializeComponent();
        SyncfusionBootstrap.ApplyTo(this);
        KeyBox.Text = recoveryKey ?? "";
        Loaded += (_, _) =>
        {
            KeyBox.Focus();
            KeyBox.SelectAll();
        };
    }

    public static void Show(Window owner, string recoveryKey)
    {
        var win = new RecoveryKeyWindow(recoveryKey) { Owner = owner };
        win.ShowDialog();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        ClipboardHelper.Copy(KeyBox.Text, clearAfterSeconds: 0);
        CopyStatusText.Text = "Copied to clipboard.";
        KeyBox.Focus();
        KeyBox.SelectAll();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
        {
            ClipboardHelper.Copy(KeyBox.Text, clearAfterSeconds: 0);
            CopyStatusText.Text = "Copied to clipboard.";
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }
}
