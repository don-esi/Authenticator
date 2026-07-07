using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Authenticator.Controls;
using Authenticator.Data.Models;
using Authenticator.Logging;
using Authenticator.Services;
using Authenticator.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;

namespace Authenticator.Views;

public sealed partial class TwoFactorPage : Page
{
    private static readonly IReadOnlyList<FolderColorOption> FolderColorOptions =
    [
        new("Teal", "#4ECDC4"),
        new("Mint", "#A8E6CF"),
        new("Blue", "#A0C4FF"),
        new("Coral", "#FF8B94"),
        new("Orange", "#FFD3B6"),
        new("Purple", "#BDB2FF")
    ];

    private readonly DatabaseService _databaseService = new();

    public TwoFactorViewModel ViewModel
    {
        get;
    }

    public TwoFactorPage()
    {
        LoggingConfiguration.LogInfo("TwoFactorPage initialization started.", nameof(TwoFactorPage));

        InitializeComponent();

        var dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        ViewModel = new TwoFactorViewModel(
            _databaseService,
            new TotpService(),
            dispatcherQueue);

        Unloaded += TwoFactorPage_Unloaded;

        LoggingConfiguration.LogInfo("TwoFactorPage initialization completed.", nameof(TwoFactorPage));
    }

    private async Task ReloadFoldersAsync()
    {
        await ViewModel.LoadAccountsAsync();
    }

