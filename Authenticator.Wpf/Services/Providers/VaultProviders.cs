using System.IO;
using Authenticator.Data.Models;
using Authenticator.Logging;
using KeePassLib;
using KeePassLib.Keys;
using KeePassLib.Serialization;
using KeePassLib.Security;

namespace Authenticator.Services.Providers;

internal abstract class MemoryVaultProvider : IVaultProvider
{
    private List<ProviderEntry> _entries = new();

    public abstract string ProviderId { get; }
    public abstract string DisplayName { get; }
    public abstract ProviderCapabilities Capabilities { get; }
    public bool IsConnected { get; protected set; }

    public abstract Task ConnectAsync(ProviderConnectRequest request, CancellationToken ct = default);

    public virtual void Disconnect()
    {
        IsConnected = false;
        _entries = new();
    }

    public Task<IReadOnlyList<ProviderEntry>> ListEntriesAsync(CancellationToken ct = default)
    {
        // Snapshot — concurrent UpdateMemoryEntry (Diff push) must not invalidate enumerators.
        IReadOnlyList<ProviderEntry> snapshot = IsConnected
            ? _entries.ToList()
            : Array.Empty<ProviderEntry>();
        return Task.FromResult(snapshot);
    }

    public virtual Task UpdateEntryAsync(ProviderEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!IsConnected)
            throw new InvalidOperationException($"{DisplayName} is not connected.");
        if (string.IsNullOrWhiteSpace(entry.ExternalId))
            throw new ArgumentException("Origin store id (ExternalId) is required.", nameof(entry));

