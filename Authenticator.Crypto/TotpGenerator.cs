using OtpNet;

namespace Authenticator.Crypto;

/// <summary>
/// Client-side TOTP generation from a plaintext Base32 secret.
/// </summary>
public static class TotpGenerator
{
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
