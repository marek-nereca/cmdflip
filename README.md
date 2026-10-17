# Cmdflip

A small Windows tray app that brings macOS shortcuts to Windows.

- **Left Alt = Cmd**
- **Windows key = Opt**
- **Right Alt** is unchanged (AltGr keeps working)

Requires Windows 10 (1809 or later) or Windows 11.

## Install

Download `Cmdflip-<version>-setup.exe` from [Releases](https://github.com/marek-nereca/cmdflip/releases)
and run it. It installs for your user only and needs no administrator rights. A portable zip
is available too.

Right-click the tray icon (⌘) for settings. Double-click it to pause or resume.
Turn off any other key remapping tools while you use this app.

## Build

```powershell
.\build.ps1                               # dev build, uses the C# compiler that ships with Windows
.\build.ps1 -Version 1.2.0 -Installer     # release build + installer (needs Inno Setup 6)
.\bin\Cmdflip.exe
```

## Releasing

Versions follow [Semantic Versioning](https://semver.org). To publish a release:

1. Move the notes under `## [Unreleased]` in [CHANGELOG.md](CHANGELOG.md) into a new
   `## [x.y.z] - YYYY-MM-DD` section and commit.
2. Tag and push: `git tag vx.y.z && git push origin vx.y.z`

GitHub Actions builds the exe and installer, stamps the version from the tag, and publishes a
release with the changelog notes and SHA-256 checksums. Tags with a suffix (`v1.0.0-beta.1`) are
published as pre-releases.

## Shortcuts

| Keys | Result |
|---|---|
| Cmd + any key / click | Ctrl + key / click (C, V, X, Z, A, S, F, T, W, R, N, ...) |
| Cmd + Tab | app switcher (Alt+Tab) |
| Cmd + Q | close app (Alt+F4) |
| Cmd + M | minimize window |
| Cmd + Space | search (configurable) |
| Cmd + ←/→ | line start / end (Shift selects) |
| Cmd + ↑/↓ | document start / end |
| Cmd + Backspace | delete to line start |
| Cmd + Shift + [ / ] | previous / next tab |
| Cmd + Shift + 3 / 4 / 5 | full screenshot / region snip / screenshot app |
| Cmd + 4 | region snip (can be turned off) |
| Cmd + H/J/K/L | arrows (can be turned off) |
| Cmd + Opt + Esc | Task Manager |
| Cmd + Ctrl + Q | lock screen |
| Opt + ←/→/↑/↓ | word / paragraph jumps |
| Opt + Backspace / Delete | delete word |
| Opt + other keys | normal Win shortcuts (Win+E, Win+R, Win+L, ...) |
| Opt alone | Start menu |
| Caps Lock | Esc (optional, off by default; Shift + Caps Lock still toggles Caps Lock) |

## Settings

`%APPDATA%\Cmdflip\config.ini` changes apply as soon as you save the file. You can set
the screenshot tools and the Cmd+Space action (a program, a URI, or `keys:Win+Shift+S`),
turn the vim arrows and Caps Lock as Esc on or off, and list apps to exclude (`ExcludedApps = mstsc.exe, game.exe`).

## Apps running as administrator

Windows does not let a normal app send keys to windows running as administrator. In those
windows Cmdflip leaves Alt and Win alone, so you get stock Windows behaviour. To have
shortcuts there too, use **Start at login as administrator** in the tray menu. It creates a
scheduled task with no time limit, normal priority and battery-safe settings.

## How it stays reliable

- The physical Alt and Win keys never reach Windows, so menus and Start don't open by accident.
- Ctrl is held for the whole time Cmd is held, so auto-repeat, Cmd+Shift+Z and Cmd+click behave correctly.
- Every injected combination restores the modifier state in the same batch, so modifiers can't get stuck.
- Arrow, Home and End keys are injected as extended keys, so NumLock never turns them into digits.
- The hook runs on its own high-priority thread and launches apps in the background. Windows silently removes a hook that responds too slowly, and this keeps that from happening.
- State is reset and the hooks are reinstalled after lock/unlock and sleep.

## License

[MIT](LICENSE) © 2026 Marek Nereca
