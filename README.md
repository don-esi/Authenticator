# Authenticator

Local-first **WPF** 2FA authenticator for Windows. Codes stay on your machine — **no Google account**, **no password vault**.

## Requirements

- Windows 10 version 2004+ (build 19041) or Windows 11
- **SQL Server Express** with instance name **`SQLEXPRESS`** (the Setup package installs this when missing)
- Optional: Windows Hello, YubiKey (OTP wedge)

## Install (recommended)

1. Build the setup (or use a published `AuthenticatorSetup-*.exe`):

```powershell
.\packaging\build-setup.ps1
```

Requires [Inno Setup 6.4+](https://jrsoftware.org/isinfo.php) and the .NET 11 SDK.

2. Run `packaging\dist\AuthenticatorSetup-1.0.0.exe` **as administrator**.
3. If `.\SQLEXPRESS` is not present, Setup downloads and silently installs SQL Server Express.
4. Launch Authenticator, complete first-run (PIN and/or Windows Hello), optionally import a backup.

Connection used after install:

`%LocalAppData%\Authenticator\connection.json` → `Server=.\SQLEXPRESS`, `Database=Authenticator`

### Updating

Install the newer `AuthenticatorSetup-*.exe` over the existing install (Inno upgrades in place). Your LocalAppData credentials and SQL database are preserved.

## Develop

```powershell
dotnet run --project Authenticator.Wpf\Authenticator.Wpf.csproj
```

Point SQL at your instance by writing `%LocalAppData%\Authenticator\connection.json`:

```json
{
  "Server": ".\\SQLEXPRESS",
  "Database": "Authenticator"
}
```

Dev machines can use another instance (for example `.\\ZOGEXPRESS2025`) via the same file. If the file is missing, the app defaults to `.\\SQLEXPRESS`.

Open `Authenticator.slnx` in Visual Studio if you prefer.

## Features

- TOTP codes: folders, search, sort, condensed / large / squares views
- Add account via QR capture or manual entry; backup codes tab
- Lock: PIN, YubiKey OTP, Windows Hello; secrets encrypted at rest when unlock is configured
- Import / export JSON; Google Authenticator **export** (migration QR / URI) — not Google sign-in
- Desktop: dock-to-edge handle, tray, global hotkey

## Data layout

| Location | Contents |
|----------|----------|
| SQL Server `Authenticator` DB | Accounts, folders, backup codes, window/dock settings |
| `%LocalAppData%\Authenticator\lock-credentials.bin` | Unlock methods (DPAPI) |
| `%LocalAppData%\Authenticator\codes-ui-preferences.json` | Theme, tray, hotkey, dock handle prefs |
| `%LocalAppData%\Authenticator\connection.json` | SQL Server / database |
| `%LocalAppData%\Authenticator\logs\` | Rolling Serilog files |

Optional admin/dev scripts live under `DBSchema\` (not required for normal Setup installs; the app creates the database on first run).

## Phases

See [docs/phases/README.md](docs/phases/README.md). Product north star: local-first WPF 2FA — no Google login, no password storage.
