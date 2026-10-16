using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace RbxMulti
{
    /// <summary>
    /// Talks to Roblox: validates a token and obtains a one-time launch ticket.
    /// Token and ticket values are never put into error text.
    /// </summary>
    public static class TicketLauncher
    {
        public const string AdoptMePage = "https://www.roblox.com/games/920587237/Adopt-Me";
        private const string AuthenticatedUrl = "https://users.roblox.com/v1/users/authenticated";
        private const string TicketUrl = "https://auth.roblox.com/v1/authentication-ticket";
        private const int TimeoutMs = 15000;

        private static readonly Regex TicketPattern = new Regex(@"^[A-Za-z0-9_\-\.=]{8,4000}$");
        private static readonly Random Rng = new Random();
        private static readonly object RngLock = new object();

        static TicketLauncher()
        {
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;   // TLS 1.2
        }

        // ---------------- pure helpers (unit tested) ----------------

        public static bool IsValidTicket(string ticket)
        {
            return ticket != null && TicketPattern.IsMatch(ticket);
        }

        public static long NewBrowserTrackerId()
        {
            lock (RngLock)
            {
                // 12 digits, like the real web client.
                return 100000000000L + (long)(Rng.NextDouble() * 899999999999.0);
            }
        }

        public static long UnixMs(DateTime utcNow)
        {
            return (long)(utcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
        }

        /// <summary>Builds the roblox-player: URI. The ticket must already satisfy IsValidTicket.</summary>
        public static string BuildLaunchUri(string ticket, long placeId, long browserTrackerId, long launchTimeMs)
        {
            string id = browserTrackerId.ToString(CultureInfo.InvariantCulture);
            string place = placeId.ToString(CultureInfo.InvariantCulture);
            string placeLauncher = "https://assetgame.roblox.com/game/PlaceLauncher.ashx?request=RequestGame"
                + "&browserTrackerId=" + id + "&placeId=" + place + "&isPlayTogetherGame=false";
            return "roblox-player:1+launchmode:play+gameinfo:" + ticket
                + "+launchtime:" + launchTimeMs.ToString(CultureInfo.InvariantCulture)
                + "+placelauncherurl:" + Uri.EscapeDataString(placeLauncher)
                + "+browsertrackerid:" + id
                + "+robloxLocale:en_us+gameLocale:en_us+channel:+LaunchExp:InApp";
        }

        /// <summary>Extracts id and name from {"id":1,"name":"x",...}. Returns false when either is missing.</summary>
        public static bool ParseUser(string json, out long userId, out string username)
        {
            userId = 0;
            username = null;
            if (string.IsNullOrEmpty(json)) return false;
            Match mi = Regex.Match(json, "\"id\"\\s*:\\s*(\\d+)");
            Match mn = Regex.Match(json, "\"name\"\\s*:\\s*\"([A-Za-z0-9_]{1,40})\"");
            if (!mi.Success || !mn.Success) return false;
            if (!long.TryParse(mi.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out userId)) return false;
            username = mn.Groups[1].Value;
            return userId > 0;
        }

        // ---------------- network ----------------

        private sealed class Reply
        {
            public int Status;                 // -1 = no HTTP answer (network problem)
            public string NetworkProblem;
            public WebHeaderCollection Headers;
            public string Body;
        }

        private static Reply Send(string url, string method, string token, string csrf)
        {
            Reply reply = new Reply();
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = method;
                req.Timeout = TimeoutMs;
                req.ReadWriteTimeout = TimeoutMs;
                req.AllowAutoRedirect = false;
                req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) RobloxMultiLauncher";
                req.Accept = "application/json";
                req.Headers.Add("Cookie", ".ROBLOSECURITY=" + token);
                if (method == "POST")
                {
                    req.Referer = "https://www.roblox.com/";
                    req.ContentType = "application/json";
                    req.ContentLength = 0;
                    if (csrf != null) req.Headers.Add("X-CSRF-TOKEN", csrf);
                }
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    Fill(reply, resp);
                }
            }
            catch (WebException ex)
            {
                HttpWebResponse resp = ex.Response as HttpWebResponse;
                if (resp != null)
                {
                    using (resp) { Fill(reply, resp); }
                }
                else
                {
                    reply.Status = -1;
                    reply.NetworkProblem = ex.Status.ToString();
                }
            }
            catch (Exception ex)
            {
                reply.Status = -1;
                reply.NetworkProblem = ex.GetType().Name;
            }
            return reply;
        }

        private static void Fill(Reply reply, HttpWebResponse resp)
        {
            reply.Status = (int)resp.StatusCode;
            reply.Headers = resp.Headers;
            try
            {
                using (Stream s = resp.GetResponseStream())
                {
                    if (s != null)
                    {
                        byte[] buf = new byte[65536];
                        int n = s.Read(buf, 0, buf.Length);
                        reply.Body = n > 0 ? Encoding.UTF8.GetString(buf, 0, n) : "";
                    }
                }
            }
            catch (Exception)
            {
                reply.Body = "";
            }
        }

        /// <summary>
        /// Checks a token against Roblox. status is "Valid", "Expired" or "Error".
        /// </summary>
        public static bool Validate(string token, out string status, out long userId, out string username, out string error)
        {
            status = "Error";
            userId = 0;
            username = null;
            error = null;
            Reply r = Send(AuthenticatedUrl, "GET", token, null);
            if (r.Status == 200)
            {
                if (ParseUser(r.Body, out userId, out username))
                {
                    status = "Valid";
                    return true;
                }
                error = "Roblox answered, but the reply was not understood.";
                return false;
            }
            if (r.Status == 401 || r.Status == 403)
            {
                status = "Expired";
                error = "Roblox rejected this token. It is wrong, expired, or the account was logged out.";
                return false;
            }
            error = r.Status < 0
                ? "Could not reach Roblox (" + r.NetworkProblem + "). Check your internet connection."
                : "Roblox answered with an unexpected status (" + r.Status + ").";
            return false;
        }

        /// <summary>Gets a one-time launch ticket. expired is set when the token itself is no longer accepted.</summary>
        public static bool GetTicket(string token, out string ticket, out bool expired, out string error)
        {
            ticket = null;
            expired = false;
            error = null;
            Reply r = Send(TicketUrl, "POST", token, null);
            if (r.Status == 403 && r.Headers != null)
            {
                string csrf = r.Headers["X-CSRF-TOKEN"];
                if (!string.IsNullOrEmpty(csrf) && csrf.IndexOfAny(new char[] { '\r', '\n' }) < 0)
                {
                    r = Send(TicketUrl, "POST", token, csrf);
                }
            }

            if (r.Status == 200 && r.Headers != null)
            {
                string t = r.Headers["rbx-authentication-ticket"];
                if (!IsValidTicket(t))
                {
                    error = "Roblox did not return a usable launch ticket.";
                    return false;
                }
                ticket = t;
                return true;
            }
            if (r.Status == 401)
            {
                expired = true;
                error = "token rejected (expired or logged out) - re-add this account";
                return false;
            }
            if (r.Status == 403)
            {
                error = "Roblox refused the request (token rejected or account needs verification)";
                return false;
            }
            if (r.Status == 429)
            {
                error = "Roblox is rate limiting requests - wait a minute and try again";
                return false;
            }
            error = r.Status < 0
                ? "could not reach Roblox (" + r.NetworkProblem + ")"
                : "unexpected Roblox status " + r.Status;
            return false;
        }
    }

    public enum LaunchOutcome
    {
        Ok,
        TicketFailed,
        StartFailed,
        TimedOut,
        Cancelled,
        Skipped
    }

    public class AccountLaunchResult
    {
        public LaunchOutcome Outcome;
        public string Message;
        public bool TokenExpired;
    }

    /// <summary>Launches a profile's accounts one after another on a background thread.</summary>
    public class ProfileRunner
    {
        private readonly InstanceManager _manager;
        private readonly Func<RestrictResult> _evaluate;
        private readonly Func<RestrictResult, bool> _confirmWarn;
        private readonly Action<string> _progress;

        public ProfileRunner(InstanceManager manager, Func<RestrictResult> evaluate,
            Func<RestrictResult, bool> confirmWarn, Action<string> progress)
        {
            _manager = manager;
            _evaluate = evaluate;
            _confirmWarn = confirmWarn;
            _progress = progress;
        }

        /// <summary>Runs to completion (or cancel). Returns a one-line summary; per-account results go to the log.</summary>
        public string Run(LaunchProfile profile, List<Account> accounts, CancellationToken ct, out string problem)
        {
            problem = null;
            int total = accounts.Count;
            int ok = 0, failed = 0, skipped = 0;
            bool warned = false;
            bool cacheHandled = false;
            bool stopped = false;

            for (int i = 0; i < total; i++)
            {
                if (ct.IsCancellationRequested) { stopped = true; break; }
                Account a = accounts[i];
                string who = a.DisplayName;
                _progress("Launching " + (i + 1) + "/" + total + ": " + who + "...");

                if (_manager.IsAccountRunning(a.Id))
                {
                    skipped++;
                    _manager.WriteLog(who + ": skipped, already running");
                    continue;
                }

                RestrictResult r = _evaluate();
                if (r == null)
                {
                    problem = "Hardware is still being detected. Try again in a moment.";
                    _manager.WriteLog("Profile launch stopped: " + problem);
                    stopped = true;
                    break;
                }
                if (!r.CanLaunch)
                {
                    problem = r.Message + " Stopped before launching " + who + ".";
                    _manager.WriteLog("Profile launch stopped: " + problem);
                    stopped = true;
                    break;
                }
                if (r.Level == RestrictLevel.Warn && (!warned || r.RequiresConfirmation))
                {
                    warned = true;
                    if (!_confirmWarn(r))
                    {
                        _manager.WriteLog("Profile launch stopped at your request (warning declined)");
                        stopped = true;
                        break;
                    }
                }

                AccountLaunchResult res = _manager.LaunchAccount(a, profile.Name, profile.PlaceId, 60, ct, !cacheHandled);
                cacheHandled = true;
                switch (res.Outcome)
                {
                    case LaunchOutcome.Ok:
                        ok++;
                        _manager.WriteLog(who + ": ok");
                        break;
                    case LaunchOutcome.TicketFailed:
                        failed++;
                        _manager.WriteLog(who + ": ticket failed: " + res.Message);
                        if (res.TokenExpired) AccountStore.SetStatus(a.Id, "Expired", null);
                        break;
                    case LaunchOutcome.TimedOut:
                        failed++;
                        _manager.WriteLog(who + ": timed out - no new Roblox window within 60 seconds "
                            + "(if it appears later, use Adopt running clients)");
                        break;
                    case LaunchOutcome.Cancelled:
                        _manager.WriteLog(who + ": cancelled");
                        stopped = true;
                        break;
                    default:
                        failed++;
                        _manager.WriteLog(who + ": could not start Roblox: " + res.Message);
                        break;
                }
                if (stopped) break;

                bool more = i < total - 1;
                if (res.Outcome == LaunchOutcome.Ok && more && profile.StaggerSeconds > 0)
                {
                    for (int s = profile.StaggerSeconds; s > 0 && !ct.IsCancellationRequested; s--)
                    {
                        _progress("Launched " + (i + 1) + "/" + total + ". Next account in " + s + "s...");
                        ct.WaitHandle.WaitOne(1000);
                    }
                }
            }

            string summary = (stopped ? "Stopped. " : "Done. ") + ok + " launched, " + failed + " failed, " + skipped + " skipped.";
            _manager.WriteLog("Profile \"" + profile.Name + "\": " + summary);
            return summary;
        }
    }
}


