# Phase handoffs (one chat per phase)

Product north star: local-first **WPF authenticator** with Store-app-like codes UX — **no Google account requirement**. Google Authenticator *export import* is allowed; Google sign-in is not. **Phase G** adds a full password vault on shared SQL Express (multi-machine), with offline encrypted backup and import/sync from WealthForge `API/PasswordManager` backends.

## How to start a phase chat

1. Open a **new** Agent chat on this repo.
2. Paste the **Starter prompt** from that phase file (`A.md`, `B.md`, `C.md`, `E.md`, `F.md`, `G.md`).
3. Do not start the next phase until the current phase **Exit criteria** are met.
4. When finishing a phase, update that phase file’s **Status** and note any follow-ups for the next chat.

## Order

```
A (Codes UX) → B (Interop) → C (Security) → E (Desktop) → F (Ship) → G (Shared Vault)
```

Phase **D (Passwords) was removed historically** — vault work is **Phase G**, not a revival of D.

Phase **E** includes **dock-to-desktop**: relocatable red half-circle edge handle; window flies out from that side (see `E.md`).

Within a phase, implement slices in the order listed (A1→A2→…). Prefer **one PR per slice**.

## Already done (do not re-litigate unless broken)

- TOTP codes list: folders, search, sort, view modes (condensed/large/squares)
- Add account (QR capture / manual), backup codes tab
- Lock overlay: PIN and/or YubiKey (OTP wedge), Windows Hello (Phase C)
- Settings: auto-lock, import/export JSON, Google Authenticator migration import
- Secret encryption at rest while unlock methods are configured (Phase C)
- Stack: **WPF** under `Authenticator.Wpf` (code-behind; no MVVM)
- Ship: Inno Setup + SQL Express (`.\SQLEXPRESS`) bootstrap, first-run unlock, version in Settings (Phase F)
