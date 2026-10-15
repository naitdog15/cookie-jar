using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows.Threading;

namespace RbxMulti
{
    public class InstanceManager
    {

        private readonly Dispatcher _dispatcher;
        private int _nextId = 1;
        private readonly object _syncLock = new object();
        private readonly object _launchLock = new object();

        public ObservableCollection<RobloxInstance> Instances { get; private set; }
        public event Action<string> Log;

        public InstanceManager()
        {
            Instances = new ObservableCollection<RobloxInstance>();
            _dispatcher = Dispatcher.CurrentDispatcher;
        }

        private const string WaitingMessage =
            "Waiting for Roblox to start - press Play on the Roblox website (watching for up to 90 seconds)...";
        private const int WatchSeconds = 90;
        private const int SettleSeconds = 15;

        private int _watchGeneration;
        private volatile string _waitingText;

        /// <summary>Non-null while the app is watching for a new Roblox client to appear.</summary>
        public string WaitingText { get { return _waitingText; } }

        private readonly Dictionary<string, int> _accountPids = new Dictionary<string, int>();

        /// <summary>Writes a line to the activity log (any thread). Never pass tokens or tickets.</summary>
        public void WriteLog(string message)
        {
            LogMessage(message);
        }

        /// <summary>
        /// Opens the Adopt Me page in the default browser (website login path). New RobloxPlayerBeta
        /// processes are detected in the background and adopted as tracked instances.
        /// Login data is wiped only when wipeLogin is true AND no RobloxPlayerBeta process runs.
        /// </summary>
        public bool Launch(bool wipeLogin, out string error)
        {
            error = null;

            lock (_launchLock)
            {
                try
                {
                    List<int> running = RobloxProcesses.GetPids();

                    // Remember every process that exists before launching, tracked or not.
                    HashSet<int> known = new HashSet<int>(running);
                    foreach (int tracked in TrackedPids())
                    {
                        known.Add(tracked);
                    }

                    int closedCount = HandleCloser.CloseSingletonsInAllRoblox();
                    LogMessage("Closed " + closedCount + " singleton handles in existing Roblox processes");

                    if (running.Count == 0)
                    {
                        string[] clearedPaths = CacheCleaner.ClearCache();
                        LogMessage("Cleared cache folders (" + clearedPaths.Length + " paths deleted). Login data was not touched.");
                    }
                    else
                    {
                        LogMessage("Cache not cleared: Roblox clients are already running.");
                    }

                    if (wipeLogin)
                    {
                        if (RobloxProcesses.AnyRunning())
                        {
                            LogMessage("Force fresh login ignored: a Roblox client is running, so its login was kept.");
                        }
                        else
                        {
                            CacheCleaner.ClearLoginState();
                            LogMessage("Wiped saved Roblox login (Force fresh login was selected, no Roblox client was running)");
                        }
                    }

                    GameInfo game = Games.Current;
                    try
                    {
                        ProcessStartInfo psi = new ProcessStartInfo(game.Url);
                        psi.UseShellExecute = true;
                        Process web = Process.Start(psi);
                        if (web != null)
                        {
                            web.Dispose();
                        }
                    }
                    catch (Exception ex)
                    {
                        error = "Could not open the Roblox website: " + ex.Message;
                        return false;
                    }

                    LogMessage("Opened the " + game.Name + " page (place " + game.PlaceId + ") in your browser. Press Play; the new client will be detected and added here.");
                    StartWatch(known, null);
                    return true;
                }
                catch (Exception ex)
                {
                    error = "Exception: " + ex.Message;
                    return false;
                }
            }
        }

        /// <summary>True when a client launched for this stored account is still running.</summary>
        public bool IsAccountRunning(string accountId)
        {
            int pid;
            lock (_syncLock)
            {
                if (!_accountPids.TryGetValue(accountId, out pid))
                {
                    return false;
                }
            }
            if (!TrackedPids().Contains(pid))
            {
                return false;
            }
            Process p = TryGetProcess(pid);
            if (p == null)
            {
                return false;
            }
            p.Dispose();
            return true;
        }

