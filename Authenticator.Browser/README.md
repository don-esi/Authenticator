# Authenticator Vault browser extension

Auto-fills logins from the unlocked Authenticator desktop vault via native messaging.

Supports **Chrome**, **Edge**, and **Firefox** (MV3).

## Setup

1. Unlock Authenticator (starts the vault TCP bridge on `127.0.0.1:38456`).
2. Build the native host (or use the copy shipped by Setup under `{app}\NativeHost`):

```powershell
dotnet publish D:\Source\Authenticator\Authenticator.NativeHost\Authenticator.NativeHost.csproj -c Release
```

3. Register the host for all three browsers:

```powershell
powershell -ExecutionPolicy Bypass -File D:\Source\Authenticator\Authenticator.Browser\register-native-host.ps1
```

4. Load this folder as an unpacked extension (Setup also tries Chromium force-install when a signed CRX was packed; consumer Chrome often still needs Load unpacked):

| Browser | Page |
|---------|------|
| Chrome | `chrome://extensions` → Developer mode → Load unpacked |
| Edge | `edge://extensions` → Developer mode → Load unpacked |
| Firefox | `about:debugging` → This Firefox → Load Temporary Add-on → select `manifest.json` |

Stable Chromium extension id (from manifest `key`): `bfcajmeodjdgaomgpanlihipagjkmlih`  
Firefox id: `authenticator@local`

Do **not** put `chrome-extension.pem` in this folder — Chrome Load unpacked will refuse/warn. The private key lives at `packaging\chrome-extension.pem` (gitignored) for CRX packing only.

Store listing (CWS / Edge Add-ons / AMO) is deferred.

## Seeing the extension

After Load unpacked, open the **puzzle / extensions** menu and **pin** “Authenticator Vault” so the shield / 2FA icon stays on the toolbar.

| Badge | Meaning |
|-------|---------|
| (empty) | Idle / no exact Auto-fill match |
| `✓` green | Credentials filled |
| `!` amber | Vault locked or native host unreachable |
| `?` blue | Multiple exact matches — open the popup and pick a login |
| `x` red | Fill message failed |

Reload the extension after pulling changes (`chrome://extensions` → Reload).

## Behavior

- On tab load / activation, asks the desktop vault for credentials for the page origin.
- Auto-fills only when exactly one vault login has **Auto-fill** enabled and an **exact host** match (including per-login “Also match domains” and Settings → Equivalent domains groups).
- When several logins match, the toolbar badge shows `?`; open the popup to pick one.
- **Auto-fill** fills email, clicks Continue/Next when needed, then fills password.
- **Auto-login** also clicks Sign in / Submit after the password.

Vault must be unlocked; otherwise the toolbar badge shows `!`.
