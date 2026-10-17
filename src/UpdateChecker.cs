using System;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace Cmdflip
{
    // Asks GitHub for the latest release. Failures (offline, private repo, rate limit) are silent.
    internal static class UpdateChecker
    {
        public const string ReleasesPage = "https://github.com/marek-nereca/cmdflip/releases/latest";
        const string LatestApi = "https://api.github.com/repos/marek-nereca/cmdflip/releases/latest";

        public static string CurrentVersion
        {
            get { return Application.ProductVersion; }
        }

        // Calls back on a thread-pool thread with (tag, url) when a newer release exists, or (null, null).
        public static void CheckAsync(Action<string, string> done)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string tag = null, url = null;
                try
                {
                    ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                    using (var web = new WebClient())
                    {
                        web.Headers[HttpRequestHeader.UserAgent] = "Cmdflip/" + CurrentVersion;
                        web.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
                        string json = web.DownloadString(LatestApi);
                        var t = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"");
                        var u = Regex.Match(json, "\"html_url\"\\s*:\\s*\"(https://github\\.com/[^\"]+/releases/tag/[^\"]+)\"");
                        if (t.Success && IsNewer(t.Groups[1].Value, CurrentVersion))
                        {
                            tag = t.Groups[1].Value;
                            url = u.Success ? u.Groups[1].Value : ReleasesPage;
                        }
                    }
                }
                catch (Exception)
                {
                }
                done(tag, url);
            });
        }

        // Dev builds (0.0.0-dev) never report updates; a pre-release is older than the same final version.
        public static bool IsNewer(string candidate, string current)
        {
            Version a, b;
            string preA, preB;
            if (!TryParse(candidate, out a, out preA) || !TryParse(current, out b, out preB)) return false;
            if (b == new Version(0, 0, 0)) return false;
            int cmp = a.CompareTo(b);
            if (cmp != 0) return cmp > 0;
            return preA == null && preB != null;
        }

        static bool TryParse(string s, out Version version, out string prerelease)
        {
            prerelease = null;
            s = s.Trim().TrimStart('v', 'V');
            int plus = s.IndexOf('+');
            if (plus >= 0) s = s.Substring(0, plus);
            int dash = s.IndexOf('-');
            if (dash >= 0)
            {
                prerelease = s.Substring(dash + 1);
                s = s.Substring(0, dash);
            }
            if (!Version.TryParse(s, out version)) return false;
            version = new Version(version.Major, version.Minor, Math.Max(version.Build, 0));
            return true;
        }
    }
}
