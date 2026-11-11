# Phase B — Migration / interop

**Status:** Done  
**Depends on:** Phase A exit (codes UI stable enough to land imported accounts)  
**Next phase:** [C.md](C.md)

## Goal

Let users leave Google Authenticator (and similar apps) **without ever signing into Google**.

## Slices (PR each)

| ID | Outcome |
|----|---------|
| **B1** Google Authenticator export import | Parse migration QR / export payload → create accounts |
| **B2** Harden existing JSON import/export | Clearer conflicts/skips; round-trip confidence |
| **B3** Optional extra formats | e.g. Aegis — only if needed after B1 *(deferred)* |

Order: **B1 → B2 → (B3 if requested)**.

## Key files

- `Authenticator.Wpf/SettingsView.xaml(.cs)` (current import/export)
- `Authenticator.Wpf/Services/DatabaseService.cs`
- `Authenticator.Wpf/Services/QrCodeService.cs` / `AddAccountWindow` / `QrCaptureWindow`
- `Authenticator.Wpf/Services/GoogleMigrationParser.cs`

## Exit criteria

- [x] User can import from Google Authenticator export/migration without a Google login
- [x] Existing JSON export/import remains usable and documented in UI copy
- [x] Duplicates handled predictably (skip or merge — document choice) — **skip** by service+username+secret
- [x] Imported accounts show correct TOTP on Codes view

## Notes for Phase C

- Account secrets remain **Base32 plaintext** in SQLite and in JSON export (`formatVersion: 1`). Google migration secrets arrive as raw bytes and are converted to Base32 (padding stripped) before storage.
- JSON export intentionally includes plaintext secrets; treat encryption-at-rest / export passphrase as Phase C work.
- Duplicate policy is skip-only (no merge/overwrite); preserve that if C adds encrypted import/export.
- HOTP entries in Google migration payloads are skipped (app is TOTP-only).

## Starter prompt (paste into new chat)

```
Phase B only — Migration/interop for D:\Source\Authenticator.

Read docs/phases/README.md and docs/phases/B.md. Confirm Phase A is Done (or note gaps). Implement B1 then B2 (B3 only if clearly needed).

Product rule: local-first; Google Authenticator *export import* is OK; Google sign-in is not.

When done: check off exit criteria in docs/phases/B.md, set Status to Done, note crypto/format assumptions Phase C should respect.
```
