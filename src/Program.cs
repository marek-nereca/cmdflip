using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Cmdflip
{
    internal static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            // Elevated helper started from the tray menu; see AdminTask.
            if (Array.IndexOf(args, AdminTask.OnArg) >= 0) { Environment.ExitCode = AdminTask.Apply(true); return; }
            if (Array.IndexOf(args, AdminTask.OffArg) >= 0) { Environment.ExitCode = AdminTask.Apply(false); return; }

            bool created;
            using (var mutex = new Mutex(true, @"Local\Cmdflip.SingleInstance", out created))
            {
                if (!created)
                {
                    // --restart: the previous instance is shutting down, wait for it.
                    if (Array.IndexOf(args, "--restart") < 0) return;
                    try { if (!mutex.WaitOne(5000)) return; }
                    catch (AbandonedMutexException) { }
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayApp());
            }
        }
    }

    internal sealed class TrayApp : ApplicationContext
    {
        const string AppName = "Cmdflip";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        readonly KeyboardEngine engine;
        readonly NotifyIcon tray;
        readonly Control invoker = new Control();
        readonly FileSystemWatcher watcher;
        readonly System.Windows.Forms.Timer reloadTimer = new System.Windows.Forms.Timer { Interval = 300 };
        readonly Icon iconOn, iconOff;

        ToolStripMenuItem enabledItem, vimItem, cmd4Item, loginItem, adminLoginItem;
        ToolStripMenuItem snipItem, snipAppItem, customItem;
        ToolStripMenuItem updateItem, autoUpdateItem, capsItem;
        readonly System.Windows.Forms.Timer updateTimer = new System.Windows.Forms.Timer { Interval = 10 * 1000 };
        string updateUrl, notifiedTag;

        public TrayApp()
        {
            invoker.CreateControl();
            iconOn = MakeIcon(true);
            iconOff = MakeIcon(false);

            engine = new KeyboardEngine(Config.Load());
            engine.Error += msg => invoker.BeginInvoke(new Action(() => ShowError(msg)));
            engine.Start();

            tray = new NotifyIcon { Visible = true, ContextMenuStrip = BuildMenu() };
            tray.DoubleClick += (s, e) => Update(c => c.Enabled = !c.Enabled);
            tray.ContextMenuStrip.Opening += (s, e) => RefreshMenu();
            RefreshMenu();
            // Created by an older version from a per-user install, see AdminTask.
            if (adminLoginItem.Checked && !AdminTask.InProtectedFolder)
                ShowError("\"Start at login as administrator\" is unsafe for this install, because other programs " +
                          "can replace Cmdflip.exe in this folder. Turn it off in the tray menu, or reinstall for all users.");

            watcher = new FileSystemWatcher(Config.Dir, "config.ini");
            watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size;
            watcher.SynchronizingObject = invoker;
            watcher.Changed += (s, e) => { reloadTimer.Stop(); reloadTimer.Start(); };
            watcher.Created += (s, e) => { reloadTimer.Stop(); reloadTimer.Start(); };
            watcher.Renamed += (s, e) => { reloadTimer.Stop(); reloadTimer.Start(); };
            watcher.EnableRaisingEvents = true;
            reloadTimer.Tick += (s, e) => { reloadTimer.Stop(); ReloadConfig(); };

            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;

            // First check shortly after start, then once a day.
            tray.BalloonTipClicked += (s, e) => { if (updateUrl != null) OpenUrl(updateUrl); };
            updateTimer.Tick += (s, e) =>
            {
                updateTimer.Interval = 24 * 60 * 60 * 1000;
                if (engine.Config.CheckForUpdates) CheckForUpdates(false);
            };
            updateTimer.Start();

            // The installer signals this event to close a running copy before upgrading or uninstalling.
            // The current user gets access explicitly, so a non-elevated installer can close an elevated copy.
            var security = new EventWaitHandleSecurity();
            security.AddAccessRule(new EventWaitHandleAccessRule(WindowsIdentity.GetCurrent().User,
                EventWaitHandleRights.Synchronize | EventWaitHandleRights.Modify, AccessControlType.Allow));
            bool createdNew;
            quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName, out createdNew, security);
            quitWait = ThreadPool.RegisterWaitForSingleObject(quitEvent,
                (state, timedOut) => invoker.BeginInvoke(new Action(ExitThread)), null, Timeout.Infinite, true);
        }

        const string QuitEventName = @"Local\Cmdflip.Quit";
        EventWaitHandle quitEvent;
        RegisteredWaitHandle quitWait;

        ContextMenuStrip BuildMenu()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add(new ToolStripMenuItem(AppName + " " + UpdateChecker.CurrentVersion) { Enabled = false });
            updateItem = new ToolStripMenuItem("Update available", null, (s, e) => OpenUrl(updateUrl ?? UpdateChecker.ReleasesPage));
            updateItem.Font = new Font(updateItem.Font, FontStyle.Bold);
            updateItem.Visible = false;
            menu.Items.Add(updateItem);
            menu.Items.Add(new ToolStripSeparator());
            enabledItem = new ToolStripMenuItem("Enabled", null, (s, e) => Update(c => c.Enabled = !c.Enabled));
            enabledItem.Font = new Font(enabledItem.Font, FontStyle.Bold);
            menu.Items.Add(enabledItem);
            menu.Items.Add(new ToolStripSeparator());

            var shots = new ToolStripMenuItem("Cmd+Shift+4 opens");
            snipItem = new ToolStripMenuItem("Windows region snip", null, (s, e) => Update(c => c.Screenshot = "ms-screenclip:"));
            snipAppItem = new ToolStripMenuItem("Snipping Tool app", null,
                (s, e) => Update(c => c.Screenshot = @"shell:AppsFolder\Microsoft.ScreenSketch_8wekyb3d8bbwe!App"));
            customItem = new ToolStripMenuItem("Other app...", null, (s, e) => ChooseScreenshotApp());
            shots.DropDownItems.AddRange(new ToolStripItem[] { snipItem, snipAppItem, customItem });
            menu.Items.Add(shots);

            cmd4Item = new ToolStripMenuItem("Plain Cmd+4 takes screenshot too", null, (s, e) => Update(c => c.Cmd4Screenshot = !c.Cmd4Screenshot));
            vimItem = new ToolStripMenuItem("Cmd+H/J/K/L as arrows", null, (s, e) => Update(c => c.VimArrows = !c.VimArrows));
            capsItem = new ToolStripMenuItem("Caps Lock as Esc", null, (s, e) => Update(c => c.CapsLockAsEsc = !c.CapsLockAsEsc));
            capsItem.ToolTipText = "Shift+Caps Lock still toggles Caps Lock.";
            menu.Items.Add(cmd4Item);
            menu.Items.Add(vimItem);
            menu.Items.Add(capsItem);
            menu.Items.Add(new ToolStripSeparator());

            loginItem = new ToolStripMenuItem("Start at login", null, (s, e) => ToggleLogin());
            adminLoginItem = new ToolStripMenuItem("Start at login as administrator", null, (s, e) => ToggleAdminLogin());
            menu.Items.Add(loginItem);
            menu.Items.Add(adminLoginItem);
            if (!KeyboardEngine.IsElevated)
                menu.Items.Add(new ToolStripMenuItem("Restart as administrator", null, (s, e) => RestartElevated()));
            menu.Items.Add(new ToolStripMenuItem("Edit settings file...", null, (s, e) => OpenConfig()));
            menu.Items.Add(new ToolStripMenuItem("Shortcut cheat sheet", null, (s, e) => ShowHelp()));
            var updates = new ToolStripMenuItem("Updates");
            autoUpdateItem = new ToolStripMenuItem("Check automatically", null, (s, e) => Update(c => c.CheckForUpdates = !c.CheckForUpdates));
            updates.DropDownItems.Add(autoUpdateItem);
            updates.DropDownItems.Add(new ToolStripMenuItem("Check now", null, (s, e) => CheckForUpdates(true)));
            menu.Items.Add(updates);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Exit", null, (s, e) => ExitThread()));
            return menu;
        }

        void RefreshMenu()
        {
            var c = engine.Config;
            enabledItem.Checked = c.Enabled;
            vimItem.Checked = c.VimArrows;
            cmd4Item.Checked = c.Cmd4Screenshot;
            autoUpdateItem.Checked = c.CheckForUpdates;
            capsItem.Checked = c.CapsLockAsEsc;
            snipItem.Checked = c.Screenshot == "ms-screenclip:";
            snipAppItem.Checked = c.Screenshot.IndexOf("Microsoft.ScreenSketch", StringComparison.OrdinalIgnoreCase) >= 0;
            customItem.Checked = !snipItem.Checked && !snipAppItem.Checked;
            customItem.Text = customItem.Checked && c.Screenshot.Length > 0 ? "Other app: " + ShortName(c.Screenshot) : "Other app...";
            loginItem.Checked = Registry.GetValue(@"HKEY_CURRENT_USER\" + RunKey, AppName, null) != null;
            adminLoginItem.Checked = AdminTask.Exists();
            // An existing task can always be removed; a new one needs an install in Program Files.
            bool adminAllowed = AdminTask.InProtectedFolder;
            adminLoginItem.Enabled = adminAllowed || adminLoginItem.Checked;
            adminLoginItem.Text = adminAllowed || adminLoginItem.Checked
                ? "Start at login as administrator"
                : "Start at login as administrator (needs install for all users)";
            adminLoginItem.ToolTipText = adminAllowed
                ? "Needed for shortcuts to work in apps running as administrator."
                : "Only available when Cmdflip is installed for all users (in Program Files), where other " +
                  "programs can't replace it. Reinstall and choose \"Install for all users\".";

            tray.Icon = c.Enabled ? iconOn : iconOff;
            var text = AppName + (c.Enabled ? "" : " (paused)") + (KeyboardEngine.IsElevated ? " - admin" : "");
            tray.Text = text;
        }

        void CheckForUpdates(bool manual)
        {
            UpdateChecker.CheckAsync((tag, url) => invoker.BeginInvoke(new Action(() =>
            {
                if (tag != null)
                {
                    updateUrl = url;
                    updateItem.Text = "Update available: " + tag + " - download";
                    updateItem.Visible = true;
                    if (manual || notifiedTag != tag)
                        tray.ShowBalloonTip(8000, AppName, "Version " + tag + " is available. Click to download.", ToolTipIcon.Info);
                    notifiedTag = tag;
                }
                else if (manual)
                {
                    MessageBox.Show("You have the latest version (" + UpdateChecker.CurrentVersion + "), " +
                        "or the update server could not be reached.", AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            })));
        }

        static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception) { }
        }

        static string ShortName(string action)
        {
            var s = action.Trim('"');
            try { return Path.GetFileName(s.Split('"')[0]); } catch { return s; }
        }

        void Update(Action<Config> change)
        {
            var c = engine.Config.Clone();
            change(c);
            engine.Config = c;
            watcher.EnableRaisingEvents = false;
            try { c.Save(); }
            catch (Exception ex) { ShowError("Could not save settings: " + ex.Message); }
            finally { watcher.EnableRaisingEvents = true; }
            RefreshMenu();
        }

        void ReloadConfig()
        {
            try
            {
                engine.Config = Config.Load();
                RefreshMenu();
            }
            catch (IOException)
            {
                reloadTimer.Start(); // file still being written, try again shortly
            }
            catch (Exception ex)
            {
                ShowError("Could not read settings: " + ex.Message);
            }
        }

        void ChooseScreenshotApp()
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = "Choose the app Cmd+Shift+4 should open";
                dlg.Filter = "Programs (*.exe;*.lnk;*.bat;*.cmd)|*.exe;*.lnk;*.bat;*.cmd|All files (*.*)|*.*";
                dlg.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                if (dlg.ShowDialog() == DialogResult.OK)
                    Update(c => c.Screenshot = dlg.FileName);
            }
        }

        void ToggleLogin()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKey, true))
            {
                if (key.GetValue(AppName) != null) key.DeleteValue(AppName);
                else key.SetValue(AppName, "\"" + Application.ExecutablePath + "\"");
            }
            RefreshMenu();
        }

        void ToggleAdminLogin()
        {
            bool on = !AdminTask.Exists();
            if (on && !AdminTask.InProtectedFolder) return;
            if (!AdminTask.Set(on)) return;
            if (on && !AdminTask.Exists())
                ShowError("Could not create the start-at-login task.");
            // The scheduled task replaces the plain autostart entry.
            if (AdminTask.Exists())
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                    if (key.GetValue(AppName) != null) key.DeleteValue(AppName);
            RefreshMenu();
        }

        void RestartElevated()
        {
            try
            {
                var psi = new ProcessStartInfo(Application.ExecutablePath, "--restart") { Verb = "runas", UseShellExecute = true };
                Process.Start(psi);
                ExitThread();
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // UAC prompt cancelled, keep running as is
            }
        }

        static void OpenConfig()
        {
            Process.Start(new ProcessStartInfo("notepad.exe", "\"" + Config.FilePath + "\""));
        }

        static void ShowHelp()
        {
            MessageBox.Show(
                "Left Alt = Cmd      Windows key = Opt      Right Alt = unchanged\n\n" +
                "Cmd + any key\tCtrl + key (C, V, X, Z, A, S, F, T, W, R, N...)\n" +
                "Cmd + click\tCtrl + click\n" +
                "Cmd + Tab\tswitch apps\n" +
                "Cmd + Q\tclose app (Alt+F4)\n" +
                "Cmd + M\tminimize window\n" +
                "Cmd + Space\tsearch\n" +
                "Cmd + Left/Right\tline start / end\n" +
                "Cmd + Up/Down\tdocument start / end\n" +
                "Cmd + Backspace\tdelete to line start\n" +
                "Cmd + Shift + [ / ]\tprevious / next tab\n" +
                "Cmd + Shift + 3\tscreenshot of whole screen\n" +
                "Cmd + Shift + 4\tscreenshot of region\n" +
                "Cmd + Shift + 5\tscreenshot app\n" +
                "Cmd + H/J/K/L\tarrows (optional)\n" +
                "Cmd + Opt + Esc\tTask Manager\n" +
                "Cmd + Ctrl + Q\tlock screen\n" +
                "Caps Lock\tEsc (optional, Shift+Caps Lock toggles caps)\n\n" +
                "Opt + Left/Right\tword left / right\n" +
                "Opt + Backspace/Del\tdelete word\n" +
                "Opt + other keys\tnormal Windows key shortcuts (Win+E, Win+R...)\n" +
                "Opt alone\tStart menu\n\n" +
                "Double-click the tray icon to pause / resume.",
                "Cmdflip", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        void ShowError(string message)
        {
            tray.ShowBalloonTip(5000, "Cmdflip", message, ToolTipIcon.Warning);
        }

        void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            // Key-up events are lost while the secure desktop is shown; start from a clean state.
            if (e.Reason == SessionSwitchReason.SessionUnlock || e.Reason == SessionSwitchReason.SessionLogon ||
                e.Reason == SessionSwitchReason.ConsoleConnect || e.Reason == SessionSwitchReason.RemoteConnect)
                engine.Reset(true);
            else
                engine.Reset(false);
        }

        void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume) engine.Reset(true);
        }

        static Icon MakeIcon(bool enabled)
        {
            // Rendered at the exact tray size for the current DPI, so Windows doesn't rescale it.
            using (var bmp = IconArt.Render(SystemInformation.SmallIconSize.Width, enabled))
            {
                IntPtr h = bmp.GetHicon();
                var icon = (Icon)Icon.FromHandle(h).Clone();
                Native.DestroyIcon(h);
                return icon;
            }
        }

        protected override void ExitThreadCore()
        {
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            updateTimer.Stop();
            quitWait.Unregister(null);
            quitEvent.Dispose();
            watcher.Dispose();
            engine.Stop();
            tray.Visible = false;
            tray.Dispose();
            base.ExitThreadCore();
        }
    }
}
