using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace RbxMulti
{
    public sealed class UpdateInfo
    {
        public string Version;
        public string Revision;
        public string Notes;
    }

    public sealed class PreparedUpdate
    {
        public string ScriptPath;
        public string ConfigPath;
        public string WorkDirectory;
    }

    public static class UpdateService
    {
        private const string Repo = "naitdog15/cookie-jar";
        private static readonly Regex VersionPattern = new Regex(@"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)([ab]?)$");

        public static int CompareVersions(string left, string right)
        {
            Match a = VersionPattern.Match(left ?? "");
            Match b = VersionPattern.Match(right ?? "");
            if (!a.Success || !b.Success) throw new FormatException("The application version must be X.Y.Z, X.Y.Za or X.Y.Zb.");
            int result = 0;
            for (int i = 1; i <= 3; i++)
            {
                int x, y;
                if (!int.TryParse(a.Groups[i].Value, out x) || !int.TryParse(b.Groups[i].Value, out y) || x > 65534 || y > 65534)
                    throw new FormatException("Version components must be 0..65534.");
                if (result == 0) result = x.CompareTo(y);
            }
            return result != 0 ? result : ChannelRank(a.Groups[4].Value).CompareTo(ChannelRank(b.Groups[4].Value));
        }

        private static int ChannelRank(string suffix) { return suffix == "a" ? 0 : suffix == "b" ? 1 : 2; }

        public static string ValidateRevision(string revision)
        {
            if (!Regex.IsMatch(revision ?? "", "^[0-9a-f]{40}$")) throw new FormatException("GitHub returned an invalid source revision.");
            return revision;
        }

        public static UpdateInfo Check()
        {
            string json = GitHubText("repos/" + Repo + "/commits/main", false, 2 * 1024 * 1024);
            Dictionary<string, object> data = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
            object sha;
            if (data == null || !data.TryGetValue("sha", out sha)) throw new FormatException("GitHub did not return a source revision.");
            string revision = ValidateRevision(Convert.ToString(sha));
            string contents = "repos/" + Repo + "/contents/";
            string version = GitHubText(contents + "VERSION?ref=" + revision, true, 128).Trim();
            CompareVersions(version, version);
            return new UpdateInfo { Version = version, Revision = revision,
                Notes = GitHubText(contents + "docs/patch-notes/" + version + ".md?ref=" + revision, true, 128 * 1024) };
        }

        private static string GitHubText(string endpoint, bool raw, int limit)
        {
            using (MemoryStream output = new MemoryStream())
            {
                GitHubDownload(endpoint, raw, output, limit);
                return Encoding.UTF8.GetString(output.ToArray());
            }
        }

        private static void GitHubDownload(string endpoint, bool raw, Stream output, long limit)
        {
            long originalPosition = output.Position;
            ProcessStartInfo start = new ProcessStartInfo { FileName = "gh.exe",
                Arguments = "api --hostname github.com -H \"Accept: " + (raw ? "application/vnd.github.raw+json" : "application/vnd.github+json") + "\" \"" + endpoint + "\"",
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true, RedirectStandardError = true };
            try
            {
                using (Process process = Process.Start(start))
                {
                    Task<string> errors = process.StandardError.ReadToEndAsync();
                    Task copy = process.StandardOutput.BaseStream.CopyToAsync(output);
                    if (!copy.Wait(90000) || !process.WaitForExit(10000))
                    { try { process.Kill(); } catch { } throw new IOException("GitHub download timed out; try again later."); }
                    errors.Wait(10000);
                    if (process.ExitCode == 0)
                    {
                        if (output.Position - originalPosition > limit) throw new IOException("GitHub response exceeded its size limit.");
                        return;
                    }
                }
            }
            catch (Win32Exception) { } // GitHub CLI is optional for a public repository.
            output.SetLength(originalPosition);
            output.Position = originalPosition;
            try { Download("https://api.github.com/" + endpoint, output, limit, raw); }
            catch (WebException ex)
            {
                HttpWebResponse response = ex.Response as HttpWebResponse;
                if (response != null && (response.StatusCode == HttpStatusCode.NotFound || response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden))
                    throw new IOException("GitHub could not grant access. For this private repository, install GitHub CLI and sign in using 'gh auth login' with repository read access, then try again. GitHub rate limits can also cause access failures.");
                throw;
            }
        }

        private static HttpWebRequest Request(string url, bool raw)
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = "CookieJar/" + AppVersion.Display;
            request.Accept = raw ? "application/vnd.github.raw+json" : "application/vnd.github+json";
            request.Timeout = 30000;
            request.ReadWriteTimeout = 30000;
            return request;
        }

        private static void Download(string url, Stream output, long limit, bool raw)
        {
            using (WebResponse response = Request(url, raw).GetResponse())
            using (Stream input = response.GetResponseStream())
            {
                byte[] buffer = new byte[16384];
                int read;
                long total = 0;
                Stopwatch elapsed = Stopwatch.StartNew();
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    total += read;
                    if (total > limit || elapsed.Elapsed.TotalMinutes > 3) throw new IOException("Update download exceeded its size or time limit.");
                    output.Write(buffer, 0, read);
                }
            }
        }

        public static PreparedUpdate Prepare(UpdateInfo update, bool miniBar)
        {
            ValidateRevision(update.Revision);
            CompareVersions(update.Version, update.Version);
            string work = Path.Combine(Path.GetTempPath(), "CookieJarUpdate-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            string archive = Path.Combine(work, "source.zip");
            using (FileStream file = File.Create(archive))
                GitHubDownload("repos/" + Repo + "/zipball/" + update.Revision, false, file, 64 * 1024 * 1024);
            string script = Path.Combine(work, "install-update.ps1");
            using (Stream resource = typeof(UpdateService).Assembly.GetManifestResourceStream("RbxMulti.InstallUpdate.ps1"))
            {
                if (resource == null) throw new IOException("This build does not include the update installer.");
                using (FileStream file = File.Create(script)) resource.CopyTo(file);
            }
            Dictionary<string, object> config = new Dictionary<string, object>();
            config["Revision"] = update.Revision;
            config["Version"] = update.Version;
            config["ArchivePath"] = archive;
            config["WorkDirectory"] = work;
            config["TargetPath"] = Assembly.GetEntryAssembly().Location;
            config["ParentPid"] = Process.GetCurrentProcess().Id;
            config["ParentStartedUtcTicks"] = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks.ToString();
            config["MiniBar"] = miniBar;
            string configPath = Path.Combine(work, "update.json");
            File.WriteAllText(configPath, new JavaScriptSerializer().Serialize(config), new UTF8Encoding(false));
            PreparedUpdate prepared = new PreparedUpdate { ScriptPath = script, ConfigPath = configPath, WorkDirectory = work };
            using (Process process = Process.Start(StartInfo(prepared, "Prepare")))
            {
                if (!process.WaitForExit(180000)) { try { process.Kill(); } catch { } throw new IOException("Update build timed out. See " + work); }
                if (process.ExitCode != 0) throw new IOException("The update could not be built. Your installation is unchanged. See " + Path.Combine(work, "update.log"));
            }
            return prepared;
        }

        private static ProcessStartInfo StartInfo(PreparedUpdate update, string mode)
        {
            return new ProcessStartInfo {
                FileName = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
                Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + update.ScriptPath + "\" -Mode " + mode + " -ConfigPath \"" + update.ConfigPath + "\"",
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = update.WorkDirectory
            };
        }

        public static void StartInstall(PreparedUpdate update)
        {
            using (Process process = Process.Start(StartInfo(update, "Install")))
            {
                if (process == null) throw new IOException("Could not start the update installer.");
                string ready = Path.Combine(update.WorkDirectory, "install.ready");
                for (int i = 0; i < 100; i++)
                {
                    if (File.Exists(ready)) return;
                    if (process.WaitForExit(100)) throw new IOException("The update installer stopped before handoff. See " + update.WorkDirectory);
                }
                try { process.Kill(); } catch { }
                throw new IOException("The update installer did not become ready. Cookie Jar will remain open. See " + update.WorkDirectory);
            }
        }
    }
}
