# Phase A — Codes UX polish

**Status:** Done  
**Depends on:** nothing  
**Next phase:** [B.md](B.md)

## Goal

Make the main **2FA Codes** screen feel finished for daily use (parity with Store list UX, minus Google chrome).

## Slices (PR each)

| ID | Outcome |
|----|---------|
| **A1** Favorites | Star/toggle on accounts; persist `Account.IsFavorite`; **favorites pin first**, then the chosen sort within each group |
| **A2** Privacy mask | Deferred / removed — hide-codes control did not add enough value |
| **A3** Theme | Light / dark / system; persist; apply across MainWindow + overlays |
| **A4** Codes chrome | Empty states, toast consistency, folder/sort/view edge cases; condensed remains default |

Suggested order: **A1 → A2 → A4 → A3** (theme can slip later if needed).

## Key files

- `Authenticator.Wpf/CodesView.xaml(.cs)`
- `Authenticator.Wpf/Services/CodesUiPreferences.cs`
- `Authenticator.Wpf/Services/ThemeService.cs`
- `Authenticator.Wpf/Data/Models/Account.cs` (`IsFavorite`)
- `Authenticator.Wpf/Services/SettingsService.cs` / `AppSettings` (window/auto-lock only; mask/theme live in LocalAppData prefs — no DB migration)
- `Authenticator.Wpf/MainWindow.xaml(.cs)`, `App.xaml`

## Exit criteria

- [x] Favorites work and survive restart
- [x] Codes can be masked and revealed without unlocking friction for the owner *(waived — hide-codes UX removed)*
- [x] Theme preference persists and covers main surfaces
- [x] Condensed view still default; sort/view/folder flows stable
- [x] No Google account / identity prompts introduced

## Notes for Phase B

- Favorites: DB column `Account.IsFavorite` via `DatabaseService.SetAccountFavoriteAsync`; list order is favorites-first then sort mode.
- Theme: `codes-ui-preferences.json` under LocalAppData (`CodesUiPreferences` shared static cache). Settings APPEARANCE picks light/dark/system. `ThemeService` swaps DynamicResource brushes (MainWindow, lock overlay, Codes/Settings/Backup, Add/Folder dialogs). System follows Windows AppsUseLightTheme. Hide-codes mask UI was removed.
- Some FolderWindow/AddAccountWindow chrome still has a few hardcoded light accents; extend ThemeService brushes if needed.

## Starter prompt (paste into new chat)

```
Phase A only — Codes UX polish for D:\Source\Authenticator.

Read docs/phases/README.md and docs/phases/A.md. Implement Phase A slices in order (A1→A2→A4→A3 unless A3 is deferred). One coherent change set per slice; match existing WPF patterns in Authenticator.Wpf.

Product rule: local-first replica of Store Authenticator UX; no Google account requirement.

When done: check off exit criteria in docs/phases/A.md, set Status to Done, list anything Phase B should know.
```