        UpdateMemoryEntry(entry);
        return Task.CompletedTask;
    }

    public virtual Task DeleteEntryAsync(string externalId, CancellationToken ct = default)
    {
        if (!IsConnected)
            throw new InvalidOperationException($"{DisplayName} is not connected.");
        if (!Capabilities.SupportsDelete)
            throw new NotSupportedException($"{DisplayName} does not support deleting entries.");
        RemoveMemoryEntry(externalId);
        return Task.CompletedTask;
    }

    public virtual Task<string> CreateEntryAsync(ProviderEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!IsConnected)
            throw new InvalidOperationException($"{DisplayName} is not connected.");
        if (!Capabilities.SupportsCreate)
            throw new NotSupportedException($"{DisplayName} does not support creating entries.");
        throw new NotSupportedException($"{DisplayName} create is not implemented.");
    }

    protected void UpdateMemoryEntry(ProviderEntry entry)
    {
        var originId = entry.ExternalId.Trim();
        var index = _entries.FindIndex(e =>
            string.Equals(e.ExternalId, originId, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            throw new InvalidOperationException(
                $"No {DisplayName} entry with origin id '{originId}'.");

        // Merge onto the live session entry so Path/Fields/etc. survive Shared edits.
        var previous = _entries[index];
        _entries[index] = new ProviderEntry
        {
            ExternalId = previous.ExternalId,
            Title = entry.Title,
            Username = entry.Username,
            Password = entry.Password ?? previous.Password,
            Url = entry.Url,
            Notes = entry.Notes,
            Totp = entry.Totp ?? previous.Totp,
            Path = entry.Path is not null ? entry.Path : previous.Path,
            IsFavorite = entry.IsFavorite,
            ItemType = entry.ItemType == default ? previous.ItemType : entry.ItemType,
            Fields = entry.Fields.Count > 0 ? entry.Fields : previous.Fields,
            CreatedAt = entry.CreatedAt ?? previous.CreatedAt,
            ModifiedAt = entry.ModifiedAt ?? DateTime.UtcNow
        };
    }

    protected void RemoveMemoryEntry(string externalId)
    {
        var originId = (externalId ?? "").Trim();
        _entries.RemoveAll(e =>
            string.Equals(e.ExternalId, originId, StringComparison.OrdinalIgnoreCase));
    }

    protected void AddMemoryEntry(ProviderEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var originId = entry.ExternalId.Trim();
        var index = _entries.FindIndex(e =>
            string.Equals(e.ExternalId, originId, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
            _entries[index] = entry;
        else
            _entries.Add(entry);
    }

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
        bool favorite = false,
        DateTime? createdAt = null,
        DateTime? modifiedAt = null) =>
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
            ItemType = VaultItemType.Login,
            CreatedAt = createdAt,
            ModifiedAt = modifiedAt
        };
}

internal sealed class KeePassVaultProvider : MemoryVaultProvider
{
    private readonly object _gate = new();
    private PwDatabase? _db;

    public override string ProviderId => "KeePass";
    public override string DisplayName => "KeePass";
    public override ProviderCapabilities Capabilities { get; } = new()
    {
        RequiresFilePath = true,
        SupportsSecureNotes = true,
        SupportsFavorites = true,
        SupportsWriteBack = true,
        SupportsDelete = true,
        SupportsCreate = true
    };

    public override Task ConnectAsync(ProviderConnectRequest request, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.FilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Password);

        lock (_gate)
        {
            CloseDatabase();

            var key = new CompositeKey();
            key.AddUserKey(new KcpPassword(request.Password));
            if (!string.IsNullOrWhiteSpace(request.KeyFilePath))
                key.AddUserKey(new KcpKeyFile(request.KeyFilePath));

            var db = new PwDatabase();
            db.Open(IOConnectionInfo.FromPath(request.FilePath), key, null);
            _db = db;
            SetEntries(EnumerateEntries(db));
            LoggingConfiguration.LogInfo(
                $"KeePass opened '{request.FilePath}' with {_db.RootGroup.GetEntriesCount(true)} entries (write-back enabled).",
                nameof(KeePassVaultProvider));
        }

        return Task.CompletedTask;
    }

    public override void Disconnect()
    {
        lock (_gate)
            CloseDatabase();
        base.Disconnect();
    }

    public override Task UpdateEntryAsync(ProviderEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (string.IsNullOrWhiteSpace(entry.ExternalId))
            throw new ArgumentException("Origin store id (ExternalId) is required.", nameof(entry));

        lock (_gate)
        {
            if (_db is null || !IsConnected)
                throw new InvalidOperationException("KeePass is not connected.");

            var uuid = ParseUuid(entry.ExternalId);
            var pe = _db.RootGroup.FindEntry(uuid, true)
                     ?? throw new InvalidOperationException(
                         $"No KeePass entry with origin id '{entry.ExternalId.Trim()}'.");

            SetField(pe, PwDefs.TitleField, entry.Title ?? "", protect: false);
            SetField(pe, PwDefs.UserNameField, entry.Username ?? "", protect: false);
            if (entry.Password is not null)
                SetField(pe, PwDefs.PasswordField, entry.Password, protect: true);
            SetField(pe, PwDefs.UrlField, entry.Url ?? "", protect: false);
            SetField(pe, PwDefs.NotesField, entry.Notes ?? "", protect: false);
            SetField(pe, "Favorite", entry.IsFavorite ? "1" : "0", protect: false);
            if (entry.Path is not null)
                MoveEntryToPath(pe, entry.Path);
            pe.Touch(true, false);

            SaveDatabase();
            entry.Path = BuildPath(pe);
            LoggingConfiguration.LogInfo(
                $"KeePass wrote origin id {entry.ExternalId.Trim()} to disk (path='{entry.Path}').",
                nameof(KeePassVaultProvider));
        }

        UpdateMemoryEntry(entry);
        return Task.CompletedTask;
    }

    public override Task DeleteEntryAsync(string externalId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(externalId))
            throw new ArgumentException("Origin store id is required.", nameof(externalId));

        lock (_gate)
        {
            if (_db is null || !IsConnected)
                throw new InvalidOperationException("KeePass is not connected.");

            var uuid = ParseUuid(externalId);
            var pe = _db.RootGroup.FindEntry(uuid, true)
                     ?? throw new InvalidOperationException(
                         $"No KeePass entry with origin id '{externalId.Trim()}'.");
            var parent = pe.ParentGroup
                         ?? throw new InvalidOperationException("KeePass entry has no parent group.");
            if (!parent.Entries.Remove(pe))
                throw new InvalidOperationException($"Could not remove KeePass entry '{externalId.Trim()}'.");
            SaveDatabase();
            LoggingConfiguration.LogInfo(
                $"KeePass deleted origin id {externalId.Trim()}.",
                nameof(KeePassVaultProvider));
        }

        RemoveMemoryEntry(externalId);
        return Task.CompletedTask;
    }

    public override Task<string> CreateEntryAsync(ProviderEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        string id;
        lock (_gate)
        {
            if (_db is null || !IsConnected)
                throw new InvalidOperationException("KeePass is not connected.");

            var group = ResolveOrCreateGroup(entry.Path);
            var pe = new PwEntry(true, true);
            SetField(pe, PwDefs.TitleField, entry.Title ?? "", protect: false);
            SetField(pe, PwDefs.UserNameField, entry.Username ?? "", protect: false);
            SetField(pe, PwDefs.PasswordField, entry.Password ?? "", protect: true);
            SetField(pe, PwDefs.UrlField, entry.Url ?? "", protect: false);
            SetField(pe, PwDefs.NotesField, entry.Notes ?? "", protect: false);
            SetField(pe, "Favorite", entry.IsFavorite ? "1" : "0", protect: false);
            group.AddEntry(pe, true);
            SaveDatabase();
            id = pe.Uuid.ToHexString().ToLowerInvariant();
            entry.ExternalId = id;
            entry.Path = BuildPath(pe);
            LoggingConfiguration.LogInfo(
                $"KeePass created origin id {id} under '{entry.Path}'.",
                nameof(KeePassVaultProvider));
        }

        AddMemoryEntry(entry);
        return Task.FromResult(id);
    }

    private void MoveEntryToPath(PwEntry entry, string? path)
    {
        var target = ResolveOrCreateGroup(path);
        if (ReferenceEquals(entry.ParentGroup, target))
            return;
        entry.ParentGroup?.Entries.Remove(entry);
        target.AddEntry(entry, true);
    }

    private PwGroup ResolveOrCreateGroup(string? path)
    {
        if (_db is null)
            throw new InvalidOperationException("KeePass is not connected.");

        var normalized = NormalizeKeePassPath(path);
        if (string.IsNullOrEmpty(normalized))
            return _db.RootGroup;

        // FindCreateSubTree creates missing groups; separators match Path we emit with '/'.
        return _db.RootGroup.FindCreateSubTree(normalized, new[] { '/', '\\' }, true)
               ?? _db.RootGroup;
    }

    private static string NormalizeKeePassPath(string? path)
    {
        var raw = (path ?? "").Trim().Replace('\\', '/');
        if (string.IsNullOrEmpty(raw) ||
            string.Equals(raw, "(none)", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(raw, "/", StringComparison.Ordinal))
            return "";

        var parts = raw.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => !string.Equals(p, "Root", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return string.Join("/", parts);
    }

    private void SaveDatabase()
    {
        if (_db is null)
            return;
        try
        {
            _db.Save(null);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Could not save KeePass database. Close the file in KeePass if it is open, then try again.",
                ex);
        }
    }

    private void CloseDatabase()
    {
        if (_db is null)
            return;
        try
        {
            _db.Close();
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "KeePass Close failed.", nameof(KeePassVaultProvider));
        }
        finally
        {
            _db = null;
        }
    }

    private static IEnumerable<ProviderEntry> EnumerateEntries(PwDatabase db)
    {
        foreach (var pe in db.RootGroup.GetEntries(true))
        {
            var fav = pe.Strings.ReadSafe("Favorite");
            var favorite = fav is "1" or "true" or "True";
            yield return MapLogin(
                pe.Uuid.ToHexString().ToLowerInvariant(),
                pe.Strings.ReadSafe(PwDefs.TitleField),
                pe.Strings.ReadSafe(PwDefs.UserNameField),
                pe.Strings.ReadSafe(PwDefs.PasswordField),
                pe.Strings.ReadSafe(PwDefs.UrlField),
                pe.Strings.ReadSafe(PwDefs.NotesField),
                path: BuildPath(pe),
                favorite: favorite,
                createdAt: ToUtc(pe.CreationTime),
                modifiedAt: ToUtc(pe.LastModificationTime));
        }
    }

    private static DateTime? ToUtc(DateTime value)
    {
        if (value == default || value.Year < 1980)
            return null;
        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
    }

    private static string BuildPath(PwEntry entry)
    {
        var parts = new Stack<string>();
        var group = entry.ParentGroup;
        while (group is not null)
        {
            parts.Push(group.Name);
            group = group.ParentGroup;
        }

        return string.Join("/", parts);
    }

    private static void SetField(PwEntry entry, string key, string value, bool protect) =>
        entry.Strings.Set(key, new ProtectedString(protect, value ?? ""));

    private static PwUuid ParseUuid(string hex)
    {
        var cleaned = hex.Trim().Replace("-", "", StringComparison.Ordinal);
        if (cleaned.Length != 32 || !cleaned.All(Uri.IsHexDigit))
            throw new ArgumentException($"Invalid KeePass origin id '{hex}'.", nameof(hex));

        return new PwUuid(Convert.FromHexString(cleaned));
    }
}

