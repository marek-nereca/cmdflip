using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Cmdflip
{
    // Immutable snapshot of settings. The keyboard engine reads it from the hook
    // thread; reloading swaps the whole object.
    internal sealed class Config
    {
        public bool Enabled = true;
        public bool VimArrows = true;
        public bool Cmd4Screenshot = true;
        public bool CheckForUpdates = true;
        public bool CapsLockAsEsc = false;
        public string Screenshot = "ms-screenclip:";
        public string FullScreenshot = "keys:Win+PrintScreen";
        public string ScreenshotApp = @"shell:AppsFolder\Microsoft.ScreenSketch_8wekyb3d8bbwe!App";
        public string Spotlight = "keys:Win+S";
        public HashSet<string> ExcludedApps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static string Dir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cmdflip"); }
        }

        public static string FilePath
        {
            get { return Path.Combine(Dir, "config.ini"); }
        }

        public Config Clone()
        {
            var c = (Config)MemberwiseClone();
            c.ExcludedApps = new HashSet<string>(ExcludedApps, StringComparer.OrdinalIgnoreCase);
            return c;
        }

        public static Config Load()
        {
            var c = new Config();
            if (!File.Exists(FilePath))
            {
                c.Save();
                return c;
            }
            var seen = new HashSet<string>();
            foreach (var raw in File.ReadAllLines(FilePath))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                var key = line.Substring(0, eq).Trim();
                var value = line.Substring(eq + 1).Trim();
                seen.Add(key.ToLowerInvariant());
                switch (key.ToLowerInvariant())
                {
                    case "enabled": c.Enabled = ParseBool(value, c.Enabled); break;
                    case "vimarrows": c.VimArrows = ParseBool(value, c.VimArrows); break;
                    case "cmd4screenshot": c.Cmd4Screenshot = ParseBool(value, c.Cmd4Screenshot); break;
                    case "checkforupdates": c.CheckForUpdates = ParseBool(value, c.CheckForUpdates); break;
                    case "capslockasesc": c.CapsLockAsEsc = ParseBool(value, c.CapsLockAsEsc); break;
                    case "screenshot": c.Screenshot = value; break;
                    case "fullscreenshot": c.FullScreenshot = value; break;
                    case "screenshotapp": c.ScreenshotApp = value; break;
                    case "spotlight": c.Spotlight = value; break;
                    case "excludedapps":
                        c.ExcludedApps.Clear();
                        foreach (var app in value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            var name = app.Trim();
                            if (name.Length > 0) c.ExcludedApps.Add(name);
                        }
                        break;
                }
            }
            // A file from an older version lacks newer settings; rewrite it so they become visible.
            if (!seen.IsSupersetOf(KnownKeys)) c.Save();
            return c;
        }

        static readonly string[] KnownKeys =
        {
            "enabled", "vimarrows", "cmd4screenshot", "checkforupdates", "capslockasesc",
            "screenshot", "fullscreenshot", "screenshotapp", "spotlight", "excludedapps"
        };

        static bool ParseBool(string s, bool fallback)
        {
            s = s.ToLowerInvariant();
            if (s == "true" || s == "1" || s == "yes" || s == "on") return true;
            if (s == "false" || s == "0" || s == "no" || s == "off") return false;
            return fallback;
        }

        public void Save()
        {
            Directory.CreateDirectory(Dir);
            var sb = new StringBuilder();
            sb.AppendLine("# Cmdflip settings. Changes are applied as soon as the file is saved.");
            sb.AppendLine("#");
            sb.AppendLine("# Action values can be:");
            sb.AppendLine("#   a program path       C:\\Program Files\\ShareX\\ShareX.exe");
            sb.AppendLine("#   a URI / shell link   ms-screenclip:   or   shell:AppsFolder\\<AppUserModelId>");
            sb.AppendLine("#   a key combination    keys:Win+Shift+S");
            sb.AppendLine("#   empty                (shortcut disabled, nothing happens)");
            sb.AppendLine();
            sb.AppendLine("Enabled = " + Bool(Enabled));
            sb.AppendLine();
            sb.AppendLine("# Cmd+Shift+4 - capture a region");
            sb.AppendLine("Screenshot = " + Screenshot);
            sb.AppendLine("# Also trigger Screenshot with plain Cmd+4 (instead of Ctrl+4)");
            sb.AppendLine("Cmd4Screenshot = " + Bool(Cmd4Screenshot));
            sb.AppendLine("# Cmd+Shift+3 - capture the whole screen");
            sb.AppendLine("FullScreenshot = " + FullScreenshot);
            sb.AppendLine("# Cmd+Shift+5 - open the screenshot app");
            sb.AppendLine("ScreenshotApp = " + ScreenshotApp);
            sb.AppendLine("# Cmd+Space - search / launcher (e.g. keys:Alt+Space)");
            sb.AppendLine("Spotlight = " + Spotlight);
            sb.AppendLine();
            sb.AppendLine("# Cmd+H/J/K/L act as Left/Down/Up/Right arrows");
            sb.AppendLine("VimArrows = " + Bool(VimArrows));
            sb.AppendLine();
            sb.AppendLine("# Caps Lock acts as Esc (Shift+Caps Lock still toggles Caps Lock)");
            sb.AppendLine("CapsLockAsEsc = " + Bool(CapsLockAsEsc));
            sb.AppendLine();
            sb.AppendLine("# Comma separated process names where keys behave like stock Windows,");
            sb.AppendLine("# e.g. mstsc.exe, vmconnect.exe, game.exe");
            sb.AppendLine("ExcludedApps = " + string.Join(", ", ExcludedApps));
            sb.AppendLine();
            sb.AppendLine("# Check GitHub once a day for a newer version (nothing is sent except the request)");
            sb.AppendLine("CheckForUpdates = " + Bool(CheckForUpdates));
            File.WriteAllText(FilePath, sb.ToString(), Encoding.UTF8);
        }

        static string Bool(bool b)
        {
            return b ? "true" : "false";
        }
    }
}
