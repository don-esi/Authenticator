using System.IO;
using System.Text;
using Authenticator.Logging;
using OtpNet;

namespace Authenticator.Services;

/// <summary>
/// Parses Google Authenticator otpauth-migration:// payloads (protobuf MigrationPayload).
/// </summary>
public static class GoogleMigrationParser
{
    private const string MigrationScheme = "otpauth-migration://";

    public static bool IsMigrationUri(string? text)
        => !string.IsNullOrWhiteSpace(text)
           && text.TrimStart().StartsWith(MigrationScheme, StringComparison.OrdinalIgnoreCase);

    public static GoogleMigrationResult? Parse(string uriOrPayload)
    {
        if (string.IsNullOrWhiteSpace(uriOrPayload))
        {
            LoggingConfiguration.LogWarning("Google migration payload was empty.", nameof(GoogleMigrationParser));
            return null;
        }

        try
        {
            var data = ExtractDataBytes(uriOrPayload.Trim());
            if (data is null || data.Length == 0)
            {
                LoggingConfiguration.LogWarning("Google migration URI had no decodable data.", nameof(GoogleMigrationParser));
                return null;
            }

            return DecodePayload(data);
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Failed to parse Google migration payload.", nameof(GoogleMigrationParser));
            return null;
        }
    }

    private static byte[]? ExtractDataBytes(string input)
    {
        string encoded;
        if (input.StartsWith(MigrationScheme, StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(input, UriKind.Absolute, out var uri))
                return null;

            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            encoded = query["data"] ?? string.Empty;
        }
        else
        {
            encoded = input;
        }

        if (string.IsNullOrWhiteSpace(encoded))
            return null;

        encoded = Uri.UnescapeDataString(encoded.Trim());
        encoded = encoded.Replace('-', '+').Replace('_', '/');
        switch (encoded.Length % 4)
        {
            case 2:
                encoded += "==";
                break;
            case 3:
                encoded += "=";
                break;
        }

        return Convert.FromBase64String(encoded);
    }

    private static GoogleMigrationResult DecodePayload(byte[] data)
    {
        var accounts = new List<GoogleMigrationAccount>();
        var unsupported = 0;
        var version = 0;
        var batchSize = 0;
        var batchIndex = 0;
        var batchId = 0;

        var reader = new ProtoReader(data);
        while (reader.TryReadTag(out var field, out var wireType))
        {
            switch (field)
            {
                case 1 when wireType == ProtoWireType.LengthDelimited:
                {
                    var message = reader.ReadBytes();
                    if (TryParseOtpParameters(message, out var account))
                        accounts.Add(account);
                    else
                        unsupported++;
                    break;
                }
                case 2 when wireType == ProtoWireType.Varint:
                    version = (int)reader.ReadVarint();
                    break;
                case 3 when wireType == ProtoWireType.Varint:
                    batchSize = (int)reader.ReadVarint();
                    break;
                case 4 when wireType == ProtoWireType.Varint:
                    batchIndex = (int)reader.ReadVarint();
                    break;
                case 5 when wireType == ProtoWireType.Varint:
                    batchId = (int)reader.ReadVarint();
                    break;
                default:
                    reader.Skip(wireType);
                    break;
            }
        }

        LoggingConfiguration.LogInfo(
            $"Google migration parsed. Accounts={accounts.Count}, Unsupported={unsupported}, Batch={batchIndex + 1}/{Math.Max(batchSize, 1)}, Version={version}.",
            nameof(GoogleMigrationParser));

        return new GoogleMigrationResult
        {
            Accounts = accounts,
            UnsupportedCount = unsupported,
            Version = version,
            BatchSize = batchSize,
            BatchIndex = batchIndex,
            BatchId = batchId
        };
    }

