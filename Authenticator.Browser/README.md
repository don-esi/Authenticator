# Authenticator Vault browser extension

Auto-fills logins from the unlocked Authenticator desktop vault via native messaging.

Supports **Chrome**, **Edge**, and **Firefox** (MV3).

## Setup

1. Unlock Authenticator (starts the vault named pipe).
2. Build the native host:

```powershell
dotnet build D:\Source\Authenticator\Authenticator.NativeHost\Authenticator.NativeHost.csproj -c Release
```

3. Register the host for all three browsers:

```powershell
powershell -ExecutionPolicy Bypass -File D:\Source\Authenticator\Authenticator.Browser\register-native-host.ps1
```

4. Load this folder as an unpacked extension:

| Browser | Page |
|---------|------|
| Chrome | `chrome://extensions` → Developer mode → Load unpacked |
| Edge | `edge://extensions` → Developer mode → Load unpacked |
| Firefox | `about:debugging` → This Firefox → Load Temporary Add-on → select `manifest.json` |

Stable Chromium extension id (from manifest `key`): `apolfgjbnoelcpahldnnlmbjnbfjleil`  
Firefox id: `authenticator@local`

## Seeing the extension

After Load unpacked, open the **puzzle / extensions** menu and **pin** “Authenticator Vault” so the blue **A** icon stays on the toolbar.

| Badge | Meaning |
|-------|---------|
| (empty) | Idle / no exact Auto-fill match |
| `✓` green | Credentials filled |
| `!` amber | Vault locked or native host unreachable |
| `?` blue | Multiple exact matches |
| `x` red | Fill message failed |

Reload the extension after pulling changes (`chrome://extensions` → Reload).

## Behavior

- On tab load / activation, asks the desktop vault for credentials for the page origin.
- Auto-fills only when exactly one vault login has **Auto-fill** enabled and an **exact host** match.
- **Auto-fill** fills email, clicks Continue/Next when needed, then fills password.
- **Auto-login** also clicks Sign in / Submit after the password.

Vault must be unlocked; otherwise the toolbar badge shows `!`.
