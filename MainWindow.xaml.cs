using System;
using System.Collections.Generic;
using Authenticator.Logging;
using Authenticator.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Authenticator;

public sealed partial class MainWindow : Window
{
    private readonly Dictionary<string, ShellSection> _shellSections = new(StringComparer.Ordinal)
    {
        ["TwoFactor"] = new(typeof(TwoFactorPage), "2FA", "Manage and organize your authenticator accounts."),
        ["Backup"] = new(typeof(BackupPage), "Backup", "Review backup codes and recovery information."),
        ["Settings"] = new(typeof(SettingsPage), "Settings", "Manage storage, security, and maintenance behavior.")
    };

    public MainWindow()
    {
        LoggingConfiguration.LogInfo("MainWindow initialization started.", nameof(MainWindow));

        InitializeComponent();

        AppWindow.Resize(new Windows.Graphics.SizeInt32(778, 1490));
        NavigateToSection("TwoFactor", updateSelection: true);

        LoggingConfiguration.LogInfo("MainWindow initialization completed.", nameof(MainWindow));
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is not string tag)
        {
            return;
        }

        LoggingConfiguration.LogDebug($"Navigation selection changed: {tag}.", nameof(MainWindow));
        NavigateToSection(tag, updateSelection: false);
    }

    private void NavigateMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag })
        {
            NavigateToSection(tag, updateSelection: true);
        }
    }

    private void NavigateToSection(string tag, bool updateSelection)
    {
        if (!_shellSections.TryGetValue(tag, out var section))
        {
            LoggingConfiguration.LogWarning($"Unhandled navigation tag: {tag}.", nameof(MainWindow));
            return;
        }

        if (SimpleContentFrame.Content?.GetType() != section.PageType)
        {
            SimpleContentFrame.Navigate(section.PageType);
        }

        UpdateContext(section);

        if (updateSelection)
        {
            SelectNavigationViewItem(tag);
        }

        LoggingConfiguration.LogInfo($"Navigated to {section.Title} section.", nameof(MainWindow));
    }

    private void SelectNavigationViewItem(string tag)
    {
        foreach (var item in NavView.MenuItems)
        {
            if (item is NavigationViewItem navigationItem && string.Equals(navigationItem.Tag as string, tag, StringComparison.Ordinal))
            {
                NavView.SelectedItem = navigationItem;
                return;
            }
        }
    }

    private void UpdateContext(ShellSection section)
    {
    }

    private sealed record ShellSection(Type PageType, string Title, string Description);
}