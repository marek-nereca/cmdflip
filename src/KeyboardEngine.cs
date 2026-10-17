using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Cmdflip
{
    // Low-level keyboard/mouse hook that turns Left Alt into Cmd and the Windows key into Opt.
    //
    // Design notes (the things that make generic remappers flaky):
    //  * The physical Left Alt / Win key is never forwarded. Windows therefore never sees a
    //    bare Alt or Win press, so menus don't activate and Start doesn't pop up by accident.
    //  * Ctrl is injected lazily, the first time a key is pressed with Cmd held, and stays
    //    down until Cmd is released. Auto-repeat, Cmd+Shift+X and Cmd+click work naturally.
    //  * Taps (Cmd+Left -> Home, ...) set the exact modifier state they need and restore it
    //    afterwards in the same SendInput batch, so modifiers can't get stuck.
    //  * Hooks run on their own high-priority thread, and app launches go to the thread pool,
    //    so the callback always returns fast and Windows never silently drops the hook.
    internal sealed class KeyboardEngine
    {
        const int CTRL = 1, SHIFT = 2, ALT = 4, WIN = 8;

        const int PASS = 0;      // let the event through untouched
        const int SWALLOW = 1;   // key down handled; also eat the matching key up
        const int REPLACED = 2;  // key down re-injected with modifiers; key up passes through

        const int VK_BACK = 0x08, VK_TAB = 0x09, VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;
        const int VK_CAPITAL = 0x14, VK_ESCAPE = 0x1B, VK_SPACE = 0x20;
        const int VK_PRIOR = 0x21, VK_NEXT = 0x22, VK_END = 0x23, VK_HOME = 0x24;
        const int VK_LEFT = 0x25, VK_UP = 0x26, VK_RIGHT = 0x27, VK_DOWN = 0x28;
        const int VK_SNAPSHOT = 0x2C, VK_INSERT = 0x2D, VK_DELETE = 0x2E;
        const int VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_APPS = 0x5D, VK_DIVIDE = 0x6F, VK_F4 = 0x73;
        const int VK_NUMLOCK = 0x90, VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1;
        const int VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3, VK_LMENU = 0xA4, VK_RMENU = 0xA5;
        const int VK_OEM_4 = 0xDB, VK_OEM_6 = 0xDD;
        const int WM_ENGINE_RESET = Native.WM_APP + 1;

        static readonly UIntPtr Marker = new UIntPtr(0x434D4446); // "CMDF"
        static readonly int InputSize = Marshal.SizeOf(typeof(Native.INPUT));
        static readonly bool SelfElevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

        volatile Config config;
        Thread thread;
        uint threadId;
        IntPtr kbHook, msHook;
        readonly Native.HookProc kbProc, msProc; // fields keep the delegates alive for native code

        // Hook-thread state.
        bool cmdDown, cmdPass;
        bool optDown, optPass, optUsed;
        bool injCtrl, injAlt, injWin;
        readonly HashSet<int> swallowUp = new HashSet<int>();
        readonly HashSet<int> heldMods = new HashSet<int>();
        uint cachedPid;
        Config cachedConfig;
        bool cachedExcluded;

        public event Action<string> Error;

        public KeyboardEngine(Config config)
        {
            this.config = config;
            kbProc = KeyboardProc;
            msProc = MouseProc;
        }

        public Config Config
        {
            get { return config; }
            set { config = value; }
        }

        public static bool IsElevated
        {
            get { return SelfElevated; }
        }

        public void Start()
        {
            var ready = new ManualResetEvent(false);
            thread = new Thread(() => Run(ready));
            thread.IsBackground = true;
            thread.Priority = ThreadPriority.Highest;
            thread.Name = "Cmdflip hook";
            thread.Start();
            ready.WaitOne();
        }

        public void Stop()
        {
            Native.PostThreadMessage(threadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            thread.Join(2000);
        }

        // Forget pressed keys and release injected modifiers; optionally re-register the hooks.
        // Used after lock/unlock and sleep, where key-up events can be lost.
        public void Reset(bool reinstallHooks)
        {
            Native.PostThreadMessage(threadId, WM_ENGINE_RESET, new IntPtr(reinstallHooks ? 1 : 0), IntPtr.Zero);
        }

        void Run(ManualResetEvent ready)
        {
            threadId = Native.GetCurrentThreadId();
            InstallHooks();
            ready.Set();

            Native.MSG msg;
            while (Native.GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.hwnd == IntPtr.Zero && msg.message == WM_ENGINE_RESET)
                {
                    ResetState();
                    if (msg.wParam != IntPtr.Zero)
                    {
                        UninstallHooks();
                        InstallHooks();
                    }
                }
            }
            ResetState();
            UninstallHooks();
        }

        void InstallHooks()
        {
            IntPtr module = Native.GetModuleHandle(null);
            kbHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, kbProc, module, 0);
            msHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, msProc, module, 0);
            if (kbHook == IntPtr.Zero || msHook == IntPtr.Zero)
                RaiseError("Could not install keyboard hook (error " + Marshal.GetLastWin32Error() + ").");
        }

        void UninstallHooks()
        {
            if (kbHook != IntPtr.Zero) Native.UnhookWindowsHookEx(kbHook);
            if (msHook != IntPtr.Zero) Native.UnhookWindowsHookEx(msHook);
            kbHook = msHook = IntPtr.Zero;
        }

        void ResetState()
        {
            var inputs = new List<Native.INPUT>();
            if (injCtrl) inputs.Add(Key(VK_LCONTROL, true));
            if (injAlt) inputs.Add(Key(VK_LMENU, true));
            if (injWin) inputs.Add(Key(VK_LWIN, true));
            Send(inputs);
            cmdDown = cmdPass = optDown = optPass = optUsed = false;
            injCtrl = injAlt = injWin = false;
            swallowUp.Clear();
            heldMods.Clear();
            cachedConfig = null;
        }

        IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                var k = (Native.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.KBDLLHOOKSTRUCT));
                if ((k.flags & Native.LLKHF_INJECTED) == 0)
                {
                    int msg = wParam.ToInt32();
                    bool down = msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN;
                    bool suppress = false;
                    try
                    {
                        suppress = OnKey((int)k.vkCode, (ushort)k.scanCode, (k.flags & Native.LLKHF_EXTENDED) != 0, down);
                    }
                    catch (Exception ex)
                    {
                        RaiseError(ex.Message);
                    }
                    if (suppress) return new IntPtr(1);
                }
            }
            return Native.CallNextHookEx(kbHook, nCode, wParam, lParam);
        }

        IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && (cmdDown || optDown))
            {
                var m = (Native.MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.MSLLHOOKSTRUCT));
                if ((m.flags & Native.LLMHF_INJECTED) == 0)
                {
                    uint button = 0;
                    switch (wParam.ToInt32())
                    {
                        case Native.WM_LBUTTONDOWN: button = Native.MOUSEEVENTF_LEFTDOWN; break;
                        case Native.WM_RBUTTONDOWN: button = Native.MOUSEEVENTF_RIGHTDOWN; break;
                        case Native.WM_MBUTTONDOWN: button = Native.MOUSEEVENTF_MIDDLEDOWN; break;
                    }
                    if (button != 0)
                    {
                        if (optDown && !optPass) optUsed = true;
                        if (cmdDown && !cmdPass && !injCtrl && !injAlt)
                        {
                            // Cmd+click -> Ctrl+click (open link in new tab, multi-select, ...)
                            var click = new Native.INPUT { type = Native.INPUT_MOUSE };
                            click.U.mi.dwFlags = button;
                            click.U.mi.dwExtraInfo = Marker;
                            Send(new List<Native.INPUT> { Key(VK_LCONTROL, false), click });
                            injCtrl = true;
                            return new IntPtr(1);
                        }
                    }
                }
            }
            return Native.CallNextHookEx(msHook, nCode, wParam, lParam);
        }

        bool OnKey(int vk, ushort scan, bool ext, bool down)
        {
            switch (vk)
            {
                case VK_LSHIFT: case VK_RSHIFT: case VK_LCONTROL: case VK_RCONTROL:
                    if (down) heldMods.Add(vk); else heldMods.Remove(vk);
                    return false;
                case VK_SHIFT: case VK_CONTROL: case VK_MENU: case VK_RMENU: case VK_CAPITAL:
                    return false;
                case VK_LMENU:
                    return OnCmd(down);
                case VK_LWIN: case VK_RWIN:
                    return OnOpt(down);
            }

            if (!down) return swallowUp.Remove(vk);

            bool cmd = cmdDown && !cmdPass;
            bool opt = optDown && !optPass;
            if (opt) optUsed = true;

            int result = PASS;
            if (cmd) result = OnCmdKey(vk, scan, ext, opt);
            else if (opt) result = OnOptKey(vk, scan, ext);

            if (result == SWALLOW) swallowUp.Add(vk);
            else if (result == REPLACED) swallowUp.Remove(vk);
            return result != PASS;
        }

        bool OnCmd(bool down)
        {
            if (down)
            {
                if (cmdDown) return !cmdPass; // auto-repeat
                cmdDown = true;
                cmdPass = !ShouldRemap();
                if (optDown) optUsed = true;
                return !cmdPass;
            }
            if (!cmdDown) return false;
            bool pass = cmdPass;
            cmdDown = cmdPass = false;
            if (pass) return false;

            var inputs = new List<Native.INPUT>();
            if (injCtrl) inputs.Add(Key(VK_LCONTROL, true));
            if (injAlt) inputs.Add(Key(VK_LMENU, true)); // commits the Alt+Tab switcher
            Send(inputs);
            injCtrl = injAlt = false;
            return true;
        }

        bool OnOpt(bool down)
        {
            if (down)
            {
                if (optDown) return !optPass;
                optDown = true;
                optUsed = cmdDown && !cmdPass;
                optPass = !ShouldRemap();
                return !optPass;
            }
            if (!optDown) return false;
            bool pass = optPass;
            optDown = optPass = false;
            if (pass) return false;

            if (injWin)
                Send(new List<Native.INPUT> { Key(VK_LWIN, true) });
            else if (!optUsed)
                Send(new List<Native.INPUT> { Key(VK_LWIN, false), Key(VK_LWIN, true) }); // lone tap opens Start
            injWin = false;
            return true;
        }

        int OnCmdKey(int vk, ushort scan, bool ext, bool opt)
        {
            var c = config;
            bool shift = heldMods.Contains(VK_LSHIFT) || heldMods.Contains(VK_RSHIFT);
            bool ctrl = heldMods.Contains(VK_LCONTROL) || heldMods.Contains(VK_RCONTROL);
            int keepShift = shift ? SHIFT : 0;

            // Inside the Cmd+Tab switcher every key goes through with Alt held (Shift+Tab, arrows...).
            if (injAlt) return PASS;

            if (opt && vk == VK_ESCAPE) return Tap(VK_ESCAPE, CTRL | SHIFT);        // Cmd+Opt+Esc -> Task Manager
            if (ctrl && vk == 'Q') { Native.LockWorkStation(); return SWALLOW; }   // Cmd+Ctrl+Q -> lock

            switch (vk)
            {
                case VK_TAB:
                {
                    var inputs = new List<Native.INPUT>();
                    if (injCtrl) inputs.Add(Key(VK_LCONTROL, true));
                    inputs.Add(Key(VK_LMENU, false));
                    inputs.Add(Key(VK_TAB, false, scan, ext));
                    Send(inputs);
                    injCtrl = false;
                    injAlt = true;
                    return REPLACED;
                }
                case VK_SPACE: return RunAction(c.Spotlight);
                case 'Q': if (!shift) return Tap(VK_F4, ALT); break;
                case 'M':
                    if (!shift)
                    {
                        Native.PostMessage(Native.GetForegroundWindow(), Native.WM_SYSCOMMAND, new IntPtr(Native.SC_MINIMIZE), IntPtr.Zero);
                        return SWALLOW;
                    }
                    break;
                case VK_LEFT: return Tap(VK_HOME, keepShift);
                case VK_RIGHT: return Tap(VK_END, keepShift);
                case VK_UP: return Tap(VK_HOME, CTRL | keepShift);
                case VK_DOWN: return Tap(VK_END, CTRL | keepShift);
                case VK_BACK:
                    if (!shift) { Tap(VK_HOME, SHIFT); return Tap(VK_BACK, 0); } // delete to line start
                    break;
                case '3': if (shift) return RunAction(c.FullScreenshot); break;
                case '4': if (shift || c.Cmd4Screenshot) return RunAction(c.Screenshot); break;
                case '5': if (shift) return RunAction(c.ScreenshotApp); break;
                case VK_OEM_4: if (shift) return Tap(VK_TAB, CTRL | SHIFT); break;   // Cmd+Shift+[ -> previous tab
                case VK_OEM_6: if (shift) return Tap(VK_TAB, CTRL); break;           // Cmd+Shift+] -> next tab
                case 'H': if (c.VimArrows) return Tap(VK_LEFT, keepShift); break;
                case 'J': if (c.VimArrows) return Tap(VK_DOWN, keepShift); break;
                case 'K': if (c.VimArrows) return Tap(VK_UP, keepShift); break;
                case 'L': if (c.VimArrows) return Tap(VK_RIGHT, keepShift); break;
            }

            // Everything else: Cmd behaves as Ctrl.
            if (injCtrl) return PASS;
            Send(new List<Native.INPUT> { Key(VK_LCONTROL, false), Key(vk, false, scan, ext) });
            injCtrl = true;
            return REPLACED;
        }

        int OnOptKey(int vk, ushort scan, bool ext)
        {
            bool shift = heldMods.Contains(VK_LSHIFT) || heldMods.Contains(VK_RSHIFT);
            switch (vk)
            {
                case VK_LEFT: case VK_RIGHT: case VK_UP: case VK_DOWN:
                    return Tap(vk, CTRL | (shift ? SHIFT : 0));   // word / paragraph jumps
                case VK_BACK: case VK_DELETE:
                    return Tap(vk, CTRL);                         // delete word
                case 'L':
                    Native.LockWorkStation();                     // keep Win+L working
                    return SWALLOW;
            }

            // Everything else: the Windows key works as usual (Win+E, Win+R, Win+D, ...).
            if (injWin) return PASS;
            Send(new List<Native.INPUT> { Key(VK_LWIN, false), Key(vk, false, scan, ext) });
            injWin = true;
            return REPLACED;
        }

        // Press vk with exactly the modifiers in `want`, then restore the previous modifier state.
        int Tap(int vk, int want)
        {
            var before = new List<Native.INPUT>();
            var after = new List<Native.INPUT>();

            bool curShift = heldMods.Contains(VK_LSHIFT) || heldMods.Contains(VK_RSHIFT);
            bool curCtrl = injCtrl || heldMods.Contains(VK_LCONTROL) || heldMods.Contains(VK_RCONTROL);
            AdjustModifier(curShift, (want & SHIFT) != 0, VK_LSHIFT, new[] { VK_LSHIFT, VK_RSHIFT }, false, before, after);
            AdjustModifier(curCtrl, (want & CTRL) != 0, VK_LCONTROL, new[] { VK_LCONTROL, VK_RCONTROL }, injCtrl, before, after);
            AdjustModifier(injAlt, (want & ALT) != 0, VK_LMENU, new int[0], injAlt, before, after);
            AdjustModifier(injWin, (want & WIN) != 0, VK_LWIN, new int[0], injWin, before, after);

            var inputs = new List<Native.INPUT>(before);
            inputs.Add(Key(vk, false));
            inputs.Add(Key(vk, true));
            after.Reverse();
            inputs.AddRange(after);
            Send(inputs);
            return SWALLOW;
        }

        void AdjustModifier(bool current, bool wanted, int injectVk, int[] physicalVks, bool injected,
                            List<Native.INPUT> before, List<Native.INPUT> after)
        {
            if (current == wanted) return;
            if (wanted)
            {
                before.Add(Key(injectVk, false));
                after.Add(Key(injectVk, true));
                return;
            }
            var release = new List<int>();
            if (injected) release.Add(injectVk);
            foreach (var p in physicalVks)
                if (heldMods.Contains(p) && !release.Contains(p)) release.Add(p);
            foreach (var r in release)
            {
                before.Add(Key(r, true));
                after.Add(Key(r, false));
            }
        }

        int RunAction(string action)
        {
            if (string.IsNullOrEmpty(action)) return SWALLOW;
            if (action.StartsWith("keys:", StringComparison.OrdinalIgnoreCase))
            {
                int vk, mods;
                if (ParseKeys(action.Substring(5), out vk, out mods)) Tap(vk, mods);
                else RaiseError("Invalid key combination: " + action);
                return SWALLOW;
            }
            ThreadPool.QueueUserWorkItem(_ => Launch(action));
            return SWALLOW;
        }

        void Launch(string action)
        {
            try
            {
                ProcessStartInfo psi;
                if (action.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
                {
                    psi = new ProcessStartInfo("explorer.exe", action);
                }
                else if (action.StartsWith("\""))
                {
                    int end = action.IndexOf('"', 1);
                    if (end < 0) end = action.Length;
                    string file = action.Substring(1, end - 1);
                    string args = end + 1 < action.Length ? action.Substring(end + 1).Trim() : "";
                    psi = new ProcessStartInfo(file, args);
                }
                else
                {
                    psi = new ProcessStartInfo(action);
                }
                psi.UseShellExecute = true;
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                RaiseError("Could not start \"" + action + "\": " + ex.Message);
            }
        }

        public static bool ParseKeys(string text, out int vk, out int mods)
        {
            vk = 0;
            mods = 0;
            foreach (var raw in text.Split('+'))
            {
                var part = raw.Trim();
                switch (part.ToLowerInvariant())
                {
                    case "ctrl": case "control": mods |= CTRL; continue;
                    case "shift": mods |= SHIFT; continue;
                    case "alt": case "opt": mods |= ALT; continue;
                    case "win": case "windows": case "super": mods |= WIN; continue;
                    case "esc": part = "Escape"; break;
                    case "printscreen": case "prtsc": part = "PrintScreen"; break;
                }
                if (part.Length == 1 && char.IsDigit(part[0])) part = "D" + part;
                Keys key;
                if (part.Length == 0 || char.IsDigit(part[0]) || !Enum.TryParse(part, true, out key)) return false;
                vk = (int)key & 0xFF;
            }
            return vk != 0;
        }

        bool ShouldRemap()
        {
            var c = config;
            return c.Enabled && !ForegroundExcluded(c);
        }

        // Excluded = listed in ExcludedApps, or elevated while we are not (Windows would block
        // our injected keys there, so it is better to leave Alt/Win alone).
        bool ForegroundExcluded(Config c)
        {
            uint pid;
            Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out pid);
            if (pid == 0) return false;
            if (pid == cachedPid && c == cachedConfig) return cachedExcluded;

            bool excluded = false;
            IntPtr process = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (process == IntPtr.Zero)
            {
                excluded = !SelfElevated;
            }
            else
            {
                try
                {
                    if (!SelfElevated && IsProcessElevated(process))
                    {
                        excluded = true;
                    }
                    else if (c.ExcludedApps.Count > 0)
                    {
                        var sb = new StringBuilder(1024);
                        int size = sb.Capacity;
                        if (Native.QueryFullProcessImageName(process, 0, sb, ref size))
                            excluded = c.ExcludedApps.Contains(Path.GetFileName(sb.ToString()));
                    }
                }
                finally
                {
                    Native.CloseHandle(process);
                }
            }
            cachedPid = pid;
            cachedConfig = c;
            cachedExcluded = excluded;
            return excluded;
        }

        static bool IsProcessElevated(IntPtr process)
        {
            IntPtr token;
            if (!Native.OpenProcessToken(process, Native.TOKEN_QUERY, out token)) return true;
            try
            {
                int elevated, len;
                return Native.GetTokenInformation(token, Native.TokenElevation, out elevated, 4, out len) && elevated != 0;
            }
            finally
            {
                Native.CloseHandle(token);
            }
        }

        static Native.INPUT Key(int vk, bool up, ushort scan = 0, bool? extended = null)
        {
            var input = new Native.INPUT { type = Native.INPUT_KEYBOARD };
            input.U.ki.wVk = (ushort)vk;
            input.U.ki.wScan = scan != 0 ? scan : (ushort)Native.MapVirtualKey((uint)vk, 0);
            bool ext = extended.HasValue ? extended.Value : IsExtended(vk);
            input.U.ki.dwFlags = (ext ? Native.KEYEVENTF_EXTENDEDKEY : 0) | (up ? Native.KEYEVENTF_KEYUP : 0);
            input.U.ki.dwExtraInfo = Marker;
            return input;
        }

        // Without the extended flag, injected arrows/Home/End arrive as numpad keys and type
        // digits when NumLock is on.
        static bool IsExtended(int vk)
        {
            switch (vk)
            {
                case VK_LEFT: case VK_RIGHT: case VK_UP: case VK_DOWN:
                case VK_HOME: case VK_END: case VK_PRIOR: case VK_NEXT:
                case VK_INSERT: case VK_DELETE: case VK_SNAPSHOT:
                case VK_LWIN: case VK_RWIN: case VK_APPS:
                case VK_RCONTROL: case VK_RMENU: case VK_DIVIDE: case VK_NUMLOCK:
                    return true;
            }
            return false;
        }

        static void Send(List<Native.INPUT> inputs)
        {
            if (inputs.Count == 0) return;
            Native.SendInput((uint)inputs.Count, inputs.ToArray(), InputSize);
        }

        void RaiseError(string message)
        {
            var handler = Error;
            if (handler != null) handler(message);
        }
    }
}