        /// <summary>
        /// Launches one stored account straight into the place: ticket, roblox-player URI, then waits
        /// (up to timeoutSeconds) for the new client, adopts it and closes its singleton handle.
        /// </summary>
        public AccountLaunchResult LaunchAccount(Account account, string profileName, long placeId, int timeoutSeconds, CancellationToken ct, bool mayClearCache = true)
        {
            AccountLaunchResult result = new AccountLaunchResult();
            lock (_launchLock)
            {
                try
                {
                    HashSet<int> known = new HashSet<int>(RobloxProcesses.GetPids());
                    foreach (int tracked in TrackedPids())
                    {
                        known.Add(tracked);
                    }

                    HandleCloser.CloseSingletonsInAllRoblox();
                    if (mayClearCache && !RobloxProcesses.AnyRunning())
                    {
                        CacheCleaner.ClearCache();
                    }

                    string ticket;
                    bool expired;
                    string ticketError;
                    if (!TicketLauncher.GetTicket(account.Token, out ticket, out expired, out ticketError))
                    {
                        result.Outcome = LaunchOutcome.TicketFailed;
                        result.Message = ticketError;
                        result.TokenExpired = expired;
                        return result;
                    }

                    if (ct.IsCancellationRequested)
                    {
                        result.Outcome = LaunchOutcome.Cancelled;
                        return result;
                    }

                    string uri = TicketLauncher.BuildLaunchUri(ticket, placeId,
                        TicketLauncher.NewBrowserTrackerId(), TicketLauncher.UnixMs(DateTime.UtcNow));
                    try
                    {
                        ProcessStartInfo psi = new ProcessStartInfo(uri);
                        psi.UseShellExecute = true;
                        Process starter = Process.Start(psi);
                        if (starter != null)
                        {
                            starter.Dispose();
                        }
                    }
                    catch (Exception)
                    {
                        result.Outcome = LaunchOutcome.StartFailed;
                        result.Message = "Windows could not open the roblox-player link. Is Roblox installed?";
                        return result;
                    }

                    DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
                    int adoptedPid = 0;
                    DateTime adoptedAt = DateTime.MinValue;
                    while (DateTime.UtcNow < deadline)
                    {
                        if (ct.IsCancellationRequested)
                        {
                            result.Outcome = LaunchOutcome.Cancelled;
                            return result;
                        }

                        if (adoptedPid == 0)
                        {
                            foreach (int pid in RobloxProcesses.GetPids())
                            {
                                if (known.Contains(pid))
                                {
                                    continue;
                                }
                                known.Add(pid);
                                RobloxInstance inst = Adopt(pid, false, profileName, account.Username);
                                if (inst != null && adoptedPid == 0)
                                {
                                    adoptedPid = pid;
                                    adoptedAt = DateTime.UtcNow;
                                    lock (_syncLock)
                                    {
                                        _accountPids[account.Id] = pid;
                                    }
                                }
                            }
                        }

                        if (adoptedPid != 0)
                        {
                            bool closed = false;
                            try
                            {
                                closed = HandleCloser.CloseSingletons(adoptedPid) > 0;
                            }
                            catch
                            {
                            }
                            if (closed)
                            {
                                LogMessage("Closed singleton handle of new client (PID " + adoptedPid + ")");
                                result.Outcome = LaunchOutcome.Ok;
                                return result;
                            }
                            if ((DateTime.UtcNow - adoptedAt).TotalSeconds > 10)
                            {
                                LogMessage("Warning: no singleton handle found for PID " + adoptedPid + " within 10s");
                                result.Outcome = LaunchOutcome.Ok;
                                return result;
                            }
                        }

                        Thread.Sleep(250);
                    }

                    result.Outcome = LaunchOutcome.TimedOut;
                    return result;
                }
                catch (Exception ex)
                {
                    result.Outcome = LaunchOutcome.StartFailed;
                    result.Message = ex.GetType().Name;
                    return result;
                }
            }
        }

        /// <summary>Adds every already-running RobloxPlayerBeta process that is not tracked yet. Returns how many were added.</summary>
        public int AdoptRunning()
        {
            HashSet<int> tracked = new HashSet<int>(TrackedPids());
            int added = 0;
            foreach (int pid in RobloxProcesses.GetPids())
            {
                if (tracked.Contains(pid))
                {
                    continue;
                }
                if (Adopt(pid, true, null) != null)
                {
                    added++;
                }
            }
            if (added > 0)
            {
                LogMessage("Adopted " + added + " running Roblox client(s)");
            }
            return added;
        }

        private List<int> TrackedPids()
        {
            List<int> pids = new List<int>();
            OnUi(() =>
            {
                lock (_syncLock)
                {
                    foreach (RobloxInstance i in Instances)
                    {
                        if (i.IsActive)
                        {
                            pids.Add(i.Pid);
                        }
                    }
                }
            });
            return pids;
        }

