# Spec: Terminal-aware Cmd shortcuts

Status: proposed, not implemented.

## Problem

Cmdflip turns Cmd into Ctrl everywhere (`OnCmdKey`, the "Everything else: Cmd behaves as Ctrl"
fallback in `src/KeyboardEngine.cs`). In terminals, Ctrl+C and Ctrl+V are not copy and paste:

| Terminal | Ctrl+C | Ctrl+V |
|---|---|---|
| Windows Terminal | copy if text is selected, otherwise SIGINT | paste |
| conhost (classic console window) | copy if text is selected, otherwise SIGINT | paste |
| mintty (Git Bash, MSYS2, Cygwin) | always SIGINT | sends a raw `^V` |
| PuTTY / KiTTY | always sends `^C` to the remote | sends a raw `^V` |

A Mac user presses Cmd+C to copy and expects a running process to keep running. Today the
process gets interrupted when nothing is selected, and in mintty and PuTTY it always does. Cmd+V
doesn't paste in mintty or PuTTY.

On a Mac, the physical Ctrl key sends the interrupt. Cmdflip already passes physical Ctrl through
unchanged, so only the Cmd side needs to change.

## Goal

When a terminal window is in the foreground, Cmd+C copies and Cmd+V pastes. They never send a
control character to the shell. Physical Ctrl+C still sends SIGINT. Outside terminals nothing
changes.

## Behaviour

When the foreground window is a terminal and Cmd is held:

| Keys | Sent to the terminal | Why |
|---|---|---|
| Cmd+C | Ctrl+Insert | Copy. Bound by default in Windows Terminal, conhost and mintty; never sends SIGINT |
| Cmd+V | Shift+Insert | Paste. Bound by default in all four terminals above |
| Cmd+T | Ctrl+Shift+T | New tab (Windows Terminal only, see below) |
| Cmd+W | Ctrl+Shift+W | Close tab/pane (Windows Terminal only) |
| Cmd+F | Ctrl+Shift+F | Find (Windows Terminal only) |
| Cmd+Shift+C / Cmd+Shift+V | same as Cmd+C / Cmd+V | Users who know the Linux shortcut get the same result |
| Every other Cmd+key | unchanged (Ctrl+key) | Cmd+A, Cmd+R, Cmd+L and so on keep working as Ctrl for shells and TUIs |
| Physical Ctrl+anything | unchanged | Ctrl+C = SIGINT, Ctrl+D = EOF, Ctrl+R = history search |

Cmd+T/W/F are remapped only for Windows Terminal. mintty and conhost have no tabs, and in
PuTTY Ctrl+Shift+T/W/F would reach the remote program, so plain Ctrl is no worse there.

The Ctrl+Insert/Shift+Insert pair is preferred over Ctrl+Shift+C/V because it works in conhost
with its default settings and in PuTTY. Ctrl+Shift+C/V needs an option turned on in conhost, and
in PuTTY it is off by default.

Every Cmd shortcut that isn't a Ctrl passthrough keeps working in terminals as it does elsewhere:
Cmd+Tab, Cmd+Q, Cmd+M, Cmd+Space, screenshots, and Cmd+arrows/Backspace.

## Detecting a terminal

Classify the foreground window as one of `None`, `WindowsTerminal`, or `Generic`.

1. **Window class first.** `GetWindowThreadProcessId` on a classic console window returns the
   attached client (e.g. `cmd.exe`, `powershell.exe`), not `conhost.exe`. The exe name is not
   reliable there, so check the window class with `GetClassName`:

   | Class name | Kind |
   |---|---|
   | `CASCADIA_HOSTING_WINDOW_CLASS` | WindowsTerminal |
   | `ConsoleWindowClass` | Generic (conhost) |
   | `mintty` | Generic |
   | `PuTTY`, `KiTTY` | Generic |

2. **Then the exe name**, matched against `TerminalApps` from the config (case-insensitive).
   Entries default to `Generic`. `WindowsTerminal.exe` is in the default list too, as a fallback.

`GetClassName` must be added to `src/Native.cs`.

### Caching

Detection runs in the hook callback, so it must be cheap. Cache it alongside the existing
exclusion check in `ForegroundExcluded`, which already caches per `(pid, config)`. Cache the
terminal kind per **foreground HWND** plus `config`, not per pid, because one Windows Terminal
process hosts many windows and conhost windows report their client pid.

Do the check once per Cmd press (where `ShouldRemap()` is called on Cmd down) and keep the result
in a field like `cmdTerminal` until Cmd is released. That way a focus change in the middle of a
shortcut can't switch mappings halfway.

### Elevated terminals

