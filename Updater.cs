// Update check: asks GitHub for the newest Embris release and, when it's newer than this copy,
// downloads the Windows installer from it and runs it with /update (installs quietly, reopens Embris).
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Embris
{
    class ReleaseInfo
    {
        public string Tag = "";        // "v1.6.0"
        public Version Version;        // 1.6.0
        public string PageUrl = "";    // the release on github.com
        public string SetupUrl = "";   // EmbrisSetup.exe, if attached on its own
        public string ZipUrl = "";     // EmbrisInstaller.zip
        public string Notes = "";
    }

    static class Updater
    {
        public const string Repo = "Vyrzou/Embris";
        public const string ReleasesPage = "https://github.com/" + Repo + "/releases";
        const string LatestApi = "https://api.github.com/repos/" + Repo + "/releases/latest";

        static WebClient Client()
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; // TLS 1.2 (GitHub needs it)
            var wc = new WebClient();
            wc.Headers[HttpRequestHeader.UserAgent] = "Embris/" + Ui.Version; // GitHub's API refuses requests without one
            return wc;
        }

        public static Version Parse(string tag)
        {
            var m = Regex.Match(tag ?? "", @"\d+(\.\d+){0,3}");
            Version v;
            if (!m.Success || !Version.TryParse(m.Value.Contains('.') ? m.Value : m.Value + ".0", out v)) return null;
            // Missing parts count as 0, so 1.6 == 1.6.0.
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));
        }

        public static bool IsNewer(ReleaseInfo r)
        {
            return r != null && r.Version != null && r.Version > Parse(Ui.Version);
        }

        // The newest published release, or null when there is none yet (or GitHub can't be reached:
        // the exception says why).
        public static Task<ReleaseInfo> LatestAsync()
        {
            return Task.Factory.StartNew(() =>
            {
                string json;
                try
                {
                    using (var wc = Client()) json = wc.DownloadString(LatestApi);
                }
                catch (WebException ex)
                {
                    var resp = ex.Response as HttpWebResponse;
                    if (resp != null && resp.StatusCode == HttpStatusCode.NotFound) return null; // no releases yet
                    throw;
                }
                return ParseRelease(json);
            });
        }

        // Reads what's needed from GitHub's release JSON (small, flat strings, so no JSON library).
        public static ReleaseInfo ParseRelease(string json)
        {
            var r = new ReleaseInfo { Tag = JsonString(json, "tag_name"), PageUrl = JsonString(json, "html_url"), Notes = JsonString(json, "body") };
            r.Version = Parse(r.Tag);
            foreach (Match m in Regex.Matches(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+)\""))
            {
                string url = m.Groups[1].Value, name = Path.GetFileName(Uri.UnescapeDataString(url));
                if (name.Equals("EmbrisSetup.exe", StringComparison.OrdinalIgnoreCase)) r.SetupUrl = url;
                else if (name.Equals("EmbrisInstaller.zip", StringComparison.OrdinalIgnoreCase)) r.ZipUrl = url;
            }
            if (r.PageUrl == "") r.PageUrl = ReleasesPage;
            return r;
        }

        static string JsonString(string json, string key)
        {
            var m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (!m.Success) return "";
            return Regex.Replace(m.Groups[1].Value, @"\\(u[0-9a-fA-F]{4}|.)", e =>
            {
                string s = e.Groups[1].Value;
                switch (s[0])
                {
                    case 'n': return "\n";
                    case 'r': return "";
                    case 't': return "\t";
                    case 'u': return ((char)Convert.ToInt32(s.Substring(1), 16)).ToString();
                    default: return s;
                }
            });
        }

        public static bool CanInstall(ReleaseInfo r) { return r.SetupUrl != "" || r.ZipUrl != ""; }

        // Downloads the installer into a temp folder and returns the path to EmbrisSetup.exe.
        public static Task<string> DownloadSetupAsync(ReleaseInfo r)
        {
            return Task.Factory.StartNew(() =>
            {
                string dir = Path.Combine(Path.GetTempPath(), "Embris-update");
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                Directory.CreateDirectory(dir);
                string setup = Path.Combine(dir, "EmbrisSetup.exe");
                using (var wc = Client())
                {
                    if (r.SetupUrl != "") wc.DownloadFile(r.SetupUrl, setup);
                    else
                    {
                        string zip = Path.Combine(dir, "EmbrisInstaller.zip");
                        wc.DownloadFile(r.ZipUrl, zip);
                        using (var a = ZipFile.OpenRead(zip))
                        {
                            var entry = a.Entries.FirstOrDefault(e => e.Name.Equals("EmbrisSetup.exe", StringComparison.OrdinalIgnoreCase));
                            if (entry == null) throw new InvalidDataException("The download has no EmbrisSetup.exe in it.");
                            entry.ExtractToFile(setup, true);
                        }
                    }
                }
                return setup;
            });
        }

        // Starts the downloaded installer; it closes this Embris, installs and reopens it.
        public static void RunSetup(string setup)
        {
            Process.Start(new ProcessStartInfo(setup, "/update") { UseShellExecute = true });
        }
    }
}
