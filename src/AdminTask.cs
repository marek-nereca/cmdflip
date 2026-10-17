using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security;
using System.Security.Principal;
using System.Windows.Forms;

namespace Cmdflip
{
    // The "Start at login as administrator" scheduled task.
    //
    // The task starts Cmdflip.exe elevated without a UAC prompt, so it is only offered when the exe
    // lives in Program Files, where only administrators can replace it. In a per-user install
    // (%LOCALAPPDATA%\Programs) any program running as the user could swap the exe and be elevated
    // at the next login.
    //
    // Creating or removing the task needs admin rights. Cmdflip restarts itself elevated with
    // --admin-task=on|off and registers the task through the Task Scheduler API, so the task
    // definition never passes through a file another program could change.
    internal static class AdminTask
    {
        public const string Name = "Cmdflip";
        public const string OnArg = "--admin-task=on", OffArg = "--admin-task=off";

        const int TASK_CREATE_OR_UPDATE = 6;
        const int TASK_LOGON_INTERACTIVE_TOKEN = 3;

        public static bool InProtectedFolder
        {
            get
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(Application.ExecutablePath)) + "\\";
                string[] roots =
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    Environment.GetEnvironmentVariable("ProgramW6432")
                };
                foreach (var root in roots)
                    if (!string.IsNullOrEmpty(root) && dir.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                        return true;
                return false;
            }
        }

        public static bool Exists()
        {
            try
            {
                var psi = new ProcessStartInfo("schtasks.exe", "/Query /TN " + Name)
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

        // Called from the tray: runs the elevated helper and waits for it. False if UAC was cancelled.
        public static bool Set(bool on)
        {
            try
            {
                var psi = new ProcessStartInfo(Application.ExecutablePath, on ? OnArg : OffArg) { Verb = "runas", UseShellExecute = true };
                using (var p = Process.Start(psi)) p.WaitForExit(15000);
                return true;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return false; // UAC prompt cancelled
            }
        }

        // Runs in the elevated helper process. Returns the process exit code.
        public static int Apply(bool on)
        {
            try
            {
                if (on && !InProtectedFolder) return 2;
                object service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
                Call(service, "Connect");
                object root = Call(service, "GetFolder", "\\");
                if (on)
                    Call(root, "RegisterTask", Name, BuildXml(), TASK_CREATE_OR_UPDATE, null, null, TASK_LOGON_INTERACTIVE_TOKEN, null);
                else
                    Call(root, "DeleteTask", Name, 0);
                return 0;
            }
            catch (Exception)
            {
                return 1;
            }
        }

        // Built from XML because the defaults give the task a 72h time limit, no start on battery and
        // below-normal priority (bad for keyboard hook latency).
        static string BuildXml()
        {
            string user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
            return
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
        }

        static object Call(object target, string method, params object[] args)
        {
            return target.GetType().InvokeMember(method, BindingFlags.InvokeMethod, null, target, args);
        }
    }
}
