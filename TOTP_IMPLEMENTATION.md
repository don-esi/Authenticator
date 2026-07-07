# TOTP Implementation Options

Since the GoogleAuthenticator project reference was removed, here are your options for TOTP functionality:

## Option 1: Use Otp.NET (Recommended - Simplest)

### Install Package
```xml
<PackageReference Include="Otp.NET" Version="1.4.0" />
```

### Update TotpService.cs

```csharp
using Authenticator.Data.Models;
using Otp;

namespace Authenticator.Services;

public class TotpService
{
	public string GenerateCode(Account account)
	{
		var totp = new Totp(Base32Encoding.ToBytes(account.SecretKey), account.Period);
		return totp.ComputeTotp();
	}

	public string GenerateCode(Account account, int offset)
	{
		var totp = new Totp(Base32Encoding.ToBytes(account.SecretKey), account.Period);
		var timeStep = DateTime.UtcNow.AddSeconds(offset * account.Period);
		return totp.ComputeTotp(timeStep);
	}

	public int GetSecondsRemaining(Account account)
	{
		var epoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
		return account.Period - (int)(epoch % account.Period);
	}

	public double GetProgress(Account account)
	{
		var remaining = GetSecondsRemaining(account);
		return (double)remaining / account.Period;
	}

	public bool ValidateCode(Account account, string code)
	{
		var totp = new Totp(Base32Encoding.ToBytes(account.SecretKey), account.Period);
		return totp.VerifyTotp(code, out _, new VerificationWindow(2, 2));
	}

	public string GenerateSetupCode(string issuer, string accountName, string secretKey)
	{
		return $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(accountName)}?secret={secretKey}&issuer={Uri.EscapeDataString(issuer)}";
	}
}
```

## Option 2: Restore GoogleAuthenticator Project Reference

If the project exists locally at the expected path:

### Uncomment in Authenticator.csproj
```xml
<ItemGroup>
  <ProjectReference Include="..\GoogleAuthenticator\Google.Authenticator\Google.Authenticator.csproj" />
</ItemGroup>
```

### Keep existing TotpService.cs as-is
The current implementation should work with the Google.Authenticator library.

## Option 3: Custom Implementation (No Dependencies)

### Create Helpers/Base32Encoding.cs

```csharp
using System.Text;

namespace Authenticator.Helpers;

public static class Base32Encoding
{
	private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

	public static byte[] ToBytes(string base32)
	{
		if (string.IsNullOrEmpty(base32))
			throw new ArgumentNullException(nameof(base32));

		base32 = base32.TrimEnd('=').ToUpper();
		if (base32.Length == 0)
			return Array.Empty<byte>();

		var bytes = new byte[base32.Length * 5 / 8];
		var bitIndex = 0;
		var byteIndex = 0;
		var currentByte = 0;

		foreach (var c in base32)
		{
			var charValue = Base32Alphabet.IndexOf(c);
			if (charValue < 0)
				throw new ArgumentException($"Invalid Base32 character: {c}", nameof(base32));

			currentByte = (currentByte << 5) | charValue;
			bitIndex += 5;

			if (bitIndex >= 8)
			{
				bytes[byteIndex++] = (byte)(currentByte >> (bitIndex - 8));
				bitIndex -= 8;
				currentByte &= (1 << bitIndex) - 1;
			}
		}

		return bytes;
	}

	public static string ToString(byte[] bytes)
	{
		if (bytes == null || bytes.Length == 0)
			return string.Empty;

		var result = new StringBuilder((bytes.Length + 4) / 5 * 8);
		var bitIndex = 0;
		var currentByte = 0;

		foreach (var b in bytes)
		{
			currentByte = (currentByte << 8) | b;
			bitIndex += 8;

			while (bitIndex >= 5)
			{
				result.Append(Base32Alphabet[(currentByte >> (bitIndex - 5)) & 31]);
				bitIndex -= 5;
				currentByte &= (1 << bitIndex) - 1;
			}
		}

		if (bitIndex > 0)
		{
			result.Append(Base32Alphabet[(currentByte << (5 - bitIndex)) & 31]);
		}

		while (result.Length % 8 != 0)
		{
			result.Append('=');
		}

		return result.ToString();
	}
}
```

