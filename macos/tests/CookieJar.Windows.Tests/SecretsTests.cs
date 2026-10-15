using System;
using System.IO;
using System.Text;
using CookieJar.Platform.Windows;

namespace CookieJar.Tests
{
    public static class SecretsTests
    {
        public static void Run()
        {
            // Temp folder only; never the real %LocalAppData%\RobloxMultiLauncher.
            string root = Path.Combine(Path.GetTempPath(), "cj-secrets-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                RoundTrip(Path.Combine(root, "roundtrip"));
                Unicode(Path.Combine(root, "unicode"));
                Overwrite(Path.Combine(root, "overwrite"));
                DeleteThenGet(Path.Combine(root, "delete"));
                Corrupt(Path.Combine(root, "corrupt"));
            }
            finally
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch (Exception) { }
            }
        }

        private static void RoundTrip(string dir)
        {
            WinSecretStore store = new WinSecretStore(dir);
            bool set = store.Set("cookie.main", "_|WARNING:-DO-NOT-SHARE|abc123");
            string got = store.Get("cookie.main");
            T.Check("secret: Set returns true", set);
            T.Check("secret: Set then Get round-trips", got == "_|WARNING:-DO-NOT-SHARE|abc123", got ?? "null");
        }

        private static void Unicode(string dir)
        {
            WinSecretStore store = new WinSecretStore(dir);
            string value = "héllo 世界 \U0001F36A";
            store.Set("unicode", value);
            T.Check("secret: unicode value round-trips", store.Get("unicode") == value);
        }

        private static void Overwrite(string dir)
        {
            WinSecretStore store = new WinSecretStore(dir);
            store.Set("k", "first");
            bool second = store.Set("k", "second");
            T.Check("secret: overwrite returns true", second);
            T.Check("secret: overwrite replaces the value", store.Get("k") == "second", store.Get("k") ?? "null");
        }

        private static void DeleteThenGet(string dir)
        {
            WinSecretStore store = new WinSecretStore(dir);
            store.Set("gone", "value");
            T.Check("secret: Delete returns true when the key existed", store.Delete("gone"));
            T.Check("secret: Get after Delete is null", store.Get("gone") == null);
            T.Check("secret: Delete of a missing key returns false", !store.Delete("gone"));
            T.Check("secret: Get of an unknown key is null", store.Get("never-set") == null);
        }

        private static void Corrupt(string dir)
        {
            WinSecretStore store = new WinSecretStore(dir);
            store.Set("c", "sensitive-value");
            string[] files = Directory.GetFiles(dir);
            T.Check("secret: one file written for one key", files.Length == 1, files.Length.ToString());

            // Overwrite the sealed bytes with junk: DPAPI must fail and Get must report no value.
            File.WriteAllBytes(files[0], Encoding.UTF8.GetBytes("not a dpapi blob"));
            T.Check("secret: corrupted file reads as null", store.Get("c") == null);
        }
    }
}
