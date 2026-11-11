# Changelog

All notable changes to Authenticator are documented here.

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