An elevated terminal (for example "Run as administrator" PowerShell) is already excluded when
Cmdflip itself is not elevated, and gets stock Windows keys. No change is needed; the README
section on admin apps covers it.

## Configuration

New keys in `config.ini` (`src/Config.cs`: field, `Load` case, `KnownKeys` entry, `Save` line
with a comment, and `Clone` must deep-copy the set):

```ini
# Terminal-aware copy/paste: in terminals Cmd+C/V copy and paste (Ctrl+Insert / Shift+Insert)
# and never send Ctrl+C to the shell. Physical Ctrl+C still interrupts.
TerminalMode = true
# Extra terminal process names, comma separated (built-in window-class detection covers
# Windows Terminal, the classic console, mintty and PuTTY)
TerminalApps = WindowsTerminal.exe, alacritty.exe, wezterm-gui.exe, Hyper.exe, Tabby.exe
```

- `TerminalMode` defaults to `true`. Existing configs pick up the new keys through the
  "rewrite when keys are missing" path in `Config.Load`.
- When `TerminalMode = false`, detection is skipped entirely.

Before shipping the default `TerminalApps` list, check that each listed app binds
Ctrl+Insert / Shift+Insert. If one doesn't, drop it from the defaults or give it a different
mapping.

## Implementation notes

In `OnCmdKey`, add a terminal branch **before** the `switch (vk)` fallthrough to "Cmd behaves as
Ctrl". The branch can't go first in the method, because the Cmd+Tab and Cmd+Opt+Esc paths must
keep priority. A sketch:

```csharp
if (cmdTerminal != TerminalKind.None)
{
    switch (vk)
    {
        case 'C': return Tap(VK_INSERT, CTRL);
        case 'V': return Tap(VK_INSERT, SHIFT);
        case 'T': case 'W': case 'F':
            if (cmdTerminal == TerminalKind.WindowsTerminal) return Tap(vk, CTRL | SHIFT);
            break;
    }
}
```

- `Tap` already adjusts the modifiers to exactly what's requested, then restores them in the same
  batch. That covers both cases: Ctrl already injected by an earlier Cmd+key in the same hold
  (`injCtrl`), and a physically held Shift (Cmd+Shift+C). Because `Tap` passes `want` exactly, a
  held Shift is released for Ctrl+Insert, which is correct.
- `VK_INSERT` is already in `IsExtended`, so the injected Insert is not read as numpad 0 when
  NumLock is on.
- Auto-repeat: holding Cmd+V repeats `Tap`, which pastes again on each repeat. That matches a
  held Cmd+V in macOS Terminal and is acceptable.
- Cmd+click in terminals stays Ctrl+click, which opens links in Windows Terminal.

## Tests (manual)

Run each in Windows Terminal, a classic console (`conhost.exe cmd`), Git Bash (mintty) and PuTTY:

1. Run `ping -t localhost` (or `sleep 100`), press Cmd+C with no selection. The process **keeps
   running**.
2. Select text, press Cmd+C, paste into Notepad. The text was copied.
3. Copy text in Notepad, press Cmd+V in the terminal. It pastes, with no `^V` in the output.
4. Press physical Ctrl+C while the process runs. It is interrupted.
5. Press Cmd+A, Cmd+R and Cmd+L in bash. They act as Ctrl+A/R/L (line start, history search,
   clear).
6. Windows Terminal: Cmd+T opens a tab, Cmd+W closes it, Cmd+F opens find.
7. Cmd+Shift+V pastes once (not twice, and not as Ctrl+Shift+Insert).
8. Turn NumLock on, then repeat steps 2 and 3. No `0` is typed.
9. Set `TerminalMode = false` and save. Cmd+C in a terminal behaves like Ctrl+C again, with no
   restart.
10. Outside terminals (Notepad, browser), Cmd+C/V/T/W/F are unchanged.
11. Alt-tab from a terminal to a browser and back while holding Cmd. Keys don't get stuck, and
    each app gets its own mapping on the next Cmd press.

## Out of scope

- **VS Code integrated terminal.** It lives in the editor window and can't be told apart without
  UI Automation. VS Code already copies on Ctrl+C when text is selected, so it is acceptable as is.
- Per-app custom key maps in general (a Kinto-style `[App]` section). This spec only covers
  terminals.
- Remote sessions (`mstsc.exe`, `vmconnect.exe`). They should be excluded via `ExcludedApps`.

## Docs to update when implemented

- `README.md`: Shortcuts table (Cmd+C/V in terminals) and Settings (`TerminalMode`,
  `TerminalApps`).
- `CHANGELOG.md`: entry under `## [Unreleased]`.
