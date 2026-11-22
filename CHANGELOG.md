# Changelog

All notable changes to Authenticator are documented here.

## [1.1.0] — 2026-11-22

### Added

- Browser extension ambiguous-match popup picker (pick among exact Auto-fill logins).
- Equivalent domains: per-login “Also match domains” and Settings global domain groups for autofill / Relevant.
- Inno Setup ships `Browser\` extension (optional CRX) and `NativeHost\`; post-install native messaging registration and best-effort Chromium force-install.
- Setup silently uninstalls the previous AppId install before upgrading.

### Changed

- Version bumped to 1.1.0 across WPF, Crypto, NativeHost, and the browser extension.

## [1.0.1] — 2026-11-14

### Changed

- Moved TOTP and crypto helpers into `Authenticator.Crypto`.
- Removed unused SQL CLR `dbo.GetTotp` project and install scripts from the solution.

## [1.0.0] — 2026-11-11

### Added

- Inno Setup `AuthenticatorSetup` installer (`packaging/build-setup.ps1`) that detects `.\SQLEXPRESS` and installs SQL Server Express when missing.
- Configurable SQL connection via `%LocalAppData%\Authenticator\connection.json` (defaults to `.\SQLEXPRESS` / `Authenticator`).
- First-run setup requiring PIN and/or Windows Hello, with optional JSON or Google Authenticator **export** import (no Google sign-in).
- App version shown at the bottom of Settings.
- Self-contained win-x64 publish for fresh machines without a separate .NET install.

### Changed

- Target storage documented as SQL Server Express (not SQLite / WinUI).
- Logs write under `%LocalAppData%\Authenticator\logs`.
