using OtpNet;

namespace Authenticator.Crypto;

/// <summary>
/// Client-side TOTP generation from a plaintext Base32 secret.
/// </summary>
public static class TotpGenerator
{
    /// <summary>
    /// Minimum seconds left in the current TOTP window before a login automation
    /// should accept the code (fill + submit needs headroom before the server checks).
    /// </summary>
    public const int LoginMinRemainingSeconds = 10;

    /// <summary>
    /// Seconds remaining in the current TOTP step window (1..period).
    /// </summary>
    public static double GetSecondsRemaining(int period)
    {
        if (period <= 0)
            period = 30;

        var nowSeconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        var remaining = period - (nowSeconds % period);
        return remaining <= 0 ? period : remaining;
    }

    /// <summary>
    /// Delay before generating when the current window has less than
    /// <paramref name="minRemainingSeconds"/> left. Waits into the next window.
    /// </summary>
    public static int GetDelayMsUntilUsableWindow(int period, int minRemainingSeconds)
    {
        if (minRemainingSeconds <= 0)
            return 0;
        if (period <= 0)
            period = 30;
        if (minRemainingSeconds > period)
            minRemainingSeconds = period;

        var remaining = GetSecondsRemaining(period);
        if (remaining >= minRemainingSeconds)
            return 0;

        // Cross into the next window, plus a small buffer for clock/edge jitter.
        return (int)Math.Ceiling(remaining * 1000.0) + 75;
    }

    public static string? TryGenerate(string? secretKey, string? algorithm, int digits, int period)
    {
        if (string.IsNullOrWhiteSpace(secretKey))
            return null;

        try
        {
            if (digits <= 0)
                digits = 6;
            if (period <= 0)
                period = 30;

            var secret = NormalizeSecret(secretKey);
            var totp = new Totp(secret, step: period, mode: ParseMode(algorithm), totpSize: digits);
            var code = totp.ComputeTotp();
            return string.IsNullOrEmpty(code) ? null : code;
        }
        catch
        {
            return null;
        }
    }

    private static byte[] NormalizeSecret(string secretKey)
    {
        var normalized = secretKey.Replace(" ", string.Empty, StringComparison.Ordinal)
                                  .Replace("-", string.Empty, StringComparison.Ordinal)
                                  .ToUpperInvariant();
        return Base32Encoding.ToBytes(normalized);
    }

    private static OtpHashMode ParseMode(string? algorithm)
    {
        return algorithm?.ToUpperInvariant() switch
        {
            "SHA256" => OtpHashMode.Sha256,
            "SHA512" => OtpHashMode.Sha512,
            _ => OtpHashMode.Sha1
        };
    }
}
