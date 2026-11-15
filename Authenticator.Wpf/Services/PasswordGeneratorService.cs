using System.Security.Cryptography;
using System.Text;

namespace Authenticator.Services;

public sealed class PasswordGeneratorOptions
{
    public int Length { get; set; } = 20;
    public bool Upper { get; set; } = true;
    public bool Lower { get; set; } = true;
    public bool Digits { get; set; } = true;
    public bool Symbols { get; set; } = true;
    public bool ExcludeAmbiguous { get; set; } = true;
}

public static class PasswordGeneratorService
{
    private const string UpperSet = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    private const string UpperAmb = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string LowerSet = "abcdefghijkmnopqrstuvwxyz";
    private const string LowerAmb = "abcdefghijklmnopqrstuvwxyz";
    private const string DigitSet = "23456789";
    private const string DigitAmb = "0123456789";
    private const string SymbolSet = "!@#$%^&*-_=+?";

    public static string Generate(PasswordGeneratorOptions? options = null)
    {
        options ??= new PasswordGeneratorOptions();
        var length = Math.Clamp(options.Length, 4, 128);
        var pools = new List<string>();
        if (options.Upper) pools.Add(options.ExcludeAmbiguous ? UpperSet : UpperAmb);
        if (options.Lower) pools.Add(options.ExcludeAmbiguous ? LowerSet : LowerAmb);
        if (options.Digits) pools.Add(options.ExcludeAmbiguous ? DigitSet : DigitAmb);
        if (options.Symbols) pools.Add(SymbolSet);
        if (pools.Count == 0)
            pools.Add(LowerSet);

        var all = string.Concat(pools);
        var chars = new char[length];
        // Ensure at least one from each selected pool.
        var i = 0;
        foreach (var pool in pools)
        {
            if (i >= length) break;
            chars[i++] = pool[RandomNumberGenerator.GetInt32(pool.Length)];
        }
        for (; i < length; i++)
            chars[i] = all[RandomNumberGenerator.GetInt32(all.Length)];

        // Shuffle
        for (var n = chars.Length - 1; n > 0; n--)
        {
            var j = RandomNumberGenerator.GetInt32(n + 1);
            (chars[n], chars[j]) = (chars[j], chars[n]);
        }
        return new string(chars);
    }
}