internal sealed class OpVaultProvider : MemoryVaultProvider
{
    public override string ProviderId => "OpVault";
    public override string DisplayName => "1Password OpVault";
    public override ProviderCapabilities Capabilities { get; } = new()
    {
        RequiresFilePath = true,
        SupportsSecureNotes = true,
        SupportsWriteBack = true
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
    private readonly object _gate = new();
    private PasswordManager.LastPass.Vault? _vault;

    public override string ProviderId => "LastPass";
    public override string DisplayName => "LastPass";
    public override ProviderCapabilities Capabilities { get; } = new()
    {
        RequiresUsername = true,
        SupportsFavorites = true,
        SupportsSecureNotes = true,
        // Live session write-back via show_website.php (session retained until Disconnect).
        SupportsWriteBack = true,
        SupportsDelete = true,
        SupportsCreate = true
    };

    public override async Task ConnectAsync(ProviderConnectRequest request, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Username);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Password);
        var clientId = string.IsNullOrWhiteSpace(request.ClientId)
            ? PasswordManager.LastPass.Vault.GenerateRandomClientId()
            : request.ClientId;
        var ui = request.LastPassUi ?? LastPassSilentUi.Instance;
        try
        {
            var vault = await PasswordManager.LastPass.Vault.Open(
                request.Username,
                request.Password,
                new PasswordManager.LastPass.ClientInfo(
                    PasswordManager.LastPass.Platform.Desktop,
                    clientId,
                    "Authenticator"),
                ui,
                new PasswordManager.LastPass.ParserOptions
                {
                    ParseSecureNotesToAccount = true,
                    LoggingEnabled = true
                },
                new AuthenticatorLastPassLogger(),
                ct);

            lock (_gate)
            {
                CloseVault();
                _vault = vault;
                SetEntries(vault.Accounts.Select(a =>
                    MapLogin(
                        a.Id,
                        a.Name,
                        a.Username,
                        a.Password,
                        a.Url,
                        a.Notes,
                        a.Totp,
                        a.Path,
                        a.IsFavorite,
                        createdAt: a.CreatedAt,
                        // Prefer last password change when present — more useful for Diff accuracy.
                        modifiedAt: a.LastPasswordChangeAt ?? a.ModifiedAt)));
            }

            LoggingConfiguration.LogInfo(
                $"LastPass connected with {vault.Accounts.Length} accounts (write-back enabled).",
                nameof(LastPassVaultProvider));
        }
        catch (PasswordManager.Common.BaseException ex)
        {
            // Server message (incl. new-device email verification) is already on ex.Message —
            // log and rethrow so the connect dialog shows it verbatim.
            LoggingConfiguration.LogException(ex, $"LastPass login failed: {ex.Message}", nameof(LastPassVaultProvider));
            throw;
        }
    }

    public override void Disconnect()
    {
        lock (_gate)
            CloseVault();
        base.Disconnect();
    }

    public override async Task UpdateEntryAsync(ProviderEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (string.IsNullOrWhiteSpace(entry.ExternalId))
            throw new ArgumentException("Origin store id (ExternalId) is required.", nameof(entry));

        PasswordManager.LastPass.Vault vault;
        lock (_gate)
        {
            if (_vault is null || !IsConnected)
                throw new InvalidOperationException("LastPass is not connected.");
            vault = _vault;
        }

        await vault.UpdateAccountAsync(
            entry.ExternalId.Trim(),
            entry.Title ?? "",
            entry.Username ?? "",
            entry.Password ?? "",
            entry.Url ?? "",
            entry.Notes ?? "",
            entry.IsFavorite,
            pathOrGrouping: entry.Path,
            cancellationToken: ct);

        // Refresh Path from live account after grouping change.
        var live = vault.Accounts.FirstOrDefault(a =>
            string.Equals(a.Id, entry.ExternalId.Trim(), StringComparison.Ordinal));
        if (live is not null)
            entry.Path = live.Path;

        UpdateMemoryEntry(entry);
        LoggingConfiguration.LogInfo(
            $"LastPass remote update OK for origin id '{entry.ExternalId}' (path='{entry.Path}').",
            nameof(LastPassVaultProvider));
    }

    public override async Task DeleteEntryAsync(string externalId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(externalId))
            throw new ArgumentException("Origin store id is required.", nameof(externalId));

        PasswordManager.LastPass.Vault vault;
        lock (_gate)
        {
            if (_vault is null || !IsConnected)
                throw new InvalidOperationException("LastPass is not connected.");
            vault = _vault;
        }

        await vault.DeleteAccountAsync(externalId.Trim(), ct);
        RemoveMemoryEntry(externalId);
        LoggingConfiguration.LogInfo(
            $"LastPass remote delete OK for origin id '{externalId}'.",
            nameof(LastPassVaultProvider));
    }

    public override async Task<string> CreateEntryAsync(ProviderEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        PasswordManager.LastPass.Vault vault;
        lock (_gate)
        {
            if (_vault is null || !IsConnected)
                throw new InvalidOperationException("LastPass is not connected.");
            vault = _vault;
        }

        var id = await vault.CreateAccountAsync(
            entry.Title ?? "",
            entry.Username ?? "",
            entry.Password ?? "",
            entry.Url ?? "",
            entry.Notes ?? "",
            entry.IsFavorite,
            pathOrGrouping: entry.Path,
            cancellationToken: ct);

        entry.ExternalId = id;
        var live = vault.Accounts.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.Ordinal));
        if (live is not null)
            entry.Path = live.Path;
        AddMemoryEntry(entry);
        LoggingConfiguration.LogInfo(
            $"LastPass remote create OK origin id '{id}' (path='{entry.Path}').",
            nameof(LastPassVaultProvider));
        return id;
    }

    private void CloseVault()
    {
        _vault?.Dispose();
        _vault = null;
    }
}

/// <summary>Forwards PasswordManager LastPass logs into Authenticator Serilog (secrets censored upstream).</summary>
file sealed class AuthenticatorLastPassLogger : PasswordManager.Common.ISecureLogger
{
    public void Log(PasswordManager.Common.LogEntry entry) =>
        LoggingConfiguration.LogInfo($"[{entry.Tag}] {entry.Message}", "LastPass");
}

/// <summary>Cloud providers that need interactive MFA: façade present; connect stores credential probe result or fails clearly.</summary>
internal abstract class CloudStubProvider : MemoryVaultProvider
{
    public override ProviderCapabilities Capabilities { get; } = new()
    {
        RequiresUsername = true,
        SupportsSecureNotes = true,
        SupportsWriteBack = true
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
