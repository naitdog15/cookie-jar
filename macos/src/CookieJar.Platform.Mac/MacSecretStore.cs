using System;
using System.Collections.Generic;

namespace CookieJar.Platform.Mac
{
    /// <summary>
    /// Keychain-backed ISecretStore that drives the `security` tool.
    /// KNOWN LIMITATION: `security add-generic-password -w &lt;value&gt;` puts the secret on argv, so other local
    /// users can see it in `ps` for the few ms the process runs. The fix is `security -i` with the command fed
    /// on stdin, which needs an ICommandRunner overload that accepts stdin (contract request).
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
            return Succeeds(new List<string> { "add-generic-password", "-U", "-s", _service, "-a", key, "-w", value });
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