### Create Helpers/HmacGenerator.cs

```csharp
using System.Security.Cryptography;

namespace Authenticator.Helpers;

public static class HmacGenerator
{
	public static byte[] ComputeHmac(string algorithm, byte[] key, byte[] message)
	{
		using var hmac = algorithm.ToUpper() switch
		{
			"SHA1" => new HMACSHA1(key),
			"SHA256" => new HMACSHA256(key),
			"SHA512" => new HMACSHA512(key),
			_ => throw new ArgumentException($"Unsupported algorithm: {algorithm}", nameof(algorithm))
		};

		return hmac.ComputeHash(message);
	}
}
```

### Update Services/TotpService.cs

```csharp
using Authenticator.Data.Models;
using Authenticator.Helpers;
using System;

namespace Authenticator.Services;

public class TotpService
{
	public string GenerateCode(Account account)
	{
		return GenerateCode(account, 0);
	}

	public string GenerateCode(Account account, int offset)
	{
		var key = Base32Encoding.ToBytes(account.SecretKey.Replace(" ", "").Replace("-", ""));
		var unixTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
		var counter = (unixTime / account.Period) + offset;

		var counterBytes = BitConverter.GetBytes(counter);
		if (BitConverter.IsLittleEndian)
		{
			Array.Reverse(counterBytes);
		}

		var hash = HmacGenerator.ComputeHmac(account.Algorithm, key, counterBytes);

		var offset_bits = hash[^1] & 0x0f;
		var binary = ((hash[offset_bits] & 0x7f) << 24)
				   | ((hash[offset_bits + 1] & 0xff) << 16)
				   | ((hash[offset_bits + 2] & 0xff) << 8)
				   | (hash[offset_bits + 3] & 0xff);

		var otp = binary % (int)Math.Pow(10, account.Digits);
		return otp.ToString().PadLeft(account.Digits, '0');
	}

	public int GetSecondsRemaining(Account account)
	{
		var epoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
		return account.Period - (int)(epoch % account.Period);
	}

	public double GetProgress(Account account)
	{
		var remaining = GetSecondsRemaining(account);
		return (double)remaining / account.Period;
	}

	public bool ValidateCode(Account account, string code)
	{
		if (string.IsNullOrEmpty(code))
			return false;

		// Check current and ±1 time windows for clock drift tolerance
		for (int offset = -1; offset <= 1; offset++)
		{
			var expectedCode = GenerateCode(account, offset);
			if (code == expectedCode)
				return true;
		}

		return false;
	}

	public string GenerateSetupCode(string issuer, string accountName, string secretKey, string algorithm = "SHA1", int digits = 6, int period = 30)
	{
		var uri = $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(accountName)}";
		uri += $"?secret={secretKey}";
		uri += $"&issuer={Uri.EscapeDataString(issuer)}";
		uri += $"&algorithm={algorithm}";
		uri += $"&digits={digits}";
		uri += $"&period={period}";
		return uri;
	}
}
```

## Recommendation

**Use Option 1 (Otp.NET)** if you want the quickest solution with a maintained library.

**Use Option 2** if the GoogleAuthenticator project is available and you want to keep the original design.

**Use Option 3** if you want zero external dependencies for TOTP functionality (good for understanding the algorithm).

## After Choosing an Option

1. Update `Services/TotpService.cs` with the appropriate code
2. If using Option 1 or 3, update namespace/using statements
3. Test with a known 2FA account (e.g., generate a code and compare with Google Authenticator app)
4. Verify the `ViewModels/TwoFactorViewModel.cs` timer correctly updates codes every second

## Test Data

To test your TOTP implementation, use this test account:

```
Secret Key: JBSWY3DPEHPK3PXP
Expected Codes (varies by time):
- At Unix time 1234567890: Code should be 89005924 (6 digits, SHA1, 30s period)
```

Generate a code and verify it matches Google Authenticator or Authy with the same secret key.
