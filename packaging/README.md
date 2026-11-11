# Packaging

## Build Setup.exe

1. Install [Inno Setup 6.4+ / 7](https://jrsoftware.org/isinfo.php) (needs `DownloadTemporaryFile`). Per-user and Program Files installs are detected.
2. From repo root:

```powershell
.\packaging\build-setup.ps1
```

Output: `packaging\dist\AuthenticatorSetup-<version>.exe`

## Behavior

- Publishes `Authenticator.Wpf` self-contained `win-x64` into `packaging\publish\`
- Setup requires administrator rights
- If registry instance `SQLEXPRESS` is missing, downloads SQL Server 2022 Express Core (`SQLEXPR_x64_ENU.exe`, ~280 MB), extracts it, and silent-installs instance `SQLEXPRESS`
- Writes `%LocalAppData%\Authenticator\connection.json` for the installing user

Large Express redistributables are **not** committed; they are downloaded at install time.
