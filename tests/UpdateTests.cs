using System;
using System.IO;
using System.Reflection;
using RbxMulti;

public static class UpdateTests
{
    private static int failed;
    private static void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) failed++; }
    private static void Reject(string name, Action action) { bool rejected = false; try { action(); } catch (FormatException) { rejected = true; } Check(name, rejected); }
    public static int Main(string[] args)
    {
        Check("major ordering", UpdateService.CompareVersions("2.0.0", "1.99.99") > 0);
        Check("numeric minor ordering", UpdateService.CompareVersions("1.10.0", "1.9.9") > 0);
        Check("numeric patch ordering", UpdateService.CompareVersions("1.0.10", "1.0.9") > 0);
        Check("equal versions", UpdateService.CompareVersions("1.1.0", "1.1.0") == 0);
        Check("alpha before beta", UpdateService.CompareVersions("1.1.0a", "1.1.0b") < 0);
        Check("beta before stable", UpdateService.CompareVersions("1.1.0b", "1.1.0") < 0);
        Check("next numeric alpha after prior stable", UpdateService.CompareVersions("1.2.0a", "1.1.9") > 0);
        Reject("invalid version rejected", delegate { UpdateService.CompareVersions("1.0.0;echo bad", "1.0.0"); });
        Reject("leading zero rejected", delegate { UpdateService.CompareVersions("01.0.0", "1.0.0"); });
        Reject("overflow rejected even if major differs", delegate { UpdateService.CompareVersions("2.99999999999999.0", "1.0.0"); });
        Reject("invalid revision rejected", delegate { UpdateService.ValidateRevision("main/../../bad"); });
        Check("full immutable revision accepted", UpdateService.ValidateRevision(new string('a', 40)) == new string('a', 40));
        if (args.Length > 0 && (args[0] == "--live" || args[0] == "--prepare-live"))
        {
            try {
                UpdateInfo info = UpdateService.Check();
                Check("live GitHub main version and notes", info.Version.Length > 0 && info.Notes.Contains("Cookie Jar " + info.Version));
                Console.WriteLine("GitHub main: " + info.Version + " at " + info.Revision);
                using (MemoryStream archive = new MemoryStream())
                {
                    typeof(UpdateService).GetMethod("GitHubDownload", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null,
                        new object[] { "repos/naitdog15/cookie-jar/zipball/" + info.Revision, false, archive, 64L * 1024 * 1024 });
                    byte[] bytes = archive.ToArray();
                    Check("private source archive downloads as binary ZIP", bytes.Length > 4 && bytes[0] == 0x50 && bytes[1] == 0x4b);
                }
                if (args[0] == "--prepare-live")
                {
                    PreparedUpdate prepared = UpdateService.Prepare(info, false);
                    Check("private GitHub snapshot builds through real updater", System.Diagnostics.FileVersionInfo.GetVersionInfo(Path.Combine(prepared.WorkDirectory, "build", "CookieJar.exe")).ProductVersion == info.Version);
                    Console.WriteLine("Prepared only; no installation: " + prepared.WorkDirectory);
                }
            } catch (Exception ex) { Console.WriteLine(ex); return 1; }
        }
        return failed == 0 ? 0 : 1;
    }
}
