# Phase G — Shared Vault

**Status:** Done (initial implementation)  
**Depends on:** Phases A–C, E–F  
**Next phase:** —

## Goal

Shared SQL Express as multi-machine source of truth for 2FA + full password vault; LAN discovery; shared VDEK; offline encrypted backup; providers via WealthForge `API/PasswordManager`; Diff/sync; auto-fill. **No MVVM.**

## Slices

| ID | Outcome | Status |
|----|---------|--------|
| **G0** | Host vs Join installer; SQL Express host bootstrap; UDP discovery | Done |
| **G1** | Connection profiles Settings UX + `connection.json` | Done |
| **G2** | Vault schema + VDEK recovery enrollment + offline backup | Done |
| **G3** | Migrate local 2FA → shared; seed offline backup; favorites | Done |
| **G4** | Top tabs + contextual bottom bar + favorites + offline banner | Done |
| **G5** | All PasswordManager provider façades; bulk import | Done |
| **G6** | Diff/match window + mismatch indicators | Done |
| **G7** | Vault item CRUD, un-hide, copy, folders, custom fields | Done |
| **G8** | Password generator | Done |
| **G9** | Offline queue + reconnect reconcile | Done |
| **G10** | Foreground monitor → auto-filter | Done |
| **G11** | Auto-fill / auto-login (+ 2FA) | Done |

## Connection JSON + discovery protocol

See `ConnectionConfig` and `SqlDiscoveryService`. UDP port **38455**, probe tag `AuthenticatorSql/1`.

Beacon reply (JSON):

```json
{
  "product": "Authenticator",
  "version": 1,
  "server": "HOST\\SQLEXPRESS",
  "database": "Authenticator",
  "port": 1433,
  "vdekFingerprintPrefix": "a1b2c3d4"
}
```

WealthForge may mirror the same `connection.json` shape and probe protocol.

## Exit criteria

- [x] Host can install/configure shared Express; Join discovers without hard-coded names
- [x] Shared VDEK unlocks via existing PIN/Yubi/Hello; recovery enrolls new machines
- [x] 2FA + vault items on shared SQL; offline backup works when Shared unreachable
- [x] Import/sync from all `API/PasswordManager` backends (file/LastPass live; others via JSON export façade)
- [x] Diff/mismatch, generator, auto-fill/login, favorites across types
- [x] WPF code-behind only (no MVVM)

## Follow-ups

- Interactive MFA UI for remaining cloud providers (Bitwarden/Dashlane/etc. still JSON façade; LastPass Connect/Import prompts OTP/YubiKey like WealthForge)
- Always-on discovery helper when Host app is closed
- Unify 2FA Folders with VaultFolders
- **G10/G11 harden:** ranked host match for Relevant + per-entry **Auto-fill** / **Auto-login**. **Web autofill** is the Chrome/Edge/Firefox extension (`Authenticator.Browser`) via native messaging + `VaultNativePipeServer` (UIA fill abandoned for browser pages).
- **Next cycle — Diff try-selection:** from Diff, auto-test the current field selection (open site URL in browser → auto-login/fill attempt) so the user can see whether that password/source worked before Apply

## Reconcile / consolidate (G6+)

Vault is the consolidation hub across providers (not a third silo of duplicates).

- **Identity** = normalized **username + host** (strip `www.`; ignore path/query; **include non-default port** so `host:5000` ≠ `host:8123`). Username+host only consolidates when **titles are compatible** (equal / substring / one empty) — e.g. Media vs HomeAssistant on the same host must not merge. Fallback: title+username when host/username missing.
- **Same-provider origins never collapse**: if a vault row already has an `ExternalLink` for that provider, a different origin id creates a new vault row (avoids overwriting ExternalId + false Diff conflicts on clean import).
- **Soft-merge** is primary: fill empty vault fields from provider; keep vault when both sides have non-empty non-password values.
- **Password conflict** only when both sides have non-empty differing passwords (compare after decrypt) → mismatch badge → Diff. Matching/empty passwords → aligned (no Diff).
- Clean slate + **one** linked provider: skip auto conflict scan (vault came from that store). Diff/reconcile still available for edge cases. Auto-scan when **two or more** providers have links.
- Do **not** use full content-hash equality for aligned vs conflict (provider formatting noise).
- **Multi-source Diff**: columns = Vault + every linked provider. Shows **Created/Updated** as info-only (not match/selection indicators; KeePass times; LastPass `created_gmt` / `last_pwchange_gmt`/`last_modified_gmt`). Sky highlight + **This one is newer** on the source with the latest Updated (else Created) when timestamps can be compared. Import/soft-merge **preserves provider Created/Updated** (Save does not stamp now on import/scan) so vault is not always “newer” on first load. **Per-field click** picks that value (primary). **Slow second click** on a selected Vault field (Explorer-style rename) inline-edits that value before Apply. Green = matches vault; amber = differs; blue ring = selected for Apply. Window sizes to content within the work area. **Use all fields** is optional whole-source. **Apply to vault** then confirm **Update all linked sources** (KeePass disk save; LastPass `show_website.php` while session is connected). Unlink under each provider column.
- **Provider commit invariant**: when the user confirms **Update all linked sources** (Diff Apply or history Undo→Update all), every linked provider with `SupportsWriteBack` must receive a **real** durable write — not in-memory-only. Surface push failures in the Apply/Undo result; do not report success for a provider that only updated the live session cache.
- **Reconcile history + undo**: `ReconcileHistory` table stores sealed pre-Apply vault snapshots (keep 20/item). Diff **History…** lists per-item applies; Vault Providers **Reconcile history…** is the global recovery list. Undo defaults to vault restore; **Update all** also pushes restored values to linked providers.
- **Developer purge**: hard-deletes Shared vault items/links/history (verified remaining=0, SQL fallback); clears Conflicts UI; does not remove Connect credentials.
- **Unlock**: password managers are **not** reconnected at unlock (keeps startup fast). They connect lazily via `EnsureConnectedAsync` on Sync / Diff / Providers → Reconnect. Browser extension native pipe (`VaultNativePipeServer`) starts on unlock and stops on lock.

## Provider connect vs import (G5+)

- **Connect to…** — credentials DPAPI-persisted in `provider-connections.bin`; live session restored after unlock until **Disconnect**
- **Import from…** — one-shot; credentials not retained
- Vault bottom bar **Providers** menu: Connect / Import / Sync all / per-provider Sync & Disconnect

## Starter prompt

```
Phase G only — Shared Vault for D:\Source\Authenticator.

Read docs/phases/README.md and docs/phases/G.md. Implement remaining unchecked slices.
Stay WPF code-behind; no MVVM. SQL Express stays as shared SoR.
```
