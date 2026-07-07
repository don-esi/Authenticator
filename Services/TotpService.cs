using System;
using System.Security.Cryptography;
using Authenticator.Data.Models;
using Authenticator.Logging;
using OtpNet;

namespace Authenticator.Services;

/// <summary>
/// Service for generating TOTP codes.
/// </summary>
public class TotpService
{
    /// <summary>
    /// Generates a TOTP code for the given account configuration.
    /// </summary>
    public static string GenerateCode(Account account)
    {
        ArgumentNullException.ThrowIfNull(account);

        try
        {
            var totp = CreateTotp(account, account.Digits);
            return totp.ComputeTotp();
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, $"Failed to generate TOTP code for account id {account.Id}.", nameof(TotpService));
            throw;
        }
    }

    /// <summary>
    /// Generates a TOTP code with custom digits configuration.
    /// </summary>
    public string GenerateCode(Account account, int digits)
    {
        ArgumentNullException.ThrowIfNull(account);

        try
        {
            var totp = CreateTotp(account, digits);
            return totp.ComputeTotp();
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, $"Failed to generate TOTP code for account id {account.Id} with digits={digits}.", nameof(TotpService));
            throw;
        }
    }

    /// <summary>
    /// Gets the number of seconds remaining until the next code regeneration.
    /// </summary>
    public int GetSecondsRemaining(Account account)
    {
        ArgumentNullException.ThrowIfNull(account);
        var remaining = account.Period - (int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() % account.Period);
        return remaining == 0 ? account.Period : remaining;
    }

    /// <summary>
    /// Gets the progress (0.0 to 1.0) through the current time period.
    /// </summary>
    public double GetProgress(Account account)
    {
        ArgumentNullException.ThrowIfNull(account);
        var secondsRemaining = GetSecondsRemaining(account);
        return 1.0 - ((double)secondsRemaining / account.Period);
    }

    /// <summary>
    /// Validates if a TOTP code is correct for the account.
    /// </summary>
    public bool ValidateCode(Account account, string code)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        try
        {
            var totp = CreateTotp(account, account.Digits);
            var isValid = totp.VerifyTotp(code, out _, new VerificationWindow(previous: 1, future: 1));
            LoggingConfiguration.LogDebug($"Validated TOTP code for account id {account.Id}. Result={isValid}.", nameof(TotpService));
            return isValid;
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, $"Failed to validate TOTP code for account id {account.Id}.", nameof(TotpService));
            throw;
        }
    }

    /// <summary>
    /// Generates an otpauth provisioning URI for QR code creation.
    /// </summary>
    public string GenerateSetupCode(string issuer, string accountName, string secretKey,
        string algorithm = "SHA1", int digits = 6, int period = 30)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(issuer);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretKey);

        LoggingConfiguration.LogDebug($"Generating setup URI. Issuer={issuer}, Account={accountName}, Algorithm={algorithm}, Digits={digits}, Period={period}.", nameof(TotpService));

        return $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(accountName)}?secret={Uri.EscapeDataString(secretKey)}&issuer={Uri.EscapeDataString(issuer)}&algorithm={Uri.EscapeDataString(algorithm)}&digits={digits}&period={period}";
    }

    private static Totp CreateTotp(Account account, int digits)
    {
        var secret = NormalizeSecret(account.SecretKey);
        return new Totp(secret, step: account.Period, mode: ParseMode(account.Algorithm), totpSize: digits);
    }

    private static byte[] NormalizeSecret(string secretKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretKey);
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
