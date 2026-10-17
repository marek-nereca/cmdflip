# Spec: Work in admin windows without running as admin (`uiAccess`)

Status: proposed, not implemented. Blocked on code signing.

## Problem

Windows blocks a normal (medium integrity) process from sending input to a window that runs as
administrator. This is User Interface Privilege Isolation (UIPI). Cmdflip handles it by stepping
aside in admin windows (`ForegroundExcluded` in `src/KeyboardEngine.cs`), so shortcuts change
depending on which window has focus.

Today the only way around it is to run Cmdflip elevated:

- **Restart as administrator** asks for UAC approval every time.
- **Start at login as administrator** uses a scheduled task (`src/AdminTask.cs`). It is safe only
  for installs in Program Files, and it runs a keyboard hook with full admin rights, which is more
  privilege than the job needs.

## Goal

Cmdflip shortcuts work in admin windows while Cmdflip runs with the user's **normal, non-elevated**
token. No scheduled task, no UAC prompt at login.

## How `uiAccess` works

`uiAccess` is a manifest flag meant for accessibility and input tools (on-screen keyboard, screen
readers, remote input). A process with the flag is allowed past UIPI: it can send input to, and
receive input from, windows at higher integrity levels, without being elevated.

```xml
<requestedExecutionLevel level="asInvoker" uiAccess="true"/>
```

Windows only honours the flag when **all** of these hold:

1. The exe has an **Authenticode signature** that chains to a root certificate trusted by the
   machine. A self-signed certificate only works on machines where you installed its root, so it
   is fine for testing but not for a public release.
2. The exe is in a **secure location**: `%ProgramFiles%`, `%ProgramFiles(x86)%` or
   `%WINDIR%\System32`, including subfolders. This is the default policy "User Account Control:
   Only elevate UIAccess applications that are installed in secure locations".
3. The process is started through **ShellExecute** (the AppInfo service). `CreateProcess` on a
   `uiAccess` exe fails with `ERROR_ELEVATION_REQUIRED` (740).

If (1) or (2) fails, the exe **does not start at all**. Windows shows "A referral was returned from
the server" (`0x8007202B`). It does not fall back to running without the flag. This shapes the
build plan below.

Limits that stay the same: the secure desktop (UAC prompts, Ctrl+Alt+Del, the lock screen) is still
unreachable, as it is today.

## Prerequisite: code signing

This is the blocker. Options, all to be confirmed when the work starts (terms and prices change):

| Option | Notes |
|---|---|
| SignPath Foundation | Free for open-source projects. Signs in CI from a GitHub Actions build. Requires the project to be accepted. |
| Azure Artifact Signing (formerly Trusted Signing) | Low monthly fee. Requires identity validation; check whether individual developers in your country are eligible. |
| OV code-signing certificate | Yearly fee. Since 2023 the key must live on a hardware token or cloud HSM, which complicates CI signing. |

Signing also reduces SmartScreen warnings over time, which the README currently explains away.

## Build plan

Because an unsigned or misplaced `uiAccess` exe refuses to start, there must be **two variants**
of the exe:

| Variant | Manifest | Used for |
|---|---|---|
| `Cmdflip.exe` (standard) | `uiAccess="false"` (as today) | dev builds, portable zip, per-user installs |
| `Cmdflip.exe` (uiAccess) | `uiAccess="true"`, signed | all-users installs in Program Files |

### `build.ps1`

- Add a `-UiAccess` switch. When it is set, compile with a second manifest
  (`src/app.uiaccess.manifest`, identical except for `uiAccess="true"`) and output to
  `bin\uiaccess\Cmdflip.exe`.
- Add a signing step, or a hook for one, that signs both exes. Unsigned `uiAccess` builds are only
  useful for local testing with a self-signed root installed.

### Installer (`installer/Cmdflip.iss`)

Ship both exes in the setup and install the right one under the same name:

```ini
[Files]
Source: "..\bin\uiaccess\Cmdflip.exe"; DestDir: "{app}"; Flags: ignoreversion; Check: IsAdminInstallMode
Source: "..\bin\Cmdflip.exe";          DestDir: "{app}"; Flags: ignoreversion; Check: not IsAdminInstallMode
```

- Admin (all-users) installs go to `{autopf}` = Program Files, which meets the location rule.
- Add the `shellexec` flag to the `[Run]` entries, so the post-install start goes through
  ShellExecute (rule 3).
- Sign the installer itself too.

### CI (`.github/workflows/build.yml`)

- Build both variants, sign both exes and the installer, and put only the **standard** exe in the
  portable zip.
- Fail the release if the uiAccess exe is not signed: check that
  `Get-AuthenticodeSignature` returns `Valid`.

## App changes

### Detect the flag at runtime

Add `HasUiAccess`: `GetTokenInformation(token, TokenUIAccess /* 26 */, ...)` on the current
process token, next to `SelfElevated` in `KeyboardEngine`.

### Keyboard engine

`ForegroundExcluded` currently treats elevated foreground processes as excluded when Cmdflip is not
elevated. Change the condition to:

```csharp
if (!SelfElevated && !HasUiAccess && IsProcessElevated(process)) excluded = true;
```

Also handle the case where `OpenProcess` fails: today that sets `excluded = !SelfElevated`. With
uiAccess, input still reaches the window, so use `!SelfElevated && !HasUiAccess`.

### Tray menu (`src/Program.cs`)

- When `HasUiAccess` is true:
  - hide **Start at login as administrator** and **Restart as administrator**, because they are no
    longer needed.
  - if an admin task from an older version exists, show a one-time balloon offering to remove it
    (`AdminTask.Set(false)`), and keep the plain Run-key autostart instead.
- When `HasUiAccess` is false (per-user or portable), keep today's behaviour, including the Program
  Files restriction on the admin task.
- Show the mode in the tray tooltip, e.g. `Cmdflip - works in admin windows`, so users can tell which
  variant they run.

### Restart paths

`RestartElevated` and `--restart` already use `UseShellExecute = true`, so they meet rule 3. The
Run-key autostart is started by Explorer through ShellExecute, which is fine too. Audit any new
`Process.Start` of Cmdflip.exe for `UseShellExecute = true`.

## Docs

- README "Apps running as administrator": explain that the all-users install works in admin
  windows out of the box, and that per-user or portable installs need the admin options.
- README warnings section: update once releases are signed.
- CHANGELOG entry.

## Tests (manual, on a clean VM)

1. All-users install: Cmdflip starts, the tooltip shows the uiAccess mode, and Task Manager shows
   it as **not** elevated.
2. Open an admin PowerShell or Registry Editor. Cmd+C/V, Cmd+arrows, Cmd+Tab and Opt+arrows work.
3. Switch between admin and normal windows while holding Cmd. Keys don't get stuck.
4. Per-user install: the standard exe is installed, admin windows are skipped as today, and the
   admin task is greyed out.
5. Portable zip: the standard exe starts from Downloads.
6. Copy the uiAccess exe to the desktop and run it. It fails to start, which is expected and
   confirms rule 2. Make sure no shipped path can end up like this.
7. Upgrade from a version with an admin task: the balloon offers removal, and after removal
   Cmdflip still autostarts via the Run key.
8. Lock and unlock, and sleep and resume, while an admin window has focus. Shortcuts still work
   afterwards.
9. Uninstall removes both the Run-key value and any leftover task.

## Open questions

- Should the installer default to all-users once uiAccess ships? That costs a UAC prompt at
  install time, in exchange for a better default experience.
- Can the admin task be dropped completely after a few releases, or should per-user installs keep
  the "Restart as administrator" option forever?
