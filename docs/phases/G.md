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

- Interactive MFA UI for cloud providers beyond LastPass silent/JSON path
- Provider write-back for managers that support API updates
- Always-on discovery helper when Host app is closed
- Unify 2FA Folders with VaultFolders

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