        private RobloxInstance Adopt(int pid, bool adopted, string profile)
        {
            return Adopt(pid, adopted, profile, null);
        }

        private RobloxInstance Adopt(int pid, bool adopted, string profile, string accountName)
        {
            DateTime started = DateTime.UtcNow;
            try
            {
                using (Process p = Process.GetProcessById(pid))
                {
                    if (p.HasExited)
                    {
                        return null;
                    }
                    started = p.StartTime.ToUniversalTime();
                }
            }
            catch
            {
                return null;
            }

            RobloxInstance instance = new RobloxInstance();
            lock (_syncLock)
            {
                instance.Id = _nextId++;
            }
            instance.Name = string.IsNullOrEmpty(accountName) ? "Instance " + instance.Id : accountName;
            instance.AccountName = accountName;
            instance.Pid = pid;
            instance.StartedUtc = started;
            instance.IsAdopted = adopted;
            instance.Status = adopted ? "Running" : "Awaiting login";
            instance.Uptime = "0s";
            instance.ProfileName = profile;

            OnUi(() => Instances.Add(instance));
            LogMessage("Instance " + instance.Id + " added (PID " + pid + (adopted ? ", adopted" : "") + ")");
            return instance;
        }

        private void StartWatch(HashSet<int> known, string profile)
        {
            int gen = Interlocked.Increment(ref _watchGeneration);
            _waitingText = WaitingMessage;
            ThreadPool.QueueUserWorkItem(delegate(object state) { WatchLoop(gen, known, profile); });
        }

        private void WatchLoop(int gen, HashSet<int> known, string profile)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(WatchSeconds);
            DateTime lastAdopt = DateTime.MinValue;
            int adoptedCount = 0;
            Dictionary<int, DateTime> pending = new Dictionary<int, DateTime>();

            try
            {
                while (gen == _watchGeneration && DateTime.UtcNow < deadline)
                {
                    foreach (int pid in RobloxProcesses.GetPids())
                    {
                        if (known.Contains(pid))
                        {
                            continue;
                        }
                        known.Add(pid);
                        if (Adopt(pid, false, profile) != null)
                        {
                            adoptedCount++;
                            lastAdopt = DateTime.UtcNow;
                            pending[pid] = DateTime.UtcNow;
                        }
                    }

                    foreach (int pid in new List<int>(pending.Keys))
                    {
                        bool done = false;
                        try
                        {
                            if (HandleCloser.CloseSingletons(pid) > 0)
                            {
                                LogMessage("Closed singleton handle of new client (PID " + pid + ")");
                                done = true;
                            }
                        }
                        catch
                        {
                        }
                        if (!done && (DateTime.UtcNow - pending[pid]).TotalSeconds > 10)
                        {
                            LogMessage("Warning: no singleton handle found for PID " + pid + " within 10s");
                            done = true;
                        }
                        if (done)
                        {
                            pending.Remove(pid);
                        }
                    }

                    if (adoptedCount > 0 && pending.Count == 0
                        && (DateTime.UtcNow - lastAdopt).TotalSeconds > SettleSeconds)
                    {
                        break;
                    }

                    Thread.Sleep(250);
                }
            }
            catch (Exception ex)
            {
                LogMessage("Watcher error: " + ex.Message);
            }
            finally
            {
                if (gen == _watchGeneration)
                {
                    _waitingText = null;
                    if (adoptedCount == 0)
                    {
                        LogMessage("No new Roblox client appeared within " + WatchSeconds + " seconds.");
                    }
                }
            }
        }

        /// <summary>
        /// Ends the instance immediately: kills its whole process tree (like Process Explorer > Kill Process Tree).
        /// No CloseMainWindow, so Roblox never shows its in-game Leave prompt, and no waiting.
        /// </summary>
        public void Stop(RobloxInstance instance)
        {
            if (instance == null) return;

            try
            {
                int killed = ProcessTree.KillTree(instance.Pid);
                LogMessage("Ended " + instance.Name + " (PID " + instance.Pid + "), process tree killed (" + killed + " processes)");
                MarkStopped(instance);
            }
            catch (Exception ex)
            {
                SetError(instance, ex.Message);
                LogMessage("Error ending instance " + instance.Id + ": " + ex.Message);
            }
        }

