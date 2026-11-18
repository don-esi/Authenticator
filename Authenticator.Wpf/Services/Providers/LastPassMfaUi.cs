using OneOf;
using PasswordManager.Common;
using PasswordManager.Duo;
using PasswordManager.LastPass.Ui;

namespace Authenticator.Services.Providers;

/// <summary>WPF host that can prompt for LastPass (and similar) MFA codes on the UI thread.</summary>
internal interface IProviderMfaHost
{
    Task<OneOf<Otp, MfaMethod, Canceled>> PromptOtpAsync(
        string prompt,
        int attempt,
        MfaMethod[] otherMethods,
        bool yubiKey,
        CancellationToken cancellationToken);

    Task<OneOf<Otp, WaitForOutOfBand, MfaMethod, Canceled>> PromptLastPassAuthAsync(
        int attempt,
        MfaMethod[] otherMethods,
        CancellationToken cancellationToken);
}

/// <summary>Interactive LastPass MFA — mirrors WealthForge <c>LastPassUnlockUi</c>.</summary>
internal sealed class LastPassInteractiveUi : IAsyncUi
{
    private readonly IProviderMfaHost _host;

    public LastPassInteractiveUi(IProviderMfaHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    public Task<OneOf<Otp, MfaMethod, Canceled>> ProvideGoogleAuthPasscode(
        int attempt,
        MfaMethod[] otherMethods,
        CancellationToken cancellationToken)
    {
        if (PreferYubiKey(otherMethods))
            return Task.FromResult<OneOf<Otp, MfaMethod, Canceled>>(MfaMethod.YubikeyOtp);

        return _host.PromptOtpAsync(
            "Enter Google Authenticator code",
            attempt,
            otherMethods,
            yubiKey: false,
            cancellationToken);
    }

    public Task<OneOf<Otp, MfaMethod, Canceled>> ProvideMicrosoftAuthPasscode(
        int attempt,
        MfaMethod[] otherMethods,
        CancellationToken cancellationToken)
    {
        if (PreferYubiKey(otherMethods))
            return Task.FromResult<OneOf<Otp, MfaMethod, Canceled>>(MfaMethod.YubikeyOtp);

        return _host.PromptOtpAsync(
            "Enter Microsoft Authenticator code",
            attempt,
            otherMethods,
            yubiKey: false,
            cancellationToken);
    }

    public Task<OneOf<Otp, MfaMethod, Canceled>> ProvideYubikeyPasscode(
        int attempt,
        MfaMethod[] otherMethods,
        CancellationToken cancellationToken) =>
        _host.PromptOtpAsync(
            "Touch your YubiKey",
            attempt,
            otherMethods,
            yubiKey: true,
            cancellationToken);

    public Task<OneOf<Otp, WaitForOutOfBand, MfaMethod, Canceled>> ApproveLastPassAuth(
        int attempt,
        MfaMethod[] otherMethods,
        CancellationToken cancellationToken)
    {
        if (PreferYubiKey(otherMethods))
            return Task.FromResult<OneOf<Otp, WaitForOutOfBand, MfaMethod, Canceled>>(MfaMethod.YubikeyOtp);

        return _host.PromptLastPassAuthAsync(attempt, otherMethods, cancellationToken);
    }

    public Task<OneOf<DuoChoice, MfaMethod, DuoCancelled>> ChooseDuoFactor(
        DuoDevice[] devices,
        MfaMethod[] otherMethods,
        CancellationToken cancellationToken)
    {
        if (PreferYubiKey(otherMethods))
            return Task.FromResult<OneOf<DuoChoice, MfaMethod, DuoCancelled>>(MfaMethod.YubikeyOtp);

        return Task.FromResult(IDuoAsyncUi.CancelChoice());
    }

    public Task<OneOf<DuoPasscode, DuoCancelled>> ProvideDuoPasscode(
        DuoDevice device,
        CancellationToken cancellationToken) =>
        Task.FromResult(IDuoAsyncUi.CancelPasscode());

    public Task DuoDone(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task UpdateDuoStatus(DuoStatus status, string text, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task<OneOf<string, Canceled>> PerformSsoLogin(
        string url,
        string expectedRedirectUrl,
        CancellationToken cancellationToken) =>
        Task.FromResult<OneOf<string, Canceled>>(new Canceled("SSO is not supported in this dialog"));

    private static bool PreferYubiKey(MfaMethod[] otherMethods) =>
        otherMethods.Contains(MfaMethod.YubikeyOtp);
}

/// <summary>Silent MFA — used for background reconnect when device trust should already exist.</summary>
internal sealed class LastPassSilentUi : IAsyncUi
{
    public static LastPassSilentUi Instance { get; } = new();

    public Task<OneOf<Otp, MfaMethod, Canceled>> ProvideGoogleAuthPasscode(
        int attempt, MfaMethod[] otherMethods, CancellationToken cancellationToken) =>
        CanceledOtp();

    public Task<OneOf<Otp, MfaMethod, Canceled>> ProvideMicrosoftAuthPasscode(
        int attempt, MfaMethod[] otherMethods, CancellationToken cancellationToken) =>
        CanceledOtp();

    public Task<OneOf<Otp, MfaMethod, Canceled>> ProvideYubikeyPasscode(
        int attempt, MfaMethod[] otherMethods, CancellationToken cancellationToken) =>
        CanceledOtp();

    public Task<OneOf<Otp, WaitForOutOfBand, MfaMethod, Canceled>> ApproveLastPassAuth(
        int attempt, MfaMethod[] otherMethods, CancellationToken cancellationToken) =>
        Task.FromResult<OneOf<Otp, WaitForOutOfBand, MfaMethod, Canceled>>(
            new Canceled("Authenticator silent UI"));

    public Task<OneOf<DuoChoice, MfaMethod, DuoCancelled>> ChooseDuoFactor(
        DuoDevice[] devices, MfaMethod[] otherMethods, CancellationToken cancellationToken) =>
        Task.FromResult(IDuoAsyncUi.CancelChoice());

    public Task<OneOf<DuoPasscode, DuoCancelled>> ProvideDuoPasscode(
        DuoDevice device, CancellationToken cancellationToken) =>
        Task.FromResult(IDuoAsyncUi.CancelPasscode());

    public Task DuoDone(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task UpdateDuoStatus(DuoStatus status, string text, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task<OneOf<string, Canceled>> PerformSsoLogin(
        string url, string expectedRedirectUrl, CancellationToken cancellationToken) =>
        Task.FromResult<OneOf<string, Canceled>>(new Canceled("Authenticator silent UI"));

    private static Task<OneOf<Otp, MfaMethod, Canceled>> CanceledOtp() =>
        Task.FromResult<OneOf<Otp, MfaMethod, Canceled>>(new Canceled("Authenticator silent UI"));
}
