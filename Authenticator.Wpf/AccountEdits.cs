using System.Windows.Media;
using Authenticator.Data.Models;

namespace Authenticator;

internal static class AccountEdits
{
    public static readonly string[] InitialColors =
    [
        "#E74C3C",
        "#E67E22",
        "#2980B9",
        "#27AE60",
        "#8E44AD",
        "#16A085",
        "#C0392B",
        "#2C3E50"
    ];

    public static readonly string[] FolderColors =
    [
        "#4ECDC4",
        "#95E1D3",
        "#A8DADC",
        "#F38181",
        "#FFA987",
        "#B8A4D4",
        "#C7CEEA",
        "#FFD3B6"
    ];

    public static Account CopyScalars(Account source)
    {
        return new Account
        {
            Id = source.Id,
            ServiceName = source.ServiceName,
            ServiceUrl = source.ServiceUrl,
            Username = source.Username,
            SecretKey = source.SecretKey,
            Algorithm = source.Algorithm,
            Digits = source.Digits,
            Period = source.Period,
            FolderId = source.FolderId,
            IsFavorite = source.IsFavorite,
            IsDeleted = source.IsDeleted,
            CreatedAt = source.CreatedAt,
            ModifiedAt = source.ModifiedAt
        };
    }

    public static string NormalizeSecret(string secretKey)
    {
        return secretKey.Replace(" ", string.Empty, StringComparison.Ordinal)
                        .Replace("-", string.Empty, StringComparison.Ordinal)
                        .Replace("=", string.Empty, StringComparison.Ordinal)
                        .Trim()
                        .ToUpperInvariant();
    }

    public static string FormatCode(string code)
    {
        if (code.Length == 6)
            return string.Concat(code.AsSpan(0, 3), " ", code.AsSpan(3));
        if (code.Length == 8)
            return string.Concat(code.AsSpan(0, 4), " ", code.AsSpan(4));
        return code;
    }

    public static string Initial(string serviceName)
    {
        foreach (var ch in serviceName.Trim())
        {
            if (char.IsLetterOrDigit(ch))
                return char.ToUpperInvariant(ch).ToString();
        }

        return "?";
    }

    public static SolidColorBrush InitialBrush(string serviceName)
    {
        var index = Math.Abs(StableHash(serviceName)) % InitialColors.Length;
        return BrushFromHex(InitialColors[index]);
    }

    public static SolidColorBrush BrushFromHex(string hex)
    {
        var color = (Color)ColorConverter.ConvertFromString(hex);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static int StableHash(string text)
    {
        unchecked
        {
            var hash = 23;
            foreach (var ch in text)
                hash = (hash * 31) + char.ToUpperInvariant(ch);
            return hash;
        }
    }
}
