using System.IO;
using Authenticator.Data.Models;
using Authenticator.Logging;
using OneOf;
using PasswordManager.Common;
using PasswordManager.Duo;
using PasswordManager.LastPass.Ui;

namespace Authenticator.Services.Providers;

internal abstract class MemoryVaultProvider : IVaultProvider
{
    private List<ProviderEntry> _entries = new();

    public abstract string ProviderId { get; }
    public abstract string DisplayName { get; }
    public abstract ProviderCapabilities Capabilities { get; }
    public bool IsConnected { get; protected set; }

    public abstract Task ConnectAsync(ProviderConnectRequest request, CancellationToken ct = default);

    public void Disconnect()
    {
        IsConnected = false;
        _entries = new();
    }

    public Task<IReadOnlyList<ProviderEntry>> ListEntriesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ProviderEntry>>(IsConnected ? _entries : Array.Empty<ProviderEntry>());

    protected void SetEntries(IEnumerable<ProviderEntry> entries)
    {
        _entries = entries.ToList();
        IsConnected = true;
    }

    protected static ProviderEntry MapLogin(
        string id,
        string name,
        string? user,
        string? pass,
        string? url,
        string? notes,
        string? totp = null,
        string? path = null,
        bool favorite = false) =>
        new()
        {
            ExternalId = id,
            Title = name,
            Username = user,
            Password = pass,
            Url = url,
            Notes = notes,
            Totp = totp,
            Path = path,
            IsFavorite = favorite,
            ItemType = VaultItemType.Login
        };
}

internal sealed class KeePassVaultProvider : MemoryVaultProvider
{
    public override string ProviderId => "KeePass";
    public override string DisplayName => "KeePass";
    public override ProviderCapabilities Capabilities { get; } = new()
    {
        RequiresFilePath = true,
        SupportsSecureNotes = true,
        SupportsFavorites = true,
        SupportsWriteBack = false
    };

    public override Task ConnectAsync(ProviderConnectRequest request, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.FilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Password);
        var vault = PasswordManager.KeyPass.Vault.Open(request.FilePath, request.Password, request.KeyFilePath);
        var entries = vault.Accounts.Select(a =>
        {
            var fav = a.Fields.TryGetValue("Favorite", out var f) &&
                      (f is "1" or "true" or "True");
            return MapLogin(a.Id, a.Name, a.Username, a.Password, a.Url, a.Note, path: a.Path, favorite: fav);
        });
        SetEntries(entries);
        return Task.CompletedTask;
    }
}

internal sealed class OpVaultProvider : MemoryVaultProvider
{
    public override string ProviderId => "OpVault";
    public override string DisplayName => "1Password OpVault";
    public override ProviderCapabilities Capabilities { get; } = new()
    {
        RequiresFilePath = true,
        SupportsSecureNotes = true
    };

    public override Task ConnectAsync(ProviderConnectRequest request, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.FilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Password);
        var accounts = PasswordManager.OpVault.Vault.Open(request.FilePath, request.Password);
        SetEntries(accounts.Select(a => MapLogin(a.Id, a.Name, a.Username, a.Password, a.Url, a.Note, path: a.Folder?.Name)));
        return Task.CompletedTask;
    }
}

internal sealed class LastPassVaultProvider : MemoryVaultProvider
{
    public override string ProviderId => "LastPass";
    public override string DisplayName => "LastPass";
    public override ProviderCapabilities Capabilities { get; } = new()
    {
        RequiresUsername = true,
        SupportsFavorites = true,
        SupportsSecureNotes = true,
        SupportsWriteBack = false
    };

    public override async Task ConnectAsync(ProviderConnectRequest request, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Username);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Password);
        var clientId = string.IsNullOrWhiteSpace(request.ClientId)
            ? PasswordManager.LastPass.Vault.GenerateRandomClientId()
            : request.ClientId;
        var vault = await PasswordManager.LastPass.Vault.Open(
            request.Username,
            request.Password,
            new PasswordManager.LastPass.ClientInfo(
                PasswordManager.LastPass.Platform.Desktop,
                clientId,
                "Authenticator"),
            new SilentLastPassUi(),
            new PasswordManager.LastPass.ParserOptions { ParseSecureNotesToAccount = true },
            null,
            ct);
        SetEntries(vault.Accounts.Select(a =>
            MapLogin(a.Id, a.Name, a.Username, a.Password, a.Url, a.Notes, a.Totp, a.Path, a.IsFavorite)));
    }

    private sealed class SilentLastPassUi : IAsyncUi
    {
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
            Task.FromResult<OneOf<Otp, WaitForOutOfBand, MfaMethod, Canceled>>(new Canceled("Authenticator silent UI"));

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
}

/// <summary>Cloud providers that need interactive MFA: façade present; connect stores credential probe result or fails clearly.</summary>
internal abstract class CloudStubProvider : MemoryVaultProvider
{
    public override ProviderCapabilities Capabilities { get; } = new()
    {
        RequiresUsername = true,
        SupportsSecureNotes = true
    };

    public override Task ConnectAsync(ProviderConnectRequest request, CancellationToken ct = default)
    {
        // Allow importing a previously exported JSON list via FilePath for offline migration workflows.
        if (!string.IsNullOrWhiteSpace(request.FilePath) && File.Exists(request.FilePath))
        {
            var json = File.ReadAllText(request.FilePath);
            var entries = System.Text.Json.JsonSerializer.Deserialize<List<ProviderEntry>>(json) ?? new();
            SetEntries(entries);
            LoggingConfiguration.LogInfo($"{ProviderId}: loaded {entries.Count} entries from JSON export.", nameof(CloudStubProvider));
            return Task.CompletedTask;
        }

        throw new NotSupportedException(
            $"{DisplayName} interactive login is available via API/PasswordManager; " +
            "pass a JSON export file path for bulk import, or use LastPass/KeePass/OpVault connect in this build.");
    }
}

internal sealed class BitwardenVaultProvider : CloudStubProvider
{
    public override string ProviderId => "Bitwarden";
    public override string DisplayName => "Bitwarden";
}

internal sealed class DashlaneVaultProvider : CloudStubProvider
{
    public override string ProviderId => "Dashlane";
    public override string DisplayName => "Dashlane";
}

internal sealed class OnePasswordVaultProvider : CloudStubProvider
{
    public override string ProviderId => "OnePassword";
    public override string DisplayName => "1Password";
}

internal sealed class ProtonPassVaultProvider : CloudStubProvider
{
    public override string ProviderId => "ProtonPass";
    public override string DisplayName => "Proton Pass";
}

internal sealed class RoboFormVaultProvider : CloudStubProvider
{
    public override string ProviderId => "RoboForm";
    public override string DisplayName => "RoboForm";
}

internal sealed class StickyPasswordVaultProvider : CloudStubProvider
{
    public override string ProviderId => "StickyPassword";
    public override string DisplayName => "Sticky Password";
}

internal sealed class TrueKeyVaultProvider : CloudStubProvider
{
    public override string ProviderId => "TrueKey";
    public override string DisplayName => "True Key";
}

internal sealed class ZohoVaultVaultProvider : CloudStubProvider
{
    public override string ProviderId => "ZohoVault";
    public override string DisplayName => "Zoho Vault";
}
