using Authenticator.Logging;
using Windows.Security.Credentials.UI;

namespace Authenticator.Services;

/// <summary>
/// Windows Hello consent gate for unlock. Key material stays in lock-credentials.bin (DPAPI);
/// Hello only proves user presence before the Hello KEK may unwrap the DEK.
/// </summary>
public sealed class WindowsHelloUnlockService
{
    public async Task<bool> IsAvailableAsync()
    {
        try
        {
            var availability = await UserConsentVerifier.CheckAvailabilityAsync();
            return availability == UserConsentVerifierAvailability.Available;
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Windows Hello availability check failed.", nameof(WindowsHelloUnlockService));
            return false;
        }
    }

    public async Task<bool> RequestConsentAsync(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        try
        {
            var availability = await UserConsentVerifier.CheckAvailabilityAsync();
            if (availability != UserConsentVerifierAvailability.Available)
                return false;

            var result = await UserConsentVerifier.RequestVerificationAsync(message);
            LoggingConfiguration.LogInfo($"Windows Hello verification result={result}.", nameof(WindowsHelloUnlockService));
            return result == UserConsentVerificationResult.Verified;
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Windows Hello verification failed.", nameof(WindowsHelloUnlockService));
            return false;
        }
    }
}
