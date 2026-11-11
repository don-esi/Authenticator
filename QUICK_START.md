# Quick start

## Install

```powershell
.\packaging\build-setup.ps1
```

Run `packaging\dist\AuthenticatorSetup-*.exe` as administrator. Setup installs SQL Server Express (`SQLEXPRESS`) if that instance is missing.

## Develop

```powershell
dotnet run --project Authenticator.Wpf\Authenticator.Wpf.csproj
```

Ensure SQL Server is reachable. Default: `.\SQLEXPRESS` / database `Authenticator`. Override with `%LocalAppData%\Authenticator\connection.json`.

See [README.md](README.md) for data layout and updating.
