using System;
using System.Collections.Generic;
using Authenticator.Logging;
using Authenticator.Views;
<<<<<<< HEAD
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;
=======
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
>>>>>>> a7b6a5e5758ce5dab7ed62ef23014fd0e6a388a1

namespace Authenticator;

public sealed partial class MainWindow : Window
{
<<<<<<< HEAD
    private const int DefaultWindowWidth = 778;
    private const int DefaultWindowHeight = 1490;
    private const double NavigationItemHeight = 60;

=======
>>>>>>> a7b6a5e5758ce5dab7ed62ef23014fd0e6a388a1
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

<<<<<<< HEAD
        NavView.Loaded += NavView_Loaded;
        NavView.SizeChanged += NavView_SizeChanged;

        ResizeToDisplayIfNeeded();
=======
        AppWindow.Resize(new Windows.Graphics.SizeInt32(778, 1490));
>>>>>>> a7b6a5e5758ce5dab7ed62ef23014fd0e6a388a1
        NavigateToSection("TwoFactor", updateSelection: true);

        LoggingConfiguration.LogInfo("MainWindow initialization completed.", nameof(MainWindow));
    }

<<<<<<< HEAD
    private void ResizeToDisplayIfNeeded()
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
            var displayArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);

            if (displayArea is null)
            {
                AppWindow.Resize(new Windows.Graphics.SizeInt32(DefaultWindowWidth, DefaultWindowHeight));
                return;
            }

            var maxTargetHeight = (int)Math.Round(displayArea.WorkArea.Height * (2d / 3d));
            if (DefaultWindowHeight <= maxTargetHeight)
            {
                AppWindow.Resize(new Windows.Graphics.SizeInt32(DefaultWindowWidth, DefaultWindowHeight));
                return;
            }

            var scaleFactor = (double)maxTargetHeight / DefaultWindowHeight;
            var scaledWidth = (int)Math.Round(DefaultWindowWidth * scaleFactor);
            var scaledHeight = (int)Math.Round(DefaultWindowHeight * scaleFactor);

            AppWindow.Resize(new Windows.Graphics.SizeInt32(scaledWidth, scaledHeight));
            LoggingConfiguration.LogInfo($"MainWindow resized for display. WorkAreaHeight={displayArea.WorkArea.Height}, TargetHeight={maxTargetHeight}, FinalSize={scaledWidth}x{scaledHeight}.", nameof(MainWindow));
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Failed to resize MainWindow based on display. Falling back to default size.", nameof(MainWindow));
            AppWindow.Resize(new Windows.Graphics.SizeInt32(DefaultWindowWidth, DefaultWindowHeight));
        }
    }

    private void NavView_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateNavigationLayout();
    }

    private void NavView_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateNavigationLayout();
    }

    private void UpdateNavigationLayout()
    {
        if (NavView.MenuItems.Count == 0 || NavView.ActualWidth <= 0)
        {
            return;
        }

        const double reservedWidthForOverflowAndPadding = 96;
        var usableWidth = Math.Max(0, NavView.ActualWidth - reservedWidthForOverflowAndPadding);
        var itemWidth = usableWidth / NavView.MenuItems.Count;

        foreach (var item in NavView.MenuItems)
        {
            if (item is not NavigationViewItem navigationItem)
            {
                continue;
            }

            navigationItem.Width = itemWidth;
            navigationItem.Height = NavigationItemHeight;
            navigationItem.HorizontalAlignment = HorizontalAlignment.Stretch;
        }
    }

=======
>>>>>>> a7b6a5e5758ce5dab7ed62ef23014fd0e6a388a1
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