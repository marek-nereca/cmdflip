# Cmdflip

A small Windows tray app that brings macOS shortcuts to Windows.

- **Left Alt = Cmd**
- **Windows key = Opt**
- **Right Alt** is unchanged (AltGr keeps working)

Requires Windows 10 (1809 or later) or Windows 11.

## Shortcuts

"You press" is what you press on a Windows keyboard. "Windows gets" is what Cmdflip sends in its
place.

| You press | Mac shortcut | Windows gets | Result |
|---|---|---|---|
| Left Alt + any key / click | Cmd + key / click | Ctrl + key / click | copy, paste, undo, save, find, new tab, ... (C, V, Z, S, F, T, W, R, N, ...) |
| Left Alt + Tab | Cmd + Tab | Alt + Tab | app switcher |
| Left Alt + Q | Cmd + Q | Alt + F4 | close app |
| Left Alt + M | Cmd + M | minimize | minimize window |
| Left Alt + Space | Cmd + Space | Win + S | search (configurable) |
| Left Alt + ← / → | Cmd + ← / → | Home / End | line start / end (add Shift to select) |
| Left Alt + ↑ / ↓ | Cmd + ↑ / ↓ | Ctrl + Home / End | document start / end (add Shift to select) |
| Left Alt + Backspace | Cmd + Backspace | Shift + Home, Backspace | delete to line start |
| Left Alt + Shift + [ / ] | Cmd + Shift + [ / ] | Ctrl + Shift + Tab / Ctrl + Tab | previous / next tab |
| Left Alt + Shift + 3 | Cmd + Shift + 3 | Win + PrintScreen | screenshot of the whole screen |
| Left Alt + Shift + 4 | Cmd + Shift + 4 | region snip | screenshot of a region |
| Left Alt + 4 | Cmd + 4 | region snip | same as above (can be turned off) |
| Left Alt + Shift + 5 | Cmd + Shift + 5 | Snipping Tool | screenshot app |
| Left Alt + H / J / K / L | Cmd + H / J / K / L | ← / ↓ / ↑ / → | vim arrows (can be turned off) |
| Left Alt + Win + Esc | Cmd + Opt + Esc | Ctrl + Shift + Esc | Task Manager |
| Left Alt + Ctrl + Q | Cmd + Ctrl + Q | (locks directly) | lock screen |
| Win + ← / → | Opt + ← / → | Ctrl + ← / → | word left / right (add Shift to select) |
| Win + ↑ / ↓ | Opt + ↑ / ↓ | Ctrl + ↑ / ↓ | paragraph up / down |
| Win + Backspace / Delete | Opt + Backspace / Delete | Ctrl + Backspace / Delete | delete word |
| Win + other keys | — | Win + key | normal Windows shortcuts (Win+E, Win+R, Win+L, ...) |
| Win alone | — | Win | Start menu |
| Caps Lock | — | Esc | optional, off by default; Shift + Caps Lock still toggles Caps Lock |

Shortcuts don't work in apps running as administrator unless Cmdflip runs as administrator too.
See [Apps running as administrator](#apps-running-as-administrator).

## Install

Download `Cmdflip-<version>-setup.exe` from [Releases](https://github.com/marek-nereca/cmdflip/releases)
and run it. The installer asks how to install:

- **Only for me** (default): no administrator rights needed.
- **For all users**: installs to Program Files and needs administrator rights once. Choose this
  if you want shortcuts to work in admin apps from login (see
  [Apps running as administrator](#apps-running-as-administrator)).

A portable zip is available too.

Right-click the tray icon (⌘) for settings. Double-click it to pause or resume.
Turn off any other key remapping tools while you use this app.

> [!NOTE]
> Windows doesn't let normal apps send keys to apps running as administrator (an admin
> terminal, Registry Editor, some installers, and sometimes Task Manager). In those windows the keys
> behave as on stock Windows. See [Apps running as administrator](#apps-running-as-administrator).

### Windows or antivirus warnings

Cmdflip is not code-signed yet, so Windows SmartScreen may say it "protected your PC". Click
**More info**, then **Run anyway**. Some antivirus tools also flag apps that use a system-wide
keyboard hook, because keyloggers use the same Windows API.

What Cmdflip does with your keys:

- Keystrokes are handled in memory as they happen. They are never stored, logged or sent anywhere.
- The only network request is a daily check of this repository's latest release on GitHub. You
  can turn it off in the tray menu (**Updates → Check automatically**) or with
  `CheckForUpdates = false`.
- Every release lists SHA-256 checksums in `SHA256SUMS.txt`. Check a download with
  `Get-FileHash .\Cmdflip-<version>-setup.exe`.
- The source is all here and builds with the compiler that ships with Windows (see [Build](#build)).

## Settings

Settings are in `%APPDATA%\Cmdflip\config.ini`. **Edit settings file...** in the tray menu opens it,
and changes apply as soon as you save. You can set the screenshot tools and the Cmd+Space action
(a program, a URI, or `keys:Win+Shift+S`), turn the vim arrows and Caps Lock as Esc on or off, and
list apps to exclude (`ExcludedApps = mstsc.exe, game.exe`).

Settings are always per user, including in an all-users install. Each Windows user gets their own
`config.ini` the first time Cmdflip starts for them, and turns on **Start at login** for themselves.
The installer's "Start Cmdflip when I sign in" option only applies to the user who ran it.

## Apps running as administrator

Windows doesn't let a normal app send keys to apps running as administrator. In those windows
Cmdflip leaves Alt and Win alone, so you get stock Windows behaviour. To have shortcuts there too,
Cmdflip has to run as administrator. The tray menu has two ways:

- **Restart as administrator**: until you sign out. Windows asks for confirmation (UAC) each time.
- **Start at login as administrator**: every time you sign in, with no confirmation. Only available
  when Cmdflip is installed for all users.

Why only for all-users installs: this option makes Windows start `Cmdflip.exe` with administrator
rights without asking you. That is only safe if no other program can replace `Cmdflip.exe`. In
Program Files only administrators can change files. A per-user install is in your own app data
folder, where any program you run could swap the exe and get administrator rights silently. So
Cmdflip doesn't offer the option there, and warns you if an older version already turned it on.

## How it stays reliable

- The physical Alt and Win keys never reach Windows, so menus and Start don't open by accident.
- Ctrl is held for the whole time Cmd is held, so auto-repeat, Cmd+Shift+Z and Cmd+click behave correctly.
- Every injected combination restores the modifier state in the same batch, so modifiers can't get stuck.
- Arrow, Home and End keys are injected as extended keys, so NumLock never turns them into digits.
- The hook runs on its own high-priority thread and launches apps in the background. Windows silently removes a hook that responds too slowly, and this keeps that from happening.
- State is reset and the hooks are reinstalled after lock/unlock and sleep.

## Build

```powershell
.\build.ps1                               # dev build, uses the C# compiler that ships with Windows
.\build.ps1 -Version 1.2.0 -Installer     # release build + installer (needs Inno Setup 6)
.\bin\Cmdflip.exe
```

Releases are built and published by GitHub Actions; see [docs/releasing.md](docs/releasing.md).

## License

[MIT](LICENSE) © 2026 Marek Nereca
