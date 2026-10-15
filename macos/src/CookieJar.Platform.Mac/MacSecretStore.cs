using System;
using System.Collections.Generic;

namespace CookieJar.Platform.Mac
{
    /// <summary>
    /// Keychain-backed ISecretStore that drives the `security` tool.
    /// The secret never appears on a command line (ps shows argv to every local user): Set feeds
    /// `add-generic-password ...` to `security -i` on stdin. That needs an IStdinCommandRunner; with a plain
    /// ICommandRunner Set refuses (returns false) instead of falling back to argv. Values, keys and the service name
    /// may not contain CR, LF or NUL (they cannot be passed safely to the interactive parser).
    /// The value is stored as given (no hex encoding). No returned value or error text ever contains the secret.
    /// </summary>
    public sealed class MacSecretStore : ISecretStore
    {
        // MAC-UNVERIFIED: `security` exits 44 (errSecItemNotFound) when the item is missing.
        private const int ItemNotFoundExitCode = 44;
        // MAC-UNVERIFIED: the Keychain may show an access prompt; the timeout bounds how long that can block.
        private const int TimeoutMs = 15000;
        private const string SecurityTool = "security";

        private readonly ICommandRunner _runner;
        private readonly string _service;

        public MacSecretStore(ICommandRunner runner, string service = "com.cookiejar.app")
        {
            if (runner == null) throw new ArgumentNullException("runner");
            _runner = runner;
            _service = service ?? "com.cookiejar.app";
        }

        public bool Set(string key, string value)
        {
            if (string.IsNullOrEmpty(key) || value == null) return false;
            IStdinCommandRunner stdinRunner = _runner as IStdinCommandRunner;
            if (stdinRunner == null) return false;   // never put the secret on argv
            if (!SafeForInteractive(key) || !SafeForInteractive(value) || !SafeForInteractive(_service)) return false;
            string command = "add-generic-password -U -s " + Quote(_service) + " -a " + Quote(key) + " -w " + Quote(value) + "\n";
            try
            {
                CommandResult result = stdinRunner.Run(SecurityTool, new List<string> { "-i" }, command, TimeoutMs);
                // MAC-UNVERIFIED: in interactive mode `security` may exit 0 even when the command failed, and reports
                // the failure on stderr; so success also requires an empty stderr.
                return result.Success && string.IsNullOrWhiteSpace(result.StandardError);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool SafeForInteractive(string text)
        {
            return text.IndexOf('\n') < 0 && text.IndexOf('\r') < 0 && text.IndexOf('\0') < 0;
        }

        // MAC-UNVERIFIED: `security -i` splits a line like a shell: double quotes group, backslash escapes " and \.
        private static string Quote(string text)
        {
            return "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        public string Get(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            CommandResult result;
            try
            {
                result = _runner.Run(SecurityTool, new List<string> { "find-generic-password", "-s", _service, "-a", key, "-w" }, TimeoutMs);
            }
            catch (Exception)
            {
                return null;
            }
            if (!result.Success) return null; // includes exit 44 (not found)

            // `-w` prints the value followed by exactly one newline; drop that newline only.
            string output = result.StandardOutput;
            if (output.Length > 0 && output[output.Length - 1] == '\n') output = output.Substring(0, output.Length - 1);
            return output;
        }

        public bool Delete(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            return Succeeds(new List<string> { "delete-generic-password", "-s", _service, "-a", key });
        }

        private bool Succeeds(List<string> arguments)
        {
            try
            {
                return _runner.Run(SecurityTool, arguments, TimeoutMs).Success;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
