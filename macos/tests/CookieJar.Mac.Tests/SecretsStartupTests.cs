using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using CookieJar.Platform;
using CookieJar.Platform.Mac;

namespace CookieJar.Tests
{
    /// <summary>One call the fake runner received.</summary>
    internal sealed class RecordedCall
    {
        public string FileName;
        public List<string> Arguments;
        public string Stdin;   // null when the call had no stdin
    }

    /// <summary>
    /// Fake for `security` (a small in-memory Keychain) and `launchctl`/`open` (always succeed unless told otherwise).
    /// Records every call so tests can assert the exact argv.
    /// </summary>
    internal sealed class RecordingRunner : IStdinCommandRunner
    {
        public readonly List<RecordedCall> Calls = new List<RecordedCall>();
        public readonly Dictionary<string, string> Keychain = new Dictionary<string, string>(StringComparer.Ordinal);
        public bool FailToStart;
        public bool FailLaunchctl;

        public string StderrOnSuccess = "";   // lets a test simulate `security -i` exiting 0 while reporting an error

        public CommandResult Run(string fileName, IReadOnlyList<string> arguments, int timeoutMs)
        {
            return Run(fileName, arguments, null, timeoutMs);
        }

        public CommandResult Run(string fileName, IReadOnlyList<string> arguments, string standardInput, int timeoutMs)
        {
            Calls.Add(new RecordedCall { FileName = fileName, Arguments = new List<string>(arguments), Stdin = standardInput });
            if (fileName == "security" && arguments.Count == 1 && arguments[0] == "-i")
            {
                if (FailToStart) return new CommandResult(-1, "", "could not start", false, true);
                // Interactive mode: every stdin line is one command, split like a shell (double quotes, backslash escapes).
                CommandResult last = new CommandResult(0, "", "", false, false);
                foreach (string line in (standardInput ?? "").Split('\n'))
                {
                    if (line.Length == 0) continue;
                    List<string> argv = Tokenize(line);
                    last = Security(argv);
                    if (!last.Success) return last;
                }
                return new CommandResult(last.ExitCode, last.StandardOutput, StderrOnSuccess, false, false);
            }
            if (FailToStart) return new CommandResult(-1, "", "could not start", false, true);
            if (fileName == "security") return Security(arguments);
            if (fileName == "launchctl" && FailLaunchctl) return new CommandResult(5, "", "Bootstrap failed", false, false);
            return new CommandResult(0, "", "", false, false);
        }

        private CommandResult Security(IReadOnlyList<string> a)
        {
            string key = ArgAfter(a, "-a");
            switch (a[0])
            {
                case "add-generic-password":
                    Keychain[key] = ArgAfter(a, "-w");
                    return new CommandResult(0, "", "", false, false);
                case "find-generic-password":
                    string value;
                    if (!Keychain.TryGetValue(key, out value)) return new CommandResult(44, "", "The specified item could not be found in the keychain.", false, false);
                    return new CommandResult(0, value + "\n", "", false, false);
                case "delete-generic-password":
                    if (Keychain.Remove(key)) return new CommandResult(0, "", "", false, false);
                    return new CommandResult(44, "", "The specified item could not be found in the keychain.", false, false);
            }
            return new CommandResult(2, "", "unknown", false, false);
        }

