# Packaging

## Build Setup.exe

1. Install [Inno Setup 6.4+](https://jrsoftware.org/isinfo.php) (needs `DownloadTemporaryFile`).
2. From repo root:

```powershell
.\packaging\build-setup.ps1
```

Output: `packaging\dist\AuthenticatorSetup-<version>.exe`

## Behavior

- Publishes `Authenticator.Wpf` self-contained `win-x64` into `packaging\publish\`
- Setup requires administrator rights
- If registry instance `SQLEXPRESS` is missing, downloads SQL Server Express bootstrapper and silent-installs it
- Writes `%LocalAppData%\Authenticator\connection.json` for the installing user

Large Express redistributables are **not** committed; they are downloaded at install time (or you may stage files under `prereqs\` for local experiments — gitignored).
