# Changelog

All notable changes to Cmdflip are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.1.1] - 2026-10-17

### Security
- "Start at login as administrator" is now only available when Cmdflip is installed for all users
  (in Program Files). In a per-user install another program could replace Cmdflip.exe and get
  administrator rights at the next login. If such a task already exists, Cmdflip shows a warning
  at startup, and the task can still be turned off from the tray menu.
- The scheduled task is now created by an elevated Cmdflip through the Task Scheduler API instead
  of from a temporary XML file another program could change before it was read.

## [0.1.0] - 2026-10-17

### Added
- Left Alt works as Cmd, the Windows key as Opt, Right Alt is unchanged.
- Cmd + key sends Ctrl + key, Cmd + click sends Ctrl + click.
- macOS shortcuts: Cmd+Tab, Cmd+Q, Cmd+M, Cmd+Space, Cmd+arrows, Cmd+Backspace, Cmd+Shift+[ / ],
  Cmd+Shift+3/4/5 screenshots, Cmd+Opt+Esc, Cmd+Ctrl+Q.
- Opt shortcuts: word jumps, delete word; other Win shortcuts keep working, Opt alone opens Start.
- Optional Cmd+H/J/K/L arrows and plain Cmd+4 screenshot.
- Optional Caps Lock as Esc (off by default); Shift+Caps Lock still toggles Caps Lock.
- Tray menu with pause, screenshot app choice, start at login (normal or as administrator).
- Settings file with live reload and per-app exclusions.
- Version shown in the tray menu and a daily check for new releases on GitHub.
- Installer (per-user, no admin rights needed) and portable zip.

[Unreleased]: https://github.com/marek-nereca/cmdflip/compare/v0.1.1...HEAD
[0.1.1]: https://github.com/marek-nereca/cmdflip/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/marek-nereca/cmdflip/releases/tag/v0.1.0
