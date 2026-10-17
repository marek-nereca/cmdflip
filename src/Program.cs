using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Security;
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
        }

        ContextMenuStrip BuildMenu()
        {
            var menu = new ContextMenuStrip();
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
            menu.Items.Add(cmd4Item);
            menu.Items.Add(vimItem);
            menu.Items.Add(new ToolStripSeparator());

            loginItem = new ToolStripMenuItem("Start at login", null, (s, e) => ToggleLogin());
            adminLoginItem = new ToolStripMenuItem("Start at login as administrator", null, (s, e) => ToggleAdminLogin());
            adminLoginItem.ToolTipText = "Needed for shortcuts to work in apps running as administrator.";
            menu.Items.Add(loginItem);
            menu.Items.Add(adminLoginItem);
            if (!KeyboardEngine.IsElevated)
                menu.Items.Add(new ToolStripMenuItem("Restart as administrator", null, (s, e) => RestartElevated()));
            menu.Items.Add(new ToolStripMenuItem("Edit settings file...", null, (s, e) => OpenConfig()));
            menu.Items.Add(new ToolStripMenuItem("Shortcut cheat sheet", null, (s, e) => ShowHelp()));
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
            snipItem.Checked = c.Screenshot == "ms-screenclip:";
            snipAppItem.Checked = c.Screenshot.IndexOf("Microsoft.ScreenSketch", StringComparison.OrdinalIgnoreCase) >= 0;
            customItem.Checked = !snipItem.Checked && !snipAppItem.Checked;
            customItem.Text = customItem.Checked && c.Screenshot.Length > 0 ? "Other app: " + ShortName(c.Screenshot) : "Other app...";
            loginItem.Checked = Registry.GetValue(@"HKEY_CURRENT_USER\" + RunKey, AppName, null) != null;
            adminLoginItem.Checked = AdminTaskExists();

            tray.Icon = c.Enabled ? iconOn : iconOff;
            var text = AppName + (c.Enabled ? "" : " (paused)") + (KeyboardEngine.IsElevated ? " - admin" : "");
            tray.Text = text;
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

        static bool AdminTaskExists()
        {
            try
            {
                var psi = new ProcessStartInfo("schtasks.exe", "/Query /TN " + AppName)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (var p = Process.Start(psi))
                {
                    p.WaitForExit(3000);
                    return p.ExitCode == 0;
                }
            }
            catch
            {
                return false;
            }
        }

        void ToggleAdminLogin()
        {
            string args;
            if (AdminTaskExists())
            {
                args = "/Delete /TN " + AppName + " /F";
            }
            else
            {
                // Built from XML because plain /Create gives the task a 72h time limit, no start on
                // battery and below-normal priority (bad for keyboard hook latency).
                string user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
                string xml =
                    "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\n" +
                    "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\n" +
                    "  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + user + "</UserId></LogonTrigger></Triggers>\n" +
                    "  <Principals><Principal id=\"Author\"><UserId>" + user + "</UserId>" +
                    "<LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>\n" +
                    "  <Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>" +
                    "<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>" +
                    "<ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Priority>4</Priority></Settings>\n" +
                    "  <Actions Context=\"Author\"><Exec><Command>" + SecurityElement.Escape(Application.ExecutablePath) + "</Command></Exec></Actions>\n" +
                    "</Task>\n";
                string file = Path.Combine(Path.GetTempPath(), "Cmdflip-task.xml");
                File.WriteAllText(file, xml, System.Text.Encoding.Unicode);
                args = "/Create /TN " + AppName + " /XML \"" + file + "\" /F";
            }
            try
            {
                var psi = new ProcessStartInfo("schtasks.exe", args) { Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden };
                using (var p = Process.Start(psi)) p.WaitForExit(10000);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return; // UAC prompt cancelled
            }
            // The scheduled task replaces the plain autostart entry.
            if (AdminTaskExists())
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
                "Cmd + Ctrl + Q\tlock screen\n\n" +
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
            int size = SystemInformation.SmallIconSize.Width * 2;
            using (var bmp = new Bitmap(size, size))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    var back = enabled ? Color.FromArgb(255, 32, 32, 36) : Color.FromArgb(255, 120, 120, 120);
                    using (var brush = new SolidBrush(back))
                    using (var path = RoundedRect(new RectangleF(0, 0, size - 1, size - 1), size / 4f))
                        g.FillPath(brush, path);
                    using (var font = new Font("Segoe UI Symbol", size * 0.55f, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (var fore = new SolidBrush(Color.White))
                    {
                        var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                        g.DrawString("⌘", font, fore, new RectangleF(0, size * 0.04f, size, size), fmt);
                    }
                }
                IntPtr h = bmp.GetHicon();
                var icon = (Icon)Icon.FromHandle(h).Clone();
                Native.DestroyIcon(h);
                return icon;
            }
        }

        static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        protected override void ExitThreadCore()
        {
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            watcher.Dispose();
            engine.Stop();
            tray.Visible = false;
            tray.Dispose();
            base.ExitThreadCore();
        }
    }
}
