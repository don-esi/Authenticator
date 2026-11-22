# Packaging

## Build Setup.exe

1. Install [Inno Setup 6.4+ / 7](https://jrsoftware.org/isinfo.php) (needs `DownloadTemporaryFile`). Per-user and Program Files installs are detected.
2. From repo root:

```powershell
.\packaging\build-setup.ps1
```

Optional CRX pack (stable Chromium id `bfcajmeodjdgaomgpanlihipagjkmlih`) when `packaging\chrome-extension.pem` exists (gitignored — keep it **out** of `Authenticator.Browser` so Chrome Load unpacked does not warn). Regenerate with:

```powershell
dotnet run --project .\packaging\GenerateChromePem -- .\Authenticator.Browser
.\packaging\build-setup.ps1
# or -ChromeExtensionPem / CHROME_EXTENSION_PEM
```

`.chrome-key.b64` is the **public** key (manifest `key` / extension id). The PEM is private — do not commit it. Chrome/Edge/Firefox **store listing** is deferred.

Output: `packaging\dist\AuthenticatorSetup-<version>.exe`

## Behavior

- Publishes `Authenticator.Wpf` self-contained `win-x64` into `packaging\publish\`
- Publishes `Authenticator.NativeHost` → `publish\NativeHost\`
- Copies unpacked browser extension → `publish\Browser\` (and packs `AuthenticatorVault.crx` when a PEM is provided)
- Setup requires administrator rights
- On start: silently uninstalls any previous Authenticator install with the same AppId (`InitializeSetup`), clears `{app}\Browser` / `{app}\NativeHost`, then installs a clean tree including the extension and native host
- Post-install: registers native messaging (Chrome/Edge/Firefox HKCU) and **best-effort** HKLM `ExtensionInstallForcelist` when a CRX exists (`file://` update URLs are often ignored on consumer Chrome — Load unpacked from `{app}\Browser` remains the fallback)
- If **any** local SQL Server instance/service is already present (default or named, including non-`SQLEXPRESS`), skips Express download/install
- Only when no SQL Server is found: downloads SQL Server 2022 Express Core (`SQLEXPR_x64_ENU.exe`, ~280 MB), extracts it, and silent-installs instance `SQLEXPRESS`
- Writes `%LocalAppData%\Authenticator\connection.json` only if missing (prefers `.\SQLEXPRESS`, else an existing named/default instance)

Large Express redistributables are **not** committed; they are downloaded at install time.