    private async Task<(ContentDialogResult Result, string Name, FolderColorOption Color)> ShowFolderEditorDialogAsync(
        string title,
        string primaryButtonText,
        string description,
        string? initialName = null,
        string? initialColor = null)
    {
        var nameTextBox = new TextBox
        {
            PlaceholderText = "Folder name",
            Text = initialName ?? string.Empty
        };

        var selectedIndex = 0;
        if (!string.IsNullOrWhiteSpace(initialColor))
        {
            var matchedIndex = FolderColorOptions
                .Select((option, index) => new { option.HexColor, index })
                .FirstOrDefault(option => string.Equals(option.HexColor, initialColor, StringComparison.OrdinalIgnoreCase))?.index;

            if (matchedIndex is int index)
            {
                selectedIndex = index;
            }
        }

        var colorComboBox = new ComboBox
        {
            ItemsSource = FolderColorOptions,
            DisplayMemberPath = nameof(FolderColorOption.Name),
            SelectedIndex = selectedIndex,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var content = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = description },
                nameTextBox,
                colorComboBox
            }
        };

        var dialog = new ContentDialog
        {
            Title = title,
            PrimaryButtonText = primaryButtonText,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            Content = content,
            XamlRoot = XamlRoot
        };

        var result = await dialog.ShowAsync();
        var folderName = nameTextBox.Text?.Trim() ?? string.Empty;
        var selectedColor = colorComboBox.SelectedItem as FolderColorOption ?? FolderColorOptions[0];
        return (result, folderName, selectedColor);
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.WrapWholeWords },
            CloseButtonText = "OK",
            XamlRoot = XamlRoot
        };

        await dialog.ShowAsync();
    }

    private async Task<bool> ConfirmAsync(string title, string message, string primaryButtonText)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.WrapWholeWords },
            PrimaryButtonText = primaryButtonText,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private sealed record FolderColorOption(string Name, string HexColor);

    protected async override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        LoggingConfiguration.LogInfo("Navigated to TwoFactorPage.", nameof(TwoFactorPage));

        try
        {
            await ViewModel.LoadAccountsAsync();
            ViewModel.StartTimer();
            LoggingConfiguration.LogInfo("TwoFactorPage data loaded and timer started.", nameof(TwoFactorPage));
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Failed to load TwoFactorPage data.", nameof(TwoFactorPage));
            throw;
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel.StopTimer();
        LoggingConfiguration.LogInfo("Navigated away from TwoFactorPage; timer stopped.", nameof(TwoFactorPage));
    }

    private void FolderChip_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is Controls.FolderChip chip && chip.FolderData != null)
        {
            LoggingConfiguration.LogDebug($"Folder selected from chip: {chip.FolderData.Name} (Id={chip.FolderData.Id}).", nameof(TwoFactorPage));
            ViewModel.SelectFolderCommand.Execute(chip.FolderData);
        }
    }

    private void TwoFactorPage_Unloaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        ViewModel.StopTimer();
        LoggingConfiguration.LogDebug("TwoFactorPage unloaded; timer stopped.", nameof(TwoFactorPage));
    }

    private void AddAccountButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        LoggingConfiguration.LogInfo("Navigating to AddAccountPage from TwoFactorPage.", nameof(TwoFactorPage));
        Frame.Navigate(typeof(AddAccountPage));
    }

    private async void CreateFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var (result, folderName, selectedColor) = await ShowFolderEditorDialogAsync(
            "New Folder",
            "Create",
            "Create a folder for organizing accounts.");

        if (result != ContentDialogResult.Primary)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(folderName))
        {
            LoggingConfiguration.LogWarning("Folder creation canceled because the folder name was empty.", nameof(TwoFactorPage));
            await ShowMessageAsync("Folder name required", "Enter a name before creating a folder.");
            return;
        }

        await _databaseService.AddFolderAsync(new Folder
        {
            Name = folderName,
            Color = selectedColor.HexColor
        });

        await ReloadFoldersAsync();
        LoggingConfiguration.LogInfo($"Folder '{folderName}' created from TwoFactorPage.", nameof(TwoFactorPage));
    }

    private async void RenameFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedFolder is null)
        {
            LoggingConfiguration.LogWarning("Rename requested without a selected folder.", nameof(TwoFactorPage));
            return;
        }

        var selectedFolder = ViewModel.SelectedFolder;
        var (result, folderName, selectedColor) = await ShowFolderEditorDialogAsync(
            "Rename Folder",
            "Save",
            "Update the selected folder.",
            selectedFolder.Name,
            selectedFolder.Color);

        if (result != ContentDialogResult.Primary)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(folderName))
        {
            LoggingConfiguration.LogWarning($"Folder rename canceled because the folder name was empty for folder id {selectedFolder.Id}.", nameof(TwoFactorPage));
            await ShowMessageAsync("Folder name required", "Enter a name before saving the folder.");
            return;
        }

        selectedFolder.Name = folderName;
        selectedFolder.Color = selectedColor.HexColor;
        await _databaseService.UpdateFolderAsync(selectedFolder);
        await ReloadFoldersAsync();
        ViewModel.SelectFolderCommand.Execute(ViewModel.Folders.FirstOrDefault(folder => folder.Id == selectedFolder.Id));
        LoggingConfiguration.LogInfo($"Folder id {selectedFolder.Id} renamed to '{folderName}' from TwoFactorPage.", nameof(TwoFactorPage));
    }

    private async void DeleteFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedFolder is null)
        {
            LoggingConfiguration.LogWarning("Delete requested without a selected folder.", nameof(TwoFactorPage));
            return;
        }

        var selectedFolder = ViewModel.SelectedFolder;
        var accountCount = ViewModel.Accounts.Count(account => account.Account.FolderId == selectedFolder.Id);
        var message = accountCount > 0
            ? $"Delete '{selectedFolder.Name}'? {accountCount} account(s) will remain saved and be moved out of the folder."
            : $"Delete '{selectedFolder.Name}'?";

        if (!await ConfirmAsync("Delete Folder", message, "Delete"))
        {
            return;
        }

        await _databaseService.DeleteFolderAsync(selectedFolder.Id);
        ViewModel.ClearFolderFilterCommand.Execute(null);
        await ReloadFoldersAsync();
        LoggingConfiguration.LogInfo($"Folder id {selectedFolder.Id} deleted from TwoFactorPage.", nameof(TwoFactorPage));
    }

    private void AccountCard_CopyRequested(object sender, AccountCardActionEventArgs e)
    {
        var dataPackage = new DataPackage();
        dataPackage.SetText(e.CurrentCode);
        Clipboard.SetContent(dataPackage);
        LoggingConfiguration.LogDebug($"Copied TOTP code from AccountCard for account id {e.Account.Id}.", nameof(TwoFactorPage));
    }

    private void AccountCard_MoreRequested(object sender, AccountCardActionEventArgs e)
    {
        LoggingConfiguration.LogInfo($"Navigating to BackupPage for account id {e.Account.Id} from TwoFactorPage.", nameof(TwoFactorPage));
        Frame.Navigate(typeof(BackupPage), e.Account.Id);
    }
}
