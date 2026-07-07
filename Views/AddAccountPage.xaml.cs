using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using Authenticator.Logging;
using Authenticator.Services;
using Authenticator.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;

namespace Authenticator.Views;

public sealed partial class AddAccountPage : Page
{
    private readonly QrCodeService _qrCodeService = new();

    public AddAccountViewModel ViewModel { get; }

    public AddAccountPage()
    {
        LoggingConfiguration.LogInfo("AddAccountPage initialization started.", nameof(AddAccountPage));

        this.InitializeComponent();
        ViewModel = new AddAccountViewModel(new DatabaseService(), new TotpService());
        Loaded += AddAccountPage_Loaded;

        LoggingConfiguration.LogInfo("AddAccountPage initialization completed.", nameof(AddAccountPage));
    }

    private async void AddAccountPage_Loaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoadFoldersAsync();
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        LoggingConfiguration.LogInfo("Save account requested from AddAccountPage.", nameof(AddAccountPage));
        await ViewModel.SaveCommand.ExecuteAsync(null);

        if (Frame.CanGoBack)
        {
            LoggingConfiguration.LogDebug("Returning to previous page after save.", nameof(AddAccountPage));
            Frame.GoBack();
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        LoggingConfiguration.LogInfo("Add account canceled by user.", nameof(AddAccountPage));

        if (Frame.CanGoBack)
            Frame.GoBack();
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.WrapWholeWords
            },
            CloseButtonText = "OK",
            XamlRoot = XamlRoot
        };

        await dialog.ShowAsync();
    }

    private Task ApplyQrResultAsync(QrCodeResult? qrResult, string sourceDescription)
    {
        if (qrResult is null)
        {
            LoggingConfiguration.LogWarning($"No valid OTP QR payload found from {sourceDescription}.", nameof(AddAccountPage));
            return ShowMessageAsync("QR code not found", "No valid OTP QR code was detected. Try again with a clearer QR code.");
        }

        ViewModel.LoadFromQrResult(qrResult);
        LoggingConfiguration.LogInfo($"QR details loaded successfully from {sourceDescription}.", nameof(AddAccountPage));
        return Task.CompletedTask;
    }

    private async void ScanQrCodeButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            LoggingConfiguration.LogInfo("QR image import requested from AddAccountPage.", nameof(AddAccountPage));

            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".bmp");

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                LoggingConfiguration.LogDebug("QR image selection canceled.", nameof(AddAccountPage));
                return;
            }

            using var stream = await file.OpenAsync(Windows.Storage.FileAccessMode.Read);
            using var netStream = stream.AsStreamForRead();
            using var bitmap = new Bitmap(netStream);
            var qrResult = _qrCodeService.DecodeQrCode(bitmap);
            await ApplyQrResultAsync(qrResult, $"file '{file.Name}'");
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Failed to import account details from QR image.", nameof(AddAccountPage));
            await ShowMessageAsync("QR import failed", "The QR image could not be processed.");
        }
    }

    private async void ScanQrFromScreenButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            LoggingConfiguration.LogInfo("Screen QR scan requested from AddAccountPage.", nameof(AddAccountPage));

            await ShowMessageAsync(
                "Prepare screen scan",
                "After you select OK, switch to the window that shows the QR code. The app will capture your primary screen in 3 seconds.");

            await Task.Delay(TimeSpan.FromSeconds(3));

            const int captureX = 0;
            const int captureY = 0;
            const int captureWidth = 3840;
            const int captureHeight = 2160;

            using var bitmap = await _qrCodeService.CaptureScreenRegionAsync(captureX, captureY, captureWidth, captureHeight);
            if (bitmap is null)
            {
                await ShowMessageAsync("Screen capture failed", "The screen could not be captured for QR scanning.");
                return;
            }

            var qrResult = _qrCodeService.DecodeQrCode(bitmap);
            await ApplyQrResultAsync(qrResult, "the screen");
        }
        catch (ArgumentOutOfRangeException ex)
        {
            LoggingConfiguration.LogException(ex, "Invalid screen capture bounds for QR scan.", nameof(AddAccountPage));
            await ShowMessageAsync("Screen scan failed", "The screen capture bounds were invalid.");
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Failed to import account details from the screen QR scan.", nameof(AddAccountPage));
            await ShowMessageAsync("Screen scan failed", "The screen QR scan could not be completed.");
        }
    }
}
