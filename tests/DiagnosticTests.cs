using System;
using System.IO;
using System.Text;
using RbxMulti;

internal static class DiagnosticTests
{
    private static int failed;
    private static int checks;
    private static void Check(string name, bool ok)
    {
        checks++;
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
        if (!ok) failed++;
    }

    public static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "CookieJar-DiagnosticTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string log = Path.Combine(root, "launcher.log");
            string report = DiagnosticReport.Create("1.3.0", "Windows: test\nTracked clients: 6", DiagnosticReport.ReadHistory(log), "launch failed");
            Check("version, timestamp, context and current UI activity survive missing disk logs", report.Contains("Version: 1.3.0") && report.Contains("Exported:") && report.Contains("Tracked clients: 6") && report.Contains("launch failed"));
            Check("missing logs reported without failing", report.Contains("(No saved log.)"));
            Check("sharing instructions explain display names", report.Contains("Review before sharing") && report.Contains("display names"));
            Check("empty UI has explicit marker", DiagnosticReport.Create("1", "", "", "").Contains("(No on-screen activity.)"));

            File.WriteAllText(log + ".old", "old failure", Encoding.UTF8);
            File.WriteAllText(log, "new failure: caf\u00e9 \u2713", Encoding.UTF8);
            string history = DiagnosticReport.ReadHistory(log);
            Check("previous and current logs ordered", history.IndexOf("old failure") < history.IndexOf("new failure"));
            Check("Unicode logs retained", history.Contains("caf\u00e9 \u2713"));
            Check("no embedded BOM in report", history.IndexOf('\uFEFF') < 0);
            Check("snapshot leaves source files intact", File.ReadAllText(log + ".old") == "old failure" && File.ReadAllText(log) == "new failure: caf\u00e9 \u2713");
            using (FileStream locked = new FileStream(log, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Check("unreadable log preserves history and useful warning", DiagnosticReport.ReadHistory(log).Contains("Could not read this log: IOException") && DiagnosticReport.ReadHistory(log).Contains("old failure"));
            using (FileStream writer = new FileStream(log, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                Check("snapshot reads a shared log", DiagnosticReport.ReadHistory(log).Contains("new failure"));

            File.WriteAllText(log, new string('x', DiagnosticReport.MaxLogBytes + 10) + "\nnewest complete line\n", Encoding.UTF8);
            history = DiagnosticReport.ReadHistory(log);
            Check("oversized history bounded with notice", history.Length < DiagnosticReport.MaxLogBytes + 200 && history.Contains("Older content omitted"));
            Check("truncation preserves newest complete lines", history.Contains("newest complete line") && !history.Contains("xxxxxxxxxx"));

            string[] credentials = {
                ".ROBLOSECURITY=secretA", "Cookie: secretB", "token = secretC", "ticket: secretD",
                "authenticationTicket=secretE", "rbx-authentication-ticket: secretF", "X-CSRF-TOKEN: secretG",
                "Authorization: Bearer secretH", "_\u007cWARNING:-DO-NOT-SHARE-THIS.secretI",
                "roblox-player:1+gameinfo:secretJ", "player.exe -t secretK", "privateServerLinkCode=secretL",
                "accessCode=secretM", "\"token\": \"secretN with spaces\"", "Bearer secretO",
                "_%7CWARNING:-DO-NOT-SHARE-THIS.secretP"
            };
            foreach (string credential in credentials)
            {
                string clean = DiagnosticReport.Redact("before\n" + credential + "\nafter");
                Check("credential masked: " + credential.Split('=')[0].Split(':')[0], !clean.Contains("secret") && clean.Contains("before") && clean.Contains("after"));
            }
            Check("ordinary authentication failures stay useful", DiagnosticReport.Redact("token rejected (expired or logged out)\nticket failed: server error").Contains("token rejected"));
            string paths = DiagnosticReport.Redact("C:\\Users\\Alice\\AppData\\Local\\Roblox\nc:/users/Bob/Roblox\nPID 42 failed");
            Check("Windows user names masked in both path styles", !paths.Contains("Alice") && !paths.Contains("Bob") && paths.Contains("%USERPROFILE%") && paths.Contains("PID 42"));
            report = DiagnosticReport.Create("1.3.0", credentials[0], credentials[1], credentials[2]);
            Check("redaction covers context, saved history and screen fallback", !report.Contains("secret"));

            string output = Path.Combine(root, "support.txt");
            DiagnosticReport.Save(output, report + "\u00e9\u2713");
            Check("saved export is readable UTF-8 text", File.ReadAllText(output).EndsWith("\u00e9\u2713"));
            Check("UTF-8 BOM supports Windows text editors", File.ReadAllBytes(output)[0] == 0xEF);
            bool saveFailed = false;
            try { DiagnosticReport.Save(Path.Combine(root, "missing", "support.txt"), report); }
            catch (DirectoryNotFoundException) { saveFailed = true; }
            Check("write errors reach caller for UI error handling", saveFailed);
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine(checks + " checks, " + failed + " failures; no accounts, network, mouse or keyboard used.");
        return failed == 0 ? 0 : 1;
    }
}
