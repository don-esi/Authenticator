using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Threading.Tasks;
using Authenticator.Logging;
using ZXing;

namespace Authenticator.Services;

/// <summary>
/// Service for QR code scanning and otpauth:// URI parsing
/// </summary>
public class QrCodeService
{
    /// <summary>
    /// Decodes a QR code from a bitmap image into an otpauth://totp account.
    /// </summary>
    public QrCodeResult? DecodeQrCode(Bitmap bitmap)
    {
        var text = DecodeQrText(bitmap);
        return text is null ? null : ParseOtpAuthUri(text);
    }

    /// <summary>
    /// Decodes raw QR text from a bitmap (otpauth, otpauth-migration, etc.).
    /// </summary>
    public string? DecodeQrText(Bitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        LoggingConfiguration.LogDebug($"Decoding QR code from bitmap {bitmap.Width}x{bitmap.Height}.", nameof(QrCodeService));

        try
        {
            var reader = new BarcodeReaderGeneric
            {
                AutoRotate = true,
                Options = new ZXing.Common.DecodingOptions
                {
                    TryHarder = true,
                    PossibleFormats = new[] { BarcodeFormat.QR_CODE }
                }
            };

            var luminanceSource = new RGBLuminanceSource(ToPixelData(bitmap), bitmap.Width, bitmap.Height, RGBLuminanceSource.BitmapFormat.BGRA32);
            var result = reader.Decode(luminanceSource);
            if (result != null && !string.IsNullOrWhiteSpace(result.Text))
            {
                LoggingConfiguration.LogInfo("QR code decoded successfully.", nameof(QrCodeService));
                return result.Text;
            }
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "QR decode failed.", nameof(QrCodeService));
        }

        LoggingConfiguration.LogWarning("No valid QR code content was decoded.", nameof(QrCodeService));
        return null;
    }

    /// <summary>
    /// Parses an otpauth:// URI into account details
    /// Format: otpauth://totp/Issuer:Account?secret=SECRET&issuer=Issuer&algorithm=SHA1&digits=6&period=30
    /// </summary>
    public QrCodeResult? ParseOtpAuthUri(string uri)
    {
        if (string.IsNullOrWhiteSpace(uri) || !uri.StartsWith("otpauth://totp/", StringComparison.OrdinalIgnoreCase))
        {
            LoggingConfiguration.LogWarning("Invalid OTPAuth URI format.", nameof(QrCodeService));
            return null;
        }

        try
        {
            var uriObj = new Uri(uri);
            var path = Uri.UnescapeDataString(uriObj.AbsolutePath.TrimStart('/'));

            // Parse issuer and account from path
            var issuer = string.Empty;
            var account = path;

            if (path.Contains(':'))
            {
                var parts = path.Split(':', 2);
                issuer = parts[0];
                account = parts[1];
            }

            // Parse query parameters
            var query = System.Web.HttpUtility.ParseQueryString(uriObj.Query);

            var secret = query["secret"];
            if (string.IsNullOrWhiteSpace(secret))
            {
                LoggingConfiguration.LogWarning("OTPAuth URI is missing required secret parameter.", nameof(QrCodeService));
                return null;
            }

            // Override issuer from query if present
            if (!string.IsNullOrWhiteSpace(query["issuer"]))
                issuer = query["issuer"];

            var algorithm = query["algorithm"] ?? "SHA1";
            var digitsStr = query["digits"] ?? "6";
            var periodStr = query["period"] ?? "30";

            _ = int.TryParse(digitsStr, out var digits);
            _ = int.TryParse(periodStr, out var period);

            if (digits == 0) digits = 6;
            if (period == 0) period = 30;

            var qrCodeResult = new QrCodeResult
            {
                Issuer = issuer,
                Account = account,
                Secret = secret,
                Algorithm = algorithm.ToUpperInvariant(),
                Digits = digits,
                Period = period
            };

            LoggingConfiguration.LogInfo($"OTPAuth URI parsed. Issuer='{qrCodeResult.Issuer}', Account='{qrCodeResult.Account}', Algorithm='{qrCodeResult.Algorithm}', Digits={qrCodeResult.Digits}, Period={qrCodeResult.Period}.", nameof(QrCodeService));
            return qrCodeResult;
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Failed to parse OTPAuth URI.", nameof(QrCodeService));
            return null;
        }
    }

    /// <summary>
    /// Captures a screenshot of the specified screen region.
    /// </summary>
    public Task<Bitmap?> CaptureScreenRegionAsync(int x, int y, int width, int height)
    {
        LoggingConfiguration.LogInfo($"CaptureScreenRegionAsync requested for X={x}, Y={y}, Width={width}, Height={height}.", nameof(QrCodeService));

        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "Capture width must be greater than zero.");
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), height, "Capture height must be greater than zero.");
        }

        try
        {
            var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(x, y, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
            LoggingConfiguration.LogInfo("Screen region captured successfully.", nameof(QrCodeService));
            return Task.FromResult<Bitmap?>(bitmap);
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Screen capture failed.", nameof(QrCodeService));
            return Task.FromResult<Bitmap?>(null);
        }
    }

    private static byte[] ToPixelData(Bitmap bitmap)
    {
        var rectangle = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var bitmapData = bitmap.LockBits(rectangle, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

        try
        {
            var bytes = Math.Abs(bitmapData.Stride) * bitmap.Height;
            var pixelData = new byte[bytes];
            System.Runtime.InteropServices.Marshal.Copy(bitmapData.Scan0, pixelData, 0, bytes);
            return pixelData;
        }
        finally
        {
            bitmap.UnlockBits(bitmapData);
        }
    }
}

/// <summary>
/// Result of QR code scanning and parsing
/// </summary>
public class QrCodeResult
{
    public string? Issuer { get; set; } = string.Empty;
    public string Account { get; set; } = string.Empty;
    public string Secret { get; set; } = string.Empty;
    public string Algorithm { get; set; } = "SHA1";
    public int Digits { get; set; } = 6;
    public int Period { get; set; } = 30;
}