        /// <summary>Kept for older callers; identical to Stop.</summary>
        public void KillTree(RobloxInstance instance)
        {
            Stop(instance);
        }

        /// <summary>Ends every active instance immediately with a process-tree kill.</summary>
        public void StopAll()
        {
            List<RobloxInstance> active = new List<RobloxInstance>();
            lock (_syncLock)
            {
                active.AddRange(Instances.Where(i => i.IsActive));
            }

            foreach (RobloxInstance instance in active)
            {
                Stop(instance);
            }

            LogMessage("Stop all: ended " + active.Count + " instance(s)");
        }

        public int RunningCount
        {
            get
            {
                int count = 0;
                OnUi(() =>
                {
                    lock (_syncLock)
                    {
                        count = Instances.Count(i => i.Status != "Stopped" && i.Status != null);
                    }
                });
                return count;
            }
        }

        public void Tick()
        {
            List<RobloxInstance> instancesToUpdate = new List<RobloxInstance>();
            lock (_syncLock)
            {
                instancesToUpdate.AddRange(Instances);
            }

            foreach (RobloxInstance instance in instancesToUpdate)
            {
                try
                {
                    long wsBytes;
                    MemoryGovernor.ProcState state = MemoryGovernor.QueryWorkingSet(instance.Pid, out wsBytes);
                    if (state == MemoryGovernor.ProcState.Exited)
                    {
                        if (instance.Status != "Stopped")
                        {
                            instance.ExitedUtc = DateTime.UtcNow;
                            instance.Status = "Stopped";
                        }
                        continue;
                    }

                    {
                        // Update memory
                        instance.MemoryMb = state == MemoryGovernor.ProcState.Alive ? wsBytes / (1024.0 * 1024.0) : 0;

                        // Update uptime
                        TimeSpan uptime = DateTime.UtcNow - instance.StartedUtc;
                        if (uptime.TotalHours >= 1)
                        {
                            instance.Uptime = ((int)uptime.TotalHours) + "h " + uptime.Minutes + "m";
                        }
                        else if (uptime.TotalMinutes >= 1)
                        {
                            instance.Uptime = ((int)uptime.TotalMinutes) + "m " + uptime.Seconds + "s";
                        }
                        else
                        {
                            instance.Uptime = uptime.Seconds + "s";
                        }

                        // Update status if still starting
                        if (instance.Status == "Starting" || instance.Status == "Awaiting login")
                        {
                            // After ~3 seconds, assume it's running
                            if ((DateTime.UtcNow - instance.StartedUtc).TotalSeconds > 3)
                            {
                                instance.Status = "Running";
                            }
                        }
                    }
                }
                catch
                {
                    // Silently handle any errors in refresh
                }
            }
        }

        /// <summary>Runs an action on the UI dispatcher, inline when already on it.</summary>
        private void OnUi(Action action)
        {
            if (_dispatcher.CheckAccess())
            {
                action();
            }
            else
            {
                _dispatcher.Invoke(action);
            }
        }

        private void MarkStopped(RobloxInstance instance)
        {
            OnUi(() =>
            {
                instance.ExitedUtc = DateTime.UtcNow;
                instance.Status = "Stopped";
            });
        }

        private void SetError(RobloxInstance instance, string message)
        {
            OnUi(() => instance.LastError = message);
        }

        /// <summary>Returns the live process for a pid, or null if it is gone or has exited.</summary>
        private static Process TryGetProcess(int pid)
        {
            try
            {
                Process proc = Process.GetProcessById(pid);
                if (proc.HasExited)
                {
                    proc.Dispose();
                    return null;
                }
                return proc;
            }
            catch
            {
                return null;
            }
        }
        private void LogMessage(string message)
        {
            if (Log != null)
            {
                OnUi(() => Log(DateTime.Now.ToString("HH:mm:ss") + " - " + message));
            }
        }
    }
}

namespace RbxMulti
{
    /// <summary>Real system RobloxPlayerBeta processes, tracked or not.</summary>
    public static class RobloxProcesses
    {
        public static List<int> GetPids()
        {
            List<int> pids = new List<int>();
            try
            {
                foreach (Process p in Process.GetProcessesByName("RobloxPlayerBeta"))
                {
                    try { pids.Add(p.Id); } catch { }
                    p.Dispose();
                }
            }
            catch
            {
            }
            return pids;
        }

        public static bool AnyRunning()
        {
            return GetPids().Count > 0;
        }
    }
}


