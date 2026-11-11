# Phase F — Ship

**Status:** Not started  
**Depends on:** Phases A–C and E far enough that the packaged build is what you actually want people to run  

**Next phase:** none (maintenance)

## Goal

Someone else can install and use the app without you.

## Slices (PR each)

| ID | Outcome |
|----|---------|
| **F1** Installer | MSIX and/or Setup.exe; clean `%LocalAppData%\Authenticator` layout |
| **F2** Update path | Versioning, changelog, sideload or Store update story |
| **F3** First-run | PIN/Hello setup, optional import (B1), privacy defaults — **no Google login** |

Order: **F1 → F2 → F3**.

## Key files

- Packaging projects / publish profiles (create as needed)
- `App.xaml.cs` first-run gating
- `docs/phases/README.md` + README.md accuracy (WPF, not stale WinUI claims)

## Exit criteria

- [ ] Fresh machine install works
- [ ] Version visible in UI or about/settings
- [ ] First-run sets unlock method and offers import without Google account
- [ ] README matches the shipped product

## Starter prompt (paste into new chat)

```
Phase F only — Ship/package for D:\Source\Authenticator.

Read docs/phases/README.md and docs/phases/F.md. Implement F1→F2→F3. First-run must never require Google sign-in; optional Google Authenticator *export* import is fine.

When done: check off exit criteria in docs/phases/F.md, set Status to Done, and refresh README to match reality.
```
