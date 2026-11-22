using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

static string ToPem(string label, byte[] der)
{
    var b64 = Convert.ToBase64String(der);
    var sb = new StringBuilder();
    sb.Append("-----BEGIN ").Append(label).AppendLine("-----");
    for (var i = 0; i < b64.Length; i += 64)
        sb.AppendLine(b64.Substring(i, Math.Min(64, b64.Length - i)));
    sb.Append("-----END ").Append(label).AppendLine("-----");
    return sb.ToString();
}

static string ChromeExtensionId(byte[] spki)
{
    var hash = SHA256.HashData(spki);
    var chars = new char[32];
    for (var i = 0; i < 16; i++)
    {
        chars[i * 2] = (char)('a' + ((hash[i] >> 4) & 0xF));
        chars[i * 2 + 1] = (char)('a' + (hash[i] & 0xF));
    }
    return new string(chars);
}

static void ReplaceAllChromeIds(string path, string newId)
{
    if (!File.Exists(path))
        return;
    var text = File.ReadAllText(path);
    var updated = Regex.Replace(text, @"\b[a-p]{32}\b", newId);
    if (!string.Equals(text, updated, StringComparison.Ordinal))
    {
        File.WriteAllText(path, updated);
        Console.WriteLine($"Updated ids in {path}");
    }
}

var browserDir = args.Length > 0
    ? args[0]
    : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Authenticator.Browser"));

if (!Directory.Exists(browserDir))
    throw new DirectoryNotFoundException(browserDir);

using var rsa = RSA.Create(2048);
var privatePkcs8 = rsa.ExportPkcs8PrivateKey();
var publicSpki = rsa.ExportSubjectPublicKeyInfo();
var publicB64 = Convert.ToBase64String(publicSpki);
var extId = ChromeExtensionId(publicSpki);

var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
// Keep the private key OUT of the unpacked extension folder — Chrome warns / rejects Load unpacked otherwise.
var packagingDir = Path.GetFullPath(Path.Combine(browserDir, "..", "packaging"));
Directory.CreateDirectory(packagingDir);
var pemPath = Path.Combine(packagingDir, "chrome-extension.pem");
File.WriteAllText(pemPath, ToPem("PRIVATE KEY", privatePkcs8), utf8);
foreach (var stray in new[]
         {
             Path.Combine(browserDir, "chrome-extension.pem"),
             Path.Combine(browserDir, "private-key.pem")
         })
{
    if (File.Exists(stray))
        File.Delete(stray);
}

File.WriteAllText(Path.Combine(browserDir, ".chrome-key.b64"), publicB64, utf8);
File.WriteAllText(Path.Combine(browserDir, ".chrome-extension-id.txt"), extId + Environment.NewLine, utf8);

var manifestPath = Path.Combine(browserDir, "manifest.json");
var node = JsonNode.Parse(File.ReadAllText(manifestPath))
           ?? throw new InvalidOperationException("manifest.json parse failed");
node["key"] = publicB64;
var json = node.ToJsonString(new JsonSerializerOptions
{
    WriteIndented = true,
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
});
File.WriteAllText(manifestPath, json + Environment.NewLine, utf8);

Console.WriteLine($"PEM: {pemPath}");
Console.WriteLine($"ID:  {extId}");

ReplaceAllChromeIds(Path.Combine(browserDir, "register-native-host.ps1"), extId);
ReplaceAllChromeIds(Path.Combine(packagingDir, "build-setup.ps1"), extId);
ReplaceAllChromeIds(Path.Combine(packagingDir, "Authenticator.iss"), extId);
ReplaceAllChromeIds(Path.Combine(browserDir, "README.md"), extId);
ReplaceAllChromeIds(Path.Combine(packagingDir, "README.md"), extId);
