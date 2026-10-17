# How Cmdflip works

Cmdflip installs Windows low-level keyboard and mouse hooks (`WH_KEYBOARD_LL`, `WH_MOUSE_LL`).
Every physical key press and mouse click passes through `KeyboardEngine` (`src/KeyboardEngine.cs`)
before any app sees it. For each event the engine either lets it through, blocks it, or blocks it
and sends different keys with `SendInput`. Keys Cmdflip sends itself are tagged (`dwExtraInfo` =
`"CMDF"`), and the hook ignores injected input, so they never pass through the remapping a second
time.

Key remappers tend to fail in a handful of well-known ways. The sections below describe each
failure and how Cmdflip avoids it.

## The physical Alt and Win keys never reach Windows

**The failure.** Windows reacts to a lone modifier press. Releasing Alt highlights the app's menu
bar, and releasing Win opens Start. Many remappers let the real key through and try to cancel it
afterwards, for example by sending a dummy key. That often fails, so Start flashes open after a
Win+arrow shortcut, or Alt puts the app into menu mode.

**What Cmdflip does.** It blocks every physical Left Alt (Cmd) and Win (Opt) press (`OnCmd`,
`OnOpt`) and sends only the keys each shortcut needs. Tapping Win on its own still opens Start,
because `OnOpt` then sends a Win press and release deliberately (`optUsed` tracks whether another
key or a click was pressed in between). Right Alt is never touched, so AltGr keeps working on
international layouts.

## Ctrl stays held while Cmd is held

**The failure.** The simple approach turns each Cmd+key into a quick Ctrl+key tap. That breaks:

- **Auto-repeat:** holding Cmd+Z should undo repeatedly, which needs Ctrl held throughout.
- **Adding modifiers mid-shortcut:** pressing Cmd, then Shift, then Z must arrive as Ctrl+Shift+Z.
- **Cmd+click:** opening a link in a new tab or multi-selecting files needs Ctrl down during the
  click.

**What Cmdflip does.** On the first key pressed with Cmd, it presses Ctrl and keeps it down
(`injCtrl`) until Cmd is released. Further keys pass through untouched with Ctrl already down, so
auto-repeat and extra modifiers behave as with a real Ctrl key. The mouse hook does the same for
clicks (`MouseProc`).

## Modifiers can't get stuck

**The failure.** A tool sends "Ctrl down", something interrupts it, and the matching "Ctrl up"
never arrives. Ctrl then stays logically pressed after you let go, and every later key does
something unexpected until you press and release Ctrl again.

**What Cmdflip does.** Shortcuts that need a different modifier state, such as Cmd+← becoming Home
(Ctrl must be released briefly), go through `Tap()`. It builds a single `SendInput` call holding the
modifier changes, the key, and the restoring changes. Windows processes one `SendInput` call
without anything in between, so the sequence can't be interrupted halfway. When Cmd and Opt are
released, everything Cmdflip pressed is released too.

## Arrows and Home/End are sent as extended keys

**The failure.** On a PC keyboard, the arrow, Home, End, Insert and Delete keys share key codes
with the numeric keypad. The dedicated keys carry an "extended" flag. A tool that sends Home
without that flag gets keypad 7 when NumLock is on, and types "7" where you wanted line start.

**What Cmdflip does.** `Key()` sets `KEYEVENTF_EXTENDEDKEY` for every key in `IsExtended()`: the
arrows, Home, End, Page Up/Down, Insert, Delete, Print Screen, Win and Menu.

## The hook always responds quickly

**The failure.** Windows waits for a low-level hook to answer before delivering each key. If the
hook doesn't answer within `LowLevelHooksTimeout` (about 1 second on Windows 10 and 11), Windows
skips it. After repeated timeouts it **silently removes the hook**, and the remapping stops with no
error. This typically happens when the hook runs on the UI thread and that thread is busy, for
example showing a menu or starting a slow app.

**What Cmdflip does.**

- The hooks run on a dedicated thread (`Cmdflip hook`, `ThreadPriority.Highest`) with its own
  message loop that does nothing else.
- Slow work, such as starting the screenshot app or another configured program, goes to the thread
  pool (`RunAction` → `Launch`).
- The check for whether the foreground app is excluded or elevated is cached per process
  (`ForegroundExcluded`), so the hook doesn't query the process on every key press.
- Settings are an immutable `Config` snapshot. The tray swaps in a new one, and the hook thread reads
  it without locks.

## Recovery after lock, unlock and sleep

**The failure.** When the PC locks, the lock screen takes over the keyboard, and the hook misses key
releases. Lock with Cmd+Ctrl+Q and Cmdflip would still think Cmd and Ctrl are held after unlock.
Hooks can also stop receiving events after sleep.

**What Cmdflip does.** The tray app listens for `SessionSwitch` and `PowerModeChanged`
(`OnSessionSwitch`, `OnPowerModeChanged` in `src/Program.cs`). It posts a reset to the hook thread
(`engine.Reset`). The reset releases anything Cmdflip had pressed, forgets the tracked key state and
clears the cache. On unlock, logon, reconnect and resume it also uninstalls and reinstalls the hooks.

## Apps running as administrator

Windows does not let a normal process send input to a window that runs as administrator (User
Interface Privilege Isolation). If Cmdflip blocked Alt there, the replacement keys would be dropped
and shortcuts would do nothing. So when the foreground process is elevated and Cmdflip is not,
`ForegroundExcluded` returns true and Alt and Win pass through unchanged. See the README for running
Cmdflip as administrator, and [uiaccess-spec.md](uiaccess-spec.md) for the planned way to avoid it.
