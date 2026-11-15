using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace RbxMulti
{
    /// <summary>Plain-text support reports. Never reads account stores, cookies, or process command lines.</summary>
    internal static class DiagnosticReport
    {
        internal const int MaxLogBytes = 1024 * 1024;
        // Remove the entire line: quoted, encoded, or space-containing credentials must not leave fragments.
        private static readonly Regex CredentialLine = new Regex(
            @"(?i)(\.ROBLOSECURITY|_\|WARNING:-|_%7cWARNING|(?:authenticationticket|rbx-authentication-ticket|x-csrf-token|csrf|token|ticket|authorization|cookie|accesscode|linkcode|privateserverlinkcode)\s*[""']?\s*[:=]|\bBearer\s+\S+|(?:^|\s)-{1,2}(?:t|ticket|token|authenticationticket)\s+\S+|roblox-player:.*(?:gameinfo|ticket))");
        private static readonly Regex UserPath = new Regex(@"(?i)\b[A-Z]:[\\/]Users[\\/][^\\/\r\n""<>]+", RegexOptions.CultureInvariant);

        internal static string Redact(string value)
        {
            StringBuilder clean = new StringBuilder();
            using (StringReader reader = new StringReader(value ?? ""))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                    clean.AppendLine(CredentialLine.IsMatch(line) ? "[Credential-bearing line removed]" : UserPath.Replace(line, @"%USERPROFILE%"));
            }
            return clean.ToString();
        }

        internal static string Create(string version, string context, string history, string visibleLog)
        {
            StringBuilder report = new StringBuilder();
            report.AppendLine("Cookie Jar diagnostic report");
            report.AppendLine("Version: " + version);
            report.AppendLine("Exported: " + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture));
            report.AppendLine("Attach this text file to your support message and describe what happened and when.");
            report.AppendLine("Account stores and browser/Roblox logs are not collected. Known credential lines and Windows user paths are masked.");
            report.AppendLine("Activity may contain account/profile display names. Review before sharing.");
            report.AppendLine();
            report.AppendLine("=== System and launcher state ===");
            report.AppendLine(context);
            report.AppendLine();
            report.AppendLine("=== Recent saved activity (oldest first; includes earlier sessions) ===");
            report.AppendLine(history);
            report.AppendLine("=== Current on-screen activity (may overlap saved activity) ===");
            report.AppendLine(string.IsNullOrEmpty(visibleLog) ? "(No on-screen activity.)" : visibleLog);
            return Redact(report.ToString());
        }

        internal static string ReadHistory(string logPath)
        {
            return ReadLog(logPath + ".old", "Previous log") + Environment.NewLine + ReadLog(logPath, "Current log");
        }

        private static string ReadLog(string path, string label)
        {
            string heading = "--- " + label + " ---" + Environment.NewLine;
            try
            {
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    long start = Math.Max(0, stream.Length - MaxLogBytes);
                    int count = (int)(stream.Length - start);
                    byte[] bytes = new byte[count];
                    stream.Seek(start, SeekOrigin.Begin);
                    int read = 0;
                    while (read < count)
                    {
                        int next = stream.Read(bytes, read, count - read);
                        if (next == 0) break;
                        read += next;
                    }
                    int offset = 0;
                    if (start > 0)
                    {
                        // Drop a possibly partial UTF-8 character/line at the truncation boundary.
                        while (offset < read && bytes[offset++] != (byte)'\n') { }
                        heading += "(Older content omitted; newest complete lines retained.)" + Environment.NewLine;
                    }
                    return heading + Encoding.UTF8.GetString(bytes, offset, read - offset).TrimStart('\uFEFF');
                }
            }
            catch (FileNotFoundException) { return heading + "(No saved log.)"; }
            catch (DirectoryNotFoundException) { return heading + "(No saved log.)"; }
            catch (Exception ex) { return heading + "(Could not read this log: " + ex.GetType().Name + ". On-screen activity is included below.)"; }
        }

        internal static void Save(string path, string report)
        {
            File.WriteAllText(path, report, new UTF8Encoding(true));
        }
    }
}