    private static bool TryParseOtpParameters(byte[] message, out GoogleMigrationAccount account)
    {
        account = new GoogleMigrationAccount();
        byte[]? secret = null;
        var name = string.Empty;
        var issuer = string.Empty;
        var algorithm = 1; // SHA1
        var digits = 1;    // six
        var type = 2;      // TOTP

        var reader = new ProtoReader(message);
        while (reader.TryReadTag(out var field, out var wireType))
        {
            switch (field)
            {
                case 1 when wireType == ProtoWireType.LengthDelimited:
                    secret = reader.ReadBytes();
                    break;
                case 2 when wireType == ProtoWireType.LengthDelimited:
                    name = reader.ReadString();
                    break;
                case 3 when wireType == ProtoWireType.LengthDelimited:
                    issuer = reader.ReadString();
                    break;
                case 4 when wireType == ProtoWireType.Varint:
                    algorithm = (int)reader.ReadVarint();
                    break;
                case 5 when wireType == ProtoWireType.Varint:
                    digits = (int)reader.ReadVarint();
                    break;
                case 6 when wireType == ProtoWireType.Varint:
                    type = (int)reader.ReadVarint();
                    break;
                default:
                    reader.Skip(wireType);
                    break;
            }
        }

        // OTP_TYPE_HOTP = 1; only TOTP (2) or unspecified treated as TOTP when secret present.
        if (type == 1 || secret is null || secret.Length == 0)
            return false;

        var service = issuer.Trim();
        var username = name.Trim();
        if (string.IsNullOrWhiteSpace(service) && username.Contains(':', StringComparison.Ordinal))
        {
            var parts = username.Split(':', 2);
            service = parts[0].Trim();
            username = parts[1].Trim();
        }

        if (string.IsNullOrWhiteSpace(service))
            service = string.IsNullOrWhiteSpace(username) ? "Imported" : username;
        if (string.IsNullOrWhiteSpace(username))
            username = service;

        string base32;
        try
        {
            base32 = Base32Encoding.ToString(secret);
        }
        catch (Exception)
        {
            return false;
        }

        account = new GoogleMigrationAccount
        {
            ServiceName = service,
            Username = username,
            SecretKey = base32.Replace("=", string.Empty, StringComparison.Ordinal)
                              .Replace(" ", string.Empty, StringComparison.Ordinal)
                              .Replace("-", string.Empty, StringComparison.Ordinal)
                              .Trim()
                              .ToUpperInvariant(),
            Algorithm = algorithm switch
            {
                2 => "SHA256",
                3 => "SHA512",
                _ => "SHA1"
            },
            Digits = digits == 2 ? 8 : 6,
            Period = 30
        };
        return !string.IsNullOrWhiteSpace(account.SecretKey);
    }

    private enum ProtoWireType
    {
        Varint = 0,
        Fixed64 = 1,
        LengthDelimited = 2,
        Fixed32 = 5
    }

    private sealed class ProtoReader(byte[] data)
    {
        private int _offset;

        public bool TryReadTag(out int field, out ProtoWireType wireType)
        {
            field = 0;
            wireType = ProtoWireType.Varint;
            if (_offset >= data.Length)
                return false;

            var tag = ReadVarint();
            field = (int)(tag >> 3);
            wireType = (ProtoWireType)(tag & 0x7);
            return true;
        }

        public ulong ReadVarint()
        {
            ulong result = 0;
            var shift = 0;
            while (_offset < data.Length)
            {
                var b = data[_offset++];
                result |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                    return result;
                shift += 7;
                if (shift > 63)
                    throw new InvalidDataException("Varint too long.");
            }

            throw new InvalidDataException("Unexpected end of protobuf data.");
        }

        public byte[] ReadBytes()
        {
            var length = (int)ReadVarint();
            if (length < 0 || _offset + length > data.Length)
                throw new InvalidDataException("Invalid length-delimited field.");

            var slice = data.AsSpan(_offset, length).ToArray();
            _offset += length;
            return slice;
        }

        public string ReadString() => Encoding.UTF8.GetString(ReadBytes());

        public void Skip(ProtoWireType wireType)
        {
            switch (wireType)
            {
                case ProtoWireType.Varint:
                    _ = ReadVarint();
                    break;
                case ProtoWireType.Fixed64:
                    Ensure(8);
                    _offset += 8;
                    break;
                case ProtoWireType.LengthDelimited:
                    _ = ReadBytes();
                    break;
                case ProtoWireType.Fixed32:
                    Ensure(4);
                    _offset += 4;
                    break;
                default:
                    throw new InvalidDataException($"Unsupported protobuf wire type {(int)wireType}.");
            }
        }

        private void Ensure(int count)
        {
            if (_offset + count > data.Length)
                throw new InvalidDataException("Unexpected end of protobuf data.");
        }
    }
}

public sealed class GoogleMigrationResult
{
    public List<GoogleMigrationAccount> Accounts { get; init; } = [];
    public int UnsupportedCount { get; init; }
    public int Version { get; init; }
    public int BatchSize { get; init; }
    public int BatchIndex { get; init; }
    public int BatchId { get; init; }
}

public sealed class GoogleMigrationAccount
{
    public string ServiceName { get; init; } = string.Empty;
    public string Username { get; init; } = string.Empty;
    public string SecretKey { get; init; } = string.Empty;
    public string Algorithm { get; init; } = "SHA1";
    public int Digits { get; init; } = 6;
    public int Period { get; init; } = 30;
}
