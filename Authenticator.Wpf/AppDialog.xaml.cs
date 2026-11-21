using System.Windows;
using System.Windows.Automation;
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
    YesNo,
    /// <summary>Primary / Secondary / Cancel (Escape or X = Cancel).</summary>
    ThreeWay
}

public enum AppDialogChoice
{
    Cancel,
    Secondary,
    Primary
}

public partial class AppDialog : Window
{
    private bool _accepted;
    private AppDialogChoice _choice = AppDialogChoice.Cancel;
    private readonly AppDialogButtons _buttons;

    private AppDialog(
        string title,
        string message,
        AppDialogKind kind,
        AppDialogButtons buttons,
        string? primaryText,
        string? secondaryText,
        string? cancelText = null)
    {
        InitializeComponent();
        _buttons = buttons;
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        ApplyKind(kind);
        ApplyButtons(buttons, primaryText, secondaryText, cancelText);
    }

    public static void Alert(
        Window? owner,
        string message,
        string title = "Authenticator",
        AppDialogKind kind = AppDialogKind.Info)
    {
        var dialog = new AppDialog(title, message, kind, AppDialogButtons.Ok, null, null);
        AssignOwner(dialog, owner);
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
            cancelText);
        AssignOwner(dialog, owner);
        return dialog.ShowDialog() == true;
    }

    public static bool YesNo(
        Window? owner,
        string message,
        string title = "Confirm",
        AppDialogKind kind = AppDialogKind.Confirm)
    {
        var dialog = new AppDialog(title, message, kind, AppDialogButtons.YesNo, "Yes", "No");
        AssignOwner(dialog, owner);
        return dialog.ShowDialog() == true;
    }

    /// <summary>
    /// Three-way confirm. Primary / Secondary are both affirmative choices;
    /// Cancel (button, Escape, or window X) aborts without applying.
    /// </summary>
    public static AppDialogChoice Choose(
        Window? owner,
        string message,
        string title,
        string primaryText,
        string secondaryText,
        string cancelText = "Cancel",
        AppDialogKind kind = AppDialogKind.Confirm)
    {
        var dialog = new AppDialog(
            title,
            message,
            kind,
            AppDialogButtons.ThreeWay,
            primaryText,
            secondaryText,
            cancelText);
        AssignOwner(dialog, owner);
        dialog.ShowDialog();
        return dialog._choice;
    }

    /// <summary>
    /// WPF throws if Owner is a window that has already closed (e.g. connect dialog
    /// Cancel while an async catch still tries to Alert).
    /// </summary>
    private static void AssignOwner(Window dialog, Window? preferred)
    {
        var owner = PreferLiveOwner(preferred) ?? PreferLiveOwner(Application.Current?.MainWindow);
        if (owner is not null)
            dialog.Owner = owner;
    }

    private static Window? PreferLiveOwner(Window? window)
    {
        if (window is null)
            return null;
        try
        {
            // IsLoaded is false after Close(); accessing properties on a closed HWND is unsafe.
            return window.IsLoaded ? window : null;
        }
        catch
        {
            return null;
        }
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

    private void ApplyButtons(
        AppDialogButtons buttons,
        string? primaryText,
        string? secondaryText,
        string? cancelText)
    {
        CancelButton.Visibility = Visibility.Collapsed;
        SecondaryButton.IsCancel = true;

        switch (buttons)
        {
            case AppDialogButtons.Ok:
                SecondaryButton.Visibility = Visibility.Collapsed;
                PrimaryButton.Content = string.IsNullOrWhiteSpace(primaryText) ? "OK" : primaryText;
                break;
            case AppDialogButtons.YesNo:
                SecondaryButton.Visibility = Visibility.Visible;
                SecondaryButton.Content = string.IsNullOrWhiteSpace(secondaryText) ? "No" : secondaryText;
                SecondaryButton.SetValue(AutomationProperties.NameProperty, SecondaryButton.Content);
                PrimaryButton.Content = string.IsNullOrWhiteSpace(primaryText) ? "Yes" : primaryText;
                break;
            case AppDialogButtons.ThreeWay:
                CancelButton.Visibility = Visibility.Visible;
                CancelButton.Content = string.IsNullOrWhiteSpace(cancelText) ? "Cancel" : cancelText;
                CancelButton.IsCancel = true;
                SecondaryButton.IsCancel = false;
                SecondaryButton.Visibility = Visibility.Visible;
                SecondaryButton.Content = string.IsNullOrWhiteSpace(secondaryText) ? "No" : secondaryText;
                SecondaryButton.SetValue(AutomationProperties.NameProperty, SecondaryButton.Content);
                PrimaryButton.Content = string.IsNullOrWhiteSpace(primaryText) ? "Yes" : primaryText;
                PrimaryButton.SetValue(AutomationProperties.NameProperty, PrimaryButton.Content);
                break;
            default:
                SecondaryButton.Visibility = Visibility.Visible;
                SecondaryButton.Content = string.IsNullOrWhiteSpace(secondaryText) ? "Cancel" : secondaryText;
                SecondaryButton.SetValue(AutomationProperties.NameProperty, SecondaryButton.Content);
                PrimaryButton.Content = string.IsNullOrWhiteSpace(primaryText) ? "OK" : primaryText;
                if (string.Equals(PrimaryButton.Content as string, "Delete", StringComparison.OrdinalIgnoreCase))
                    PrimaryButton.Background = (Brush)FindResource("DangerBrush");
                break;
        }

        PrimaryButton.SetValue(AutomationProperties.NameProperty, PrimaryButton.Content);
    }

    private void Primary_Click(object sender, RoutedEventArgs e)
    {
        _choice = AppDialogChoice.Primary;
        _accepted = true;
        DialogResult = true;
        Close();
    }

    private void Secondary_Click(object sender, RoutedEventArgs e)
    {
        _choice = AppDialogChoice.Secondary;
        _accepted = false;
        DialogResult = false;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _choice = AppDialogChoice.Cancel;
        _accepted = false;
        DialogResult = false;
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_buttons == AppDialogButtons.ThreeWay)
        {
            // Window X / Alt+F4 with no button click must abort, not pick Secondary.
            if (DialogResult is null)
            {
                _choice = AppDialogChoice.Cancel;
                DialogResult = false;
            }
        }
        else if (DialogResult is null)
        {
            DialogResult = _accepted;
        }

        base.OnClosing(e);
    }
}