        private static List<string> Tokenize(string line)
        {
            List<string> tokens = new List<string>();
            System.Text.StringBuilder cur = new System.Text.StringBuilder();
            bool inQuote = false, any = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '\\' && i + 1 < line.Length) { cur.Append(line[++i]); any = true; }
                else if (c == '"') { inQuote = !inQuote; any = true; }
                else if (c == ' ' && !inQuote) { if (any) { tokens.Add(cur.ToString()); cur.Clear(); any = false; } }
                else { cur.Append(c); any = true; }
            }
            if (any) tokens.Add(cur.ToString());
            return tokens;
        }

        private static string ArgAfter(IReadOnlyList<string> a, string flag)
        {
            for (int i = 0; i + 1 < a.Count; i++)
            {
                if (a[i] == flag) return a[i + 1];
            }
            return null;
        }
    }

    public static class SecretsStartupTests
    {
        private const string Home = "/Users/tester";
        private const string PlistPath = "/Users/tester/Library/LaunchAgents/com.cookiejar.app.plist";

        public static void Run()
        {
            KeychainTests();
            PlistTests();
            StartupFlowTests();
        }

        private static bool Same(IReadOnlyList<string> actual, params string[] expected)
        {
            return actual.SequenceEqual(expected);
        }

        private static void KeychainTests()
        {
            RecordingRunner runner = new RecordingRunner();
            MacSecretStore store = new MacSecretStore(runner);

            bool set = store.Set("cookies", "s3cret");
            T.Check("keychain Set returns true", set);
            T.Check("keychain Set uses `security -i` and sends the command on stdin",
                runner.Calls.Count == 1 && runner.Calls[0].FileName == "security" && Same(runner.Calls[0].Arguments, "-i") &&
                runner.Calls[0].Stdin == "add-generic-password -U -s \"com.cookiejar.app\" -a \"cookies\" -w \"s3cret\"\n",
                runner.Calls[0].Stdin);
            T.Check("keychain secret never appears in any argv", !runner.Calls.Any(c => c.Arguments.Any(a => a.Contains("s3cret"))));

            T.Check("keychain Get round-trips the value", store.Get("cookies") == "s3cret");
            T.Check("keychain Get argv is exact",
                Same(runner.Calls[runner.Calls.Count - 1].Arguments, "find-generic-password", "-s", "com.cookiejar.app", "-a", "cookies", "-w"));

            int callsBefore = runner.Calls.Count;
            T.Check("keychain refuses values or keys with CR/LF/NUL (cannot be sent safely) without calling security",
                !store.Set("newline", "abc\n") && !store.Set("k\r", "v") && !store.Set("k", "a\0b") && runner.Calls.Count == callsBefore);

            T.Check("keychain Set with quotes and backslashes round-trips", store.Set("quoted", "a\"b\\c d") && store.Get("quoted") == "a\"b\\c d");

            FakeCommandRunner plain = new FakeCommandRunner();
            MacSecretStore noStdin = new MacSecretStore(plain);
            T.Check("keychain with a runner that has no stdin refuses to store (never falls back to argv)",
                !noStdin.Set("k", "secret-value") && plain.Calls.Count == 0);

            RecordingRunner lies = new RecordingRunner { StderrOnSuccess = "security: SecKeychainItemCreateFromContent: User interaction is not allowed." };
            T.Check("keychain Set is false when security -i exits 0 but reports an error on stderr", !new MacSecretStore(lies).Set("k", "v"));

            T.Check("keychain Get on missing key returns null", store.Get("absent") == null);

            T.Check("keychain Delete returns true when it existed", store.Delete("cookies"));
            T.Check("keychain Delete argv is exact",
                Same(runner.Calls[runner.Calls.Count - 1].Arguments, "delete-generic-password", "-s", "com.cookiejar.app", "-a", "cookies"));
            T.Check("keychain Delete returns false when missing", !store.Delete("cookies"));
            T.Check("keychain Get after Delete returns null", store.Get("cookies") == null);

            T.Check("keychain empty key is rejected", !store.Set("", "x") && store.Get("") == null && !store.Delete(null));
            T.Check("keychain null value is rejected", !store.Set("k", null));

            MacSecretStore custom = new MacSecretStore(runner, "com.example.svc");
            custom.Set("k", "v");
            T.Check("keychain custom service name is used",
                runner.Calls[runner.Calls.Count - 1].Stdin == "add-generic-password -U -s \"com.example.svc\" -a \"k\" -w \"v\"\n");

            RecordingRunner broken = new RecordingRunner { FailToStart = true };
            MacSecretStore brokenStore = new MacSecretStore(broken);
            T.Check("keychain without security: Set false, Get null, Delete false",
                !brokenStore.Set("k", "secret-value") && brokenStore.Get("k") == null && !brokenStore.Delete("k"));
        }

        private static void PlistTests()
        {
            string path = "/Applications/Cookie & Jar/Cookie Jar.app/Contents/MacOS/CookieJar";
            string plist = MacStartupService.BuildPlist("com.cookiejar.app", path);

            string expected =
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
                "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n" +
                "<plist version=\"1.0\">\n" +
                "<dict>\n" +
                "  <key>Label</key>\n" +
                "  <string>com.cookiejar.app</string>\n" +
                "  <key>ProgramArguments</key>\n" +
                "  <array>\n" +
                "    <string>/Applications/Cookie &amp; Jar/Cookie Jar.app/Contents/MacOS/CookieJar</string>\n" +
                "    <string>--minibar</string>\n" +
                "  </array>\n" +
                "  <key>RunAtLoad</key>\n" +
                "  <true/>\n" +
                "  <key>ProcessType</key>\n" +
                "  <string>Interactive</string>\n" +
                "</dict>\n" +
                "</plist>\n";
            T.Check("plist content is exact for a path with & and spaces", plist == expected);

            XmlDocument doc = new XmlDocument();
            bool wellFormed;
            try { doc.LoadXml(plist); wellFormed = true; }
            catch (XmlException) { wellFormed = false; }
            T.Check("plist parses as well-formed XML", wellFormed);
            if (wellFormed)
            {
                T.Check("plist ProgramArguments keeps the original path after parsing",
                    doc.SelectSingleNode("/plist/dict/array/string[1]").InnerText == path);
                T.Check("plist Label is com.cookiejar.app",
                    doc.SelectSingleNode("/plist/dict/string[1]").InnerText == "com.cookiejar.app");
            }
            T.Check("plist escapes & as &amp;", plist.Contains("Cookie &amp; Jar") && !plist.Contains("Cookie & Jar"));

            string tricky = "<a>'\"&";
            string trickyPlist = MacStartupService.BuildPlist("x", tricky);
            XmlDocument trickyDoc = new XmlDocument();
            trickyDoc.LoadXml(trickyPlist);
            T.Check("plist escapes < > ' \" & and round-trips them",
                trickyDoc.SelectSingleNode("/plist/dict/array/string[1]").InnerText == tricky);
        }

        private static void StartupFlowTests()
        {
            RecordingRunner runner = new RecordingRunner();
            Dictionary<string, string> files = new Dictionary<string, string>(StringComparer.Ordinal);
            string exe = "/Applications/Cookie & Jar/Cookie Jar.app/Contents/MacOS/CookieJar";
            MacStartupService svc = new MacStartupService(runner, Home, exe,
                p => files.ContainsKey(p), (p, c) => files[p] = c, p => files.Remove(p), () => 501);

            T.Check("startup is disabled before SetEnabled", !svc.IsEnabled());
            T.Check("startup SetEnabled(true) returns true", svc.SetEnabled(true));
            T.Check("startup writes the plist at the LaunchAgents path", files.ContainsKey(PlistPath));
            T.Check("startup writes exactly BuildPlist output",
                files.ContainsKey(PlistPath) && files[PlistPath] == MacStartupService.BuildPlist("com.cookiejar.app", exe));
            T.Check("startup enable runs launchctl bootstrap with exact argv",
                runner.Calls.Count == 1 && runner.Calls[0].FileName == "launchctl" &&
                Same(runner.Calls[0].Arguments, "bootstrap", "gui/501", PlistPath));
            T.Check("startup IsEnabled is true after enabling", svc.IsEnabled());

            T.Check("startup SetEnabled(false) returns true", svc.SetEnabled(false));
            T.Check("startup disable runs launchctl bootout with exact argv",
                runner.Calls.Count == 2 && runner.Calls[1].FileName == "launchctl" &&
                Same(runner.Calls[1].Arguments, "bootout", "gui/501/com.cookiejar.app"));
            T.Check("startup disable deletes the plist", !files.ContainsKey(PlistPath) && !svc.IsEnabled());

            RecordingRunner failing = new RecordingRunner { FailLaunchctl = true };
            Dictionary<string, string> files2 = new Dictionary<string, string>(StringComparer.Ordinal);
            MacStartupService besteffort = new MacStartupService(failing, Home, exe,
                p => files2.ContainsKey(p), (p, c) => files2[p] = c, p => files2.Remove(p), () => 501);
            T.Check("startup enable succeeds when launchctl fails (best effort)", besteffort.SetEnabled(true) && besteffort.IsEnabled());

            Dictionary<string, string> files3 = new Dictionary<string, string>(StringComparer.Ordinal);
            RecordingRunner quiet = new RecordingRunner();
            MacStartupService throwingWrite = new MacStartupService(quiet, Home, exe,
                p => files3.ContainsKey(p), (p, c) => { throw new System.IO.IOException("disk full"); }, p => files3.Remove(p), () => 501);
            T.Check("startup SetEnabled(true) returns false when the write throws", !throwingWrite.SetEnabled(true));
            T.Check("startup does not call launchctl when the write throws", quiet.Calls.Count == 0);

            MacStartupService noExe = new MacStartupService(quiet, Home, "", p => false, (p, c) => { }, p => { }, () => 501);
            T.Check("startup SetEnabled(true) without an executable path returns false", !noExe.SetEnabled(true));
        }
    }
}
