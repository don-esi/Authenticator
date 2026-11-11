# Phase C — Security hardening

**Status:** Done  
**Depends on:** Phase B preferred (import paths exist before locking down storage format)  
**Next phase:** [E.md](E.md)

## Goal

Credible local security: secrets not casual plaintext, unlock options beyond PIN/Yubi, codes don’t leak on idle.

## Slices (PR each)

| ID | Outcome |
|----|---------|
| **C1** Encrypt DB / secrets at rest | Key material tied to user unlock (e.g. DPAPI + app secret); migrate existing DB safely |
| **C2** Windows Hello unlock | Parallel unlock path beside PIN/Yubi on lock overlay |
| **C3** Idle mask + lock polish | Mask codes on inactivity; fix focus/lock races |

Order: **C1 → C2 → C3**.

## Key files

- `Authenticator.Wpf/Services/LockCredentialStore.cs`
- `Authenticator.Wpf/Services/VaultKeyService.cs`
- `Authenticator.Wpf/Services/SecretProtector.cs`
- `Authenticator.Wpf/Services/SecretMigrationService.cs`
- `Authenticator.Wpf/Services/WindowsHelloUnlockService.cs`
- `Authenticator.Wpf/MainWindow.xaml(.cs)` (lock overlay)
- `Authenticator.Wpf/Services/YubiKeyUnlockService.cs`
- `Authenticator.Wpf/Data/*`, `DatabaseService`, `DatabaseInitializer`
- Settings unlock UI in `SettingsView`

## Exit criteria

- [x] Account secrets are not plaintext-at-rest in the obvious DB fields when unlock is configured
- [x] Existing installs migrate or re-encrypt without data loss (or clear one-time migration UX)
- [x] Windows Hello can unlock when available; PIN/Yubi still work
- [x] Idle behavior masks and/or locks consistently with settings
- [x] Still no cloud/Google identity

## Key / unlock assumptions

- **Storage:** SQL Server (`Authenticator` on `.\ZOGEXPRESS2025`), not SQLite. Secrets live in `Account.SecretKey` and `BackupCode.Code` as AES-GCM ciphertext with prefix `v1:` when an unlock method is configured.
- **DEK:** 32-byte random data-encryption key. Present in memory only while unlocked (`VaultKeyService`). Cleared on full lock — not on idle mask.
- **Wraps:** Each unlock method stores an AES-GCM wrap of the same DEK inside DPAPI-protected `%LocalAppData%\Authenticator\lock-credentials.bin`.
  - **PIN:** PBKDF2-SHA256 (100k) → KEK → unwrap-as-verify.
  - **YubiKey:** OTP validates presence; then a DPAPI-stored Yubi slot KEK unwraps the DEK (OTP is not a cryptographic secret in-app).
  - **Windows Hello:** `UserConsentVerifier` gates access; then a DPAPI-stored Hello KEK unwraps the DEK.
- **Migration:** First successful unlock (or first unlock-method enroll while unlocked) encrypts legacy plaintext fields in place.
- **Last method:** Cannot remove the last unlock method while secrets are encrypted.
- **Export:** JSON `formatVersion: 1` remains **plaintext secrets** by design (interop); Settings warns the user.
- **Out of scope:** Password manager / `PasswordEntry` vault — Phase D removed; do not extend crypto for password storage.

## Starter prompt (paste into new chat)

```
Phase C only — Security hardening for D:\Source\Authenticator.

Read docs/phases/README.md and docs/phases/C.md. Implement C1→C2→C3. Reuse the existing lock overlay model (PIN/Yubi); extend it rather than replacing it.

Document key/unlock assumptions in the phase file when done. Do not build a password vault.

Product rule: local-first 2FA only; no Google account; no password storage.

When done: check off exit criteria in docs/phases/C.md, set Status to Done.
```
