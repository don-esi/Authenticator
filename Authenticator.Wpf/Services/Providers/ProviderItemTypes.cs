using Authenticator.Data.Models;

namespace Authenticator.Services.Providers;

/// <summary>
/// Maps provider-native type/category signals onto Shared <see cref="VaultItemType"/>.
/// KeePass has no real entry types (icons only) — imports stay Login.
/// </summary>
internal static class ProviderItemTypes
{
    public static VaultItemType FromLastPass(bool isSecureNote, string? noteType, string? url)
    {
        if (!isSecureNote &&
            !string.Equals(url, "http://sn", StringComparison.OrdinalIgnoreCase))
            return VaultItemType.Login;

        return FromLooseName(noteType, fallback: VaultItemType.SecureNote);
    }

    /// <summary>1Password classic OpVault / template category codes (001 Login, 002 Card, …).</summary>
    public static VaultItemType FromOnePasswordCategory(string? category) =>
        (category ?? "").Trim() switch
        {
            "001" => VaultItemType.Login,
            "002" => VaultItemType.CreditCard,
            "003" => VaultItemType.SecureNote,
            "004" => VaultItemType.Address, // Identity
            "005" => VaultItemType.Login,   // Password
            "100" => VaultItemType.Membership, // Software License
            "101" => VaultItemType.BankAccount,
            "102" => VaultItemType.Database,
            "103" => VaultItemType.DriverLicense,
            "104" => VaultItemType.Other, // Outdoor License
            "105" => VaultItemType.Membership,
            "106" => VaultItemType.Passport,
            "107" => VaultItemType.Membership, // Reward Program
            "108" => VaultItemType.Other, // SSN
            "109" => VaultItemType.Wifi, // Router
            "110" => VaultItemType.Server,
            "111" => VaultItemType.EmailAccount,
            "114" => VaultItemType.SshKey,
            _ => VaultItemType.Other
        };

    /// <summary>Bitwarden cipher type (1=Login … 5=SshKey).</summary>
    public static VaultItemType FromBitwardenType(int type) => type switch
    {
        1 => VaultItemType.Login,
        2 => VaultItemType.SecureNote,
        3 => VaultItemType.CreditCard,
        4 => VaultItemType.Address, // Identity
        5 => VaultItemType.SshKey,
        _ => VaultItemType.Other
    };

    /// <summary>
    /// Loose name / NoteType / JSON string → type. Unrecognized names use <paramref name="fallback"/>
    /// (LastPass unknown NoteTypes → SecureNote).
    /// </summary>
    public static VaultItemType FromLooseName(string? name, VaultItemType fallback = VaultItemType.Other)
    {
        if (TryParseLoose(name, out var parsed))
            return parsed;
        return fallback;
    }

    public static bool TryParseLoose(string? raw, out VaultItemType type)
    {
        type = VaultItemType.Login;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var s = raw.Trim();

        // 1Password category codes (001…114). Do not treat bare 1..5 as VaultItemType —
        // those collide with Bitwarden cipher type numbers.
        if (s.Length == 3 && s.All(char.IsDigit))
        {
            type = FromOnePasswordCategory(s);
            return true;
        }

        var compact = Compact(s);

        if (compact is "login" or "password" or "passwords" or "website" or "web")
        {
            type = VaultItemType.Login;
            return true;
        }

        if (compact is "securenote" or "securenotes" or "note" or "notes" or "generic")
        {
            type = VaultItemType.SecureNote;
            return true;
        }

        if (compact is "creditcard" or "card" or "paymentcard")
        {
            type = VaultItemType.CreditCard;
            return true;
        }

        if (compact is "address" or "identity")
        {
            type = VaultItemType.Address;
            return true;
        }

        if (compact is "bankaccount" or "bank" or "banking")
        {
            type = VaultItemType.BankAccount;
            return true;
        }

        if (compact is "server")
        {
            type = VaultItemType.Server;
            return true;
        }

        if (compact is "emailaccount" or "email" or "mail")
        {
            type = VaultItemType.EmailAccount;
            return true;
        }

        if (compact is "database" or "db")
        {
            type = VaultItemType.Database;
            return true;
        }

        if (compact is "instantmessenger" or "messenger" or "im" or "chat")
        {
            type = VaultItemType.InstantMessenger;
            return true;
        }

        if (compact is "sshkey" or "ssh" or "privatekey")
        {
            type = VaultItemType.SshKey;
            return true;
        }

        if (compact is "wifi" or "wifipassword" or "wireless" or "router")
        {
            type = VaultItemType.Wifi;
            return true;
        }

        if (compact is "membership" or "softwarelicense" or "license" or "rewardprogram")
        {
            type = VaultItemType.Membership;
            return true;
        }

        if (compact is "passport")
        {
            type = VaultItemType.Passport;
            return true;
        }

        if (compact is "driverslicense" or "driverlicense" or "drivinglicense")
        {
            type = VaultItemType.DriverLicense;
            return true;
        }

        if (compact is "insurance")
        {
            type = VaultItemType.Insurance;
            return true;
        }

        if (compact is "healthinsurance" or "medical")
        {
            type = VaultItemType.HealthInsurance;
            return true;
        }

        if (compact is "other")
        {
            type = VaultItemType.Other;
            return true;
        }

        // Enum member names (CreditCard, SecureNote, …).
        if (Enum.TryParse(s, ignoreCase: true, out VaultItemType parsed) &&
            Enum.IsDefined(typeof(VaultItemType), parsed))
        {
            type = parsed;
            return true;
        }

        return false;
    }

    /// <summary>
    /// If <paramref name="entry"/> still has default Login, try Fields / common export keys.
    /// </summary>
    public static void EnrichFromFields(ProviderEntry entry)
    {
        if (entry.ItemType != VaultItemType.Login)
            return;

        // Our / ProviderEntry exports — ordinal or name.
        if (TryGetField(entry, out var itemTypeRaw, "ItemType", "itemType"))
        {
            if (TryParseLoose(itemTypeRaw, out var fromName) && fromName != VaultItemType.Login)
            {
                entry.ItemType = fromName;
                return;
            }

            if (int.TryParse(itemTypeRaw, out var ordinal) &&
                Enum.IsDefined(typeof(VaultItemType), ordinal) &&
                ordinal != (int)VaultItemType.Login)
            {
                entry.ItemType = (VaultItemType)ordinal;
                return;
            }
        }

        // Bitwarden native "type": 1..5
        if (TryGetField(entry, out var bwType, "type", "Type") &&
            int.TryParse(bwType, out var bw) && bw is >= 1 and <= 5)
        {
            var mapped = FromBitwardenType(bw);
            if (mapped != VaultItemType.Login)
            {
                entry.ItemType = mapped;
                return;
            }
        }

        foreach (var key in new[] { "category", "Category", "NoteType", "noteType", "template", "TemplateId" })
        {
            if (TryGetField(entry, out var raw, key) &&
                TryParseLoose(raw, out var parsed) &&
                parsed != VaultItemType.Login)
            {
                entry.ItemType = parsed;
                return;
            }
        }
    }

    private static bool TryGetField(ProviderEntry entry, out string value, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (entry.Fields.TryGetValue(key, out var raw) && !string.IsNullOrWhiteSpace(raw))
            {
                value = raw;
                return true;
            }
        }

        value = "";
        return false;
    }

    private static string Compact(string value)
    {
        var chars = value
            .Where(c => !char.IsWhiteSpace(c) && c is not '_' and not '-' and not '.' and not '\'')
            .ToArray();
        return new string(chars).ToLowerInvariant();
    }
}
