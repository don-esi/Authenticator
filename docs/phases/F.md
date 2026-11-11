# Phase F — Ship

**Status:** Done  
**Depends on:** Phases A–C and E far enough that the packaged build is what you actually want people to run  

**Next phase:** none (maintenance)

## Goal

Someone else can install and use the app without you.

## Slices (PR each)

| ID | Outcome |
|----|---------|
| **F1** Installer | Inno Setup `Setup.exe`; self-contained win-x64; detects `.\SQLEXPRESS` and installs **SQL Server Express** when missing; writes `%LocalAppData%\Authenticator\connection.json`; app under Program Files |
| **F2** Update path | Versioning in csproj + Settings; `CHANGELOG.md`; sideload = run newer Setup |
| **F3** First-run | PIN/Hello required; optional JSON / Google Authenticator *export* import — **no Google login** |

Order: **F1 → F2 → F3**.

## Key files

- `packaging/Authenticator.iss`, `packaging/build-setup.ps1`
- `Authenticator.Wpf/Data/ConnectionConfig.cs`, `AuthenticatorDbContext.cs`
- `Authenticator.Wpf/FirstRunWindow.xaml(.cs)`
- `App.xaml.cs` / `MainWindow.xaml.cs` first-run gating
- `docs/phases/README.md` + README.md accuracy (WPF, not stale WinUI claims)

## Exit criteria

- [x] Fresh machine install works (Setup + Express bootstrap + self-contained publish)
- [x] Version visible in UI or about/settings
- [x] First-run sets unlock method and offers import without Google account
- [x] README matches the shipped product

## Starter prompt (paste into new chat)

```
Phase F only — Ship/package for D:\Source\Authenticator.

Read docs/phases/README.md and docs/phases/F.md. Implement F1→F2→F3. First-run must never require Google sign-in; optional Google Authenticator *export* import is fine.

When done: check off exit criteria in docs/phases/F.md, set Status to Done, and refresh README to match reality.
```
