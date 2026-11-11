# Phase E — Desktop convenience

**Status:** Done  
**Depends on:** Phase C3 preferred (mask/lock so tray/hotkeys don’t casually leak codes)  
**Next phase:** [F.md](F.md)

## Goal

Windows companion behavior: available without babysitting a big window — especially **dock-to-desktop** with a small edge handle and a side fly-out.

## Slices (PR each)

| ID | Outcome |
|----|---------|
| **E1** Tray + minimize-to-tray | Notify icon; restore; exit |
| **E2** Global hotkey | Show window and/or copy flow — respect lock + privacy mask |
| **E3** Dock to desktop (primary UX) | Collapsed = **relocatable red half-circle** on the screen edge; click/drag handle; main window **flies out from that side**; collapse hides again to the handle. Persist dock side + handle offset via `WindowDockPosition` / `IsWindowCollapsed` (and extend settings as needed). Optional always-on-top while expanded. |

Order: **E3 → E1 → E2** (dock is the defining companion UX; tray/hotkey support it).

## E3 behavior (required)

When docked/collapsed:

- App chrome is **hidden**.
- Only a **red half-circle** (or half-pill) peeks from the **left or right** screen edge.
- Handle is **relocatable**: user can drag it along the edge (persist Y offset) and/or move it to the other edge (persist side).
- Activating the handle (click) **animates the main window flying out** from that side (slide + fade is fine; keep it snappy).
- Collapsing (explicit control or edge re-dock) animates back and leaves only the handle.
- While expanded in dock mode, respect lock overlay + privacy mask; unlocking still required when locked.
- Do **not** require Google or any cloud identity.

Reuse/extend existing stubs: `AppSettings.WindowDockPosition`, `IsWindowCollapsed`, `SettingsService.SetWindowDockStateAsync`.

## Key files

- `Authenticator.Wpf/MainWindow.xaml(.cs)`, `App.xaml(.cs)`
- New dock handle window/control as needed (separate transparent tool window is OK)
- `Authenticator.Wpf/Data/Models/AppSettings.cs`, `SettingsService`
- `SettingsView` for dock / tray / hotkey toggles

## Exit criteria

- [x] Docked/collapsed state shows only the relocatable red half-circle handle
- [x] Handle position (edge + offset) persists across restarts
- [x] Expanding flies the window out from the docked side; collapsing returns to handle-only
- [x] App can also live in tray and restore cleanly
- [x] Hotkey does nothing useful while locked (or only opens unlock UI / expands to lock screen)
- [x] Privacy mask / lock still honored in dock + tray flows

## Phase F install / layout assumptions

- **Shutdown:** `ShutdownMode=OnExplicitShutdown`; real exit via tray **Exit**, Settings **Exit app**, or `App.RequestExit()`. Close (X) collapses to dock handle when docked, else hides to tray when Minimize to tray is on.
- **WinForms dual-stack:** `UseWindowsForms` enabled solely for `NotifyIcon`; implicit `System.Windows.Forms` / `System.Drawing` usings removed in the csproj to avoid WPF type clashes.
- **Tray icon:** interim generated red circle (or process-associated icon). Phase F should ship a real `.ico` via `ApplicationIcon` / MSIX assets.
- **Hotkey:** fixed **Ctrl+Shift+A** (show/expand only; no clipboard). Toggle in Settings → Desktop.
- **Prefs:** dock side / collapsed / handle Y offset in SQL `AppSettings` (`WindowDockHandleOffsetY` ensured at startup). Tray / hotkey / always-on-top in `%LocalAppData%\Authenticator\codes-ui-preferences.json`.
- **Handle window:** `DockHandleWindow` is a separate transparent tool window; packaging must not assume a single top-level window for lifetime.

## Starter prompt (paste into new chat)

```
Phase E only — Desktop convenience for D:\Source\Authenticator.

Read docs/phases/README.md and docs/phases/E.md. Implement E3 first (dock-to-desktop), then E1 tray, then E2 hotkey.

E3 required UX: when docked/collapsed the main window is hidden; a relocatable red half-circle sits on the left or right screen edge; activating it flies the window out from that side; collapse returns to handle-only. Persist edge + handle offset. Respect lock + privacy mask.

Product rule: local desktop 2FA utility; no Google account; no password storage.

When done: check off exit criteria in docs/phases/E.md, set Status to Done, list install/layout assumptions for Phase F.
```
