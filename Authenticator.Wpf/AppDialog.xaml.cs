using System.Windows;
using System.Windows.Media;

namespace Authenticator;

public enum AppDialogKind
{
    Info,
    Warning,
    Error,
    Confirm
}

public enum AppDialogButtons
{
    Ok,
    OkCancel,
    YesNo
}

public partial class AppDialog : Window
{
    private bool _accepted;

    private AppDialog(
        string title,
        string message,
        AppDialogKind kind,
        AppDialogButtons buttons,
        string? primaryText,
        string? secondaryText)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        ApplyKind(kind);
        ApplyButtons(buttons, primaryText, secondaryText);
    }

    public static void Alert(
        Window? owner,
        string message,
        string title = "Authenticator",
        AppDialogKind kind = AppDialogKind.Info)
    {
        var dialog = new AppDialog(title, message, kind, AppDialogButtons.Ok, null, null)
        {
            Owner = owner ?? Application.Current?.MainWindow
        };
        dialog.ShowDialog();
    }

    public static bool Confirm(
        Window? owner,
        string message,
        string title = "Confirm",
        string confirmText = "OK",
        string cancelText = "Cancel",
        AppDialogKind kind = AppDialogKind.Confirm)
    {
        var dialog = new AppDialog(
            title,
            message,
            kind,
            AppDialogButtons.OkCancel,
            confirmText,
            cancelText)
        {
            Owner = owner ?? Application.Current?.MainWindow
        };
        return dialog.ShowDialog() == true;
    }

    public static bool YesNo(
        Window? owner,
        string message,
        string title = "Confirm",
        AppDialogKind kind = AppDialogKind.Confirm)
    {
        var dialog = new AppDialog(title, message, kind, AppDialogButtons.YesNo, "Yes", "No")
        {
            Owner = owner ?? Application.Current?.MainWindow
        };
        return dialog.ShowDialog() == true;
    }

    private void ApplyKind(AppDialogKind kind)
    {
        switch (kind)
        {
            case AppDialogKind.Warning:
                IconChrome.Background = AccountEdits.BrushFromHex("#FEF3C7");
                IconGlyph.Foreground = AccountEdits.BrushFromHex("#B45309");
                IconGlyph.Text = "\uE7BA"; // Warning
                break;
            case AppDialogKind.Error:
                IconChrome.Background = AccountEdits.BrushFromHex("#FEE2E2");
                IconGlyph.Foreground = (Brush)FindResource("DangerBrush");
                IconGlyph.Text = "\uEA39"; // ErrorBadge
                break;
            case AppDialogKind.Confirm:
                IconChrome.Background = AccountEdits.BrushFromHex("#E8F0FE");
                IconGlyph.Foreground = (Brush)FindResource("BrandBlueBrush");
                IconGlyph.Text = "\uE8FD"; // Help
                break;
            default:
                IconChrome.Background = (Brush)FindResource("ChipIdleBrush");
                IconGlyph.Foreground = (Brush)FindResource("BrandBlueBrush");
                IconGlyph.Text = "\uE946"; // Info
                break;
        }
    }

    private void ApplyButtons(AppDialogButtons buttons, string? primaryText, string? secondaryText)
    {
        switch (buttons)
        {
            case AppDialogButtons.Ok:
                SecondaryButton.Visibility = Visibility.Collapsed;
                PrimaryButton.Content = string.IsNullOrWhiteSpace(primaryText) ? "OK" : primaryText;
                break;
            case AppDialogButtons.YesNo:
                SecondaryButton.Visibility = Visibility.Visible;
                SecondaryButton.Content = string.IsNullOrWhiteSpace(secondaryText) ? "No" : secondaryText;
                PrimaryButton.Content = string.IsNullOrWhiteSpace(primaryText) ? "Yes" : primaryText;
                break;
            default:
                SecondaryButton.Visibility = Visibility.Visible;
                SecondaryButton.Content = string.IsNullOrWhiteSpace(secondaryText) ? "Cancel" : secondaryText;
                PrimaryButton.Content = string.IsNullOrWhiteSpace(primaryText) ? "OK" : primaryText;
                if (string.Equals(PrimaryButton.Content as string, "Delete", StringComparison.OrdinalIgnoreCase))
                    PrimaryButton.Background = (Brush)FindResource("DangerBrush");
                break;
        }
    }

    private void Primary_Click(object sender, RoutedEventArgs e)
    {
        _accepted = true;
        DialogResult = true;
        Close();
    }

    private void Secondary_Click(object sender, RoutedEventArgs e)
    {
        _accepted = false;
        DialogResult = false;
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (DialogResult is null)
            DialogResult = _accepted;
        base.OnClosing(e);
    }
}
