using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows.Threading;

namespace RbxMulti
{
    public class InstanceManager
    {

        private readonly Dispatcher _dispatcher;
        private readonly LaunchEnvironment _launch;
        private int _nextId = 1;
        private readonly object _syncLock = new object();
        private readonly object _launchLock = new object();

        public ObservableCollection<RobloxInstance> Instances { get; private set; }
        public event Action<string> Log;

        public InstanceManager()
            : this(new LaunchEnvironment())
        {
        }

        internal InstanceManager(LaunchEnvironment launch)
        {
            if (launch == null) throw new ArgumentNullException("launch");
            _launch = launch;
            Instances = new ObservableCollection<RobloxInstance>();
            _dispatcher = Dispatcher.CurrentDispatcher;
        }

        private const string WaitingMessage =
            "Waiting for Roblox to start - press Play on the Roblox website (watching for up to 90 seconds)...";
        private const int WatchSeconds = 90;
        private const int SettleSeconds = 15;

        internal const int InstallerWaitSeconds = 120;

        internal static string JoinPids(List<int> pids)
        {
            return string.Join(", ", pids.ConvertAll(delegate(int p) { return p.ToString(CultureInfo.InvariantCulture); }).ToArray());
        }

        internal static string InstallerRunningMessage(List<int> pids)
        {
            return "Roblox is installing an update (RobloxPlayerInstaller PID " + JoinPids(pids)
                + " is still running). Wait for it to finish or end it in Task Manager, then start the profile again.";
        }

        public bool IsRobloxInstallerRunning(out List<int> pids) { pids = SafeInstallers(); return pids.Count > 0; }

        private List<int> SafeInstallers()
        {
            List<int> l = null;
            try { l = _launch.GetInstallerPids(); } catch (Exception) { }
            return l ?? new List<int>();
        }

        private int NewInstaller(HashSet<int> before)
        {
            foreach (int i in SafeInstallers()) if (!before.Contains(i)) return i;
            return 0;
        }

        /// <summary>Returns true once no installer runs. On false, remaining holds the PIDs still running (empty when cancelled).</summary>
        private bool WaitForInstallers(CancellationToken ct, out List<int> remaining)
        {
            remaining = SafeInstallers();
            if (remaining.Count == 0) return true;
            LogMessage("Roblox installer running (PID " + JoinPids(remaining) + "); waiting");
            DateTime until = _launch.UtcNow.AddSeconds(InstallerWaitSeconds);
            while (remaining.Count > 0)
            {
                if (ct.IsCancellationRequested || _launch.UtcNow >= until) return false;
                _launch.Wait(ct);
                remaining = SafeInstallers();
            }
            LogMessage("Roblox installer finished; continuing the launch");
            return true;
        }

        private string UpdateStartedMessage(string exe)
        {
            List<string> newer = null;
            try { newer = _launch.NewerPlayersThan(exe); } catch (Exception) { }
            string tail = (newer != null && newer.Count > 0) ? " Newer installs found: " + string.Join("; ", newer.ToArray()) + "."
                : " No newer Roblox install was found on this PC, so Roblox has to download the update.";
            return "Roblox started an update instead of the game: the player Cookie Jar used (" + exe + ") is out of date. "
                + "Let the Roblox installer finish (do not launch again until it closes), then start the profile again." + tail;
        }

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
                    int trayEnded = _launch.EndIdleTrayClients(TrackedPids());
                    if (trayEnded > 0)
                    {
                        LogMessage("Ended " + trayEnded + " idle Roblox tray client(s) so the new launch gets its own process");
                    }
                    List<int> running = RobloxProcesses.GetPids();

                    // Remember every process that exists before launching, tracked or not.
                    HashSet<int> known = new HashSet<int>(running);
                    foreach (int tracked in TrackedPids())
                    {
                        known.Add(tracked);
                    }

                    if (!PrepareMultiInstance(out error)) return false;

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

        private bool PrepareMultiInstance(out string error)
        {
            int closed;
            bool ready = _launch.PrepareMultiInstance(out error, out closed);
            LogMessage("Closed " + closed + " singleton handles in existing Roblox processes");
            if (!ready)
            {
                LogMessage(error);
                return false;
            }
            return true;
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
        /// Launches one stored account straight into the installed player using its own ticket.
        /// Tracks the exact player immediately; waits for a stable window before reporting success.
        /// </summary>
        public AccountLaunchResult LaunchAccount(Account account, string profileName, long placeId, int timeoutSeconds, CancellationToken ct, bool mayClearCache = true)
        {
            AccountLaunchResult result = new AccountLaunchResult();
            lock (_launchLock)
            {
                Process starter = null;
                try
                {
                    if (ct.IsCancellationRequested)
                    {
                        result.Outcome = LaunchOutcome.Cancelled;
                        return result;
                    }
                    List<int> waitingFor;
                    if (!WaitForInstallers(ct, out waitingFor))
                    {
                        if (ct.IsCancellationRequested) { result.Outcome = LaunchOutcome.Cancelled; return result; }
                        LogMessage("Roblox installer still running after " + InstallerWaitSeconds + "s (PID " + JoinPids(waitingFor) + "); launch not started");
                        result.Outcome = LaunchOutcome.RobloxUpdating; result.Message = InstallerRunningMessage(waitingFor); return result;
                    }
                    List<int> protectedPids = TrackedPids();
                    if (_launch.EndIdleTrayClients(protectedPids) > 0)
                    {
                        LogMessage("Ended idle Roblox tray client(s) so the launch gets its own process");
                    }
                    HashSet<int> known = new HashSet<int>(_launch.GetPids());
                    protectedPids.RemoveAll(delegate(int existing) { return !known.Contains(existing); });

                    string guardError;
                    if (!PrepareMultiInstance(out guardError))
                    {
                        result.Outcome = LaunchOutcome.MultiInstanceFailed;
                        result.Message = guardError;
                        return result;
                    }

                    RobloxLocator.InvalidateCache();
                    string playerExe = _launch.FindPlayerExe();
                    if (string.IsNullOrEmpty(playerExe))
                    {
                        result.Outcome = LaunchOutcome.StartFailed;
                        result.Message = "RobloxPlayerBeta.exe was not found. Install or update Roblox, then retry.";
                        return result;
                    }
                    LogMessage("Using Roblox player " + playerExe);
                    if (mayClearCache && _launch.GetPids().Count == 0)
                    {
                        _launch.ClearCache();
                    }

                    string ticket;
                    bool expired;
                    string ticketError;
                    if (!_launch.GetTicket(account.Token, out ticket, out expired, out ticketError))
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
                    HashSet<int> installersBefore = new HashSet<int>();
                    try
                    {
                        ProcessStartInfo psi = TicketLauncher.BuildPlayerStartInfo(playerExe, uri);
                        installersBefore = new HashSet<int>(SafeInstallers());
                        starter = _launch.StartPlayer(psi);
                        if (starter == null) throw new InvalidOperationException();
                    }
                    catch (Exception)
                    {
                        result.Outcome = LaunchOutcome.StartFailed;
                        result.Message = "Windows could not start the installed Roblox player. Make sure no Roblox installer is running, then retry.";
                        return result;
                    }

                    // Register before any cancellation, timeout or previous-client failure can
                    // return. Those outcomes leave already-started games running and manageable.
                    RobloxInstance launched = Adopt(starter.Id, false, profileName, account.Username);
                    if (launched == null)
                    {
                        result.Outcome = LaunchOutcome.MultiInstanceFailed;
                        result.Message = "Roblox exited before it could be tracked. Make sure no Roblox installer is running, then retry.";
                        return result;
                    }
                    lock (_syncLock) { _accountPids[account.Id] = starter.Id; }

                    int pid = starter.Id;
                    DateTime started = _launch.UtcNow;
                    DateTime deadline = started.AddSeconds(timeoutSeconds);
                    DateTime visibleSince = DateTime.MinValue;
                    bool singletonClosed = false, singletonWarned = false;
                    while (_launch.UtcNow < deadline)
                    {
                        // Close the new client's singleton handles as soon as it creates them, so the
                        // next launch cannot find them and route to (and end) this or older games.
                        if (!singletonClosed)
                        {
                            int closedNow = 0;
                            try { closedNow = _launch.CloseSingletons(pid); } catch (Exception) { }
                            if (closedNow > 0)
                            {
                                singletonClosed = true;
                                LogMessage("Closed singleton handle of new client (PID " + pid + ")");
                            }
                            else if (!singletonWarned && (_launch.UtcNow - started).TotalSeconds >= 10)
                            {
                                singletonWarned = true;
                                LogMessage("Warning: no singleton handle found for PID " + pid + " within 10s");
                            }
                        }

                        if (ct.IsCancellationRequested)
                        {
                            result.Outcome = LaunchOutcome.Cancelled;
                            return result;
                        }

                        List<int> current = _launch.GetPids();
                        int lostPid = protectedPids.Find(delegate(int existingPid) { return !current.Contains(existingPid); });
                        int newInstaller = NewInstaller(installersBefore);
                        if (lostPid != 0)
                        {
                            if (newInstaller != 0)
                            {
                                LogMessage("Existing Roblox client PID " + lostPid + " exited while Roblox updated (RobloxPlayerInstaller PID " + newInstaller + ")");
                                result.Outcome = LaunchOutcome.RobloxUpdating;
                                result.Message = "The Roblox update (RobloxPlayerInstaller PID " + newInstaller + ") closed the existing client (PID " + lostPid + "). " + UpdateStartedMessage(playerExe);
                                return result;
                            }
                            LogMessage("Existing Roblox client PID " + lostPid + " exited while PID " + pid + " was launching");
                            result.Outcome = LaunchOutcome.MultiInstanceFailed;
                            result.Message = "An existing Roblox client (PID " + lostPid + ") closed while another account was launching. "
                                + "The profile was stopped to protect the remaining games. Export the log and retry.";
                            return result;
                        }
                        if (newInstaller != 0)
                        {
                            LogMessage("Roblox PID " + pid + " started an update (RobloxPlayerInstaller PID " + newInstaller + "); the selected player " + playerExe + " is outdated");
                            result.Outcome = LaunchOutcome.RobloxUpdating; result.Message = UpdateStartedMessage(playerExe); return result;
                        }
                        if (StarterGone(starter, pid, current))
                        {
                            // The started player may hand off to another RobloxPlayerBeta process.
                            // Follow exactly one new, unknown player instead of failing the launch.
                            List<int> tracked = TrackedPids();
                            int handoff = 0, newCount = 0;
                            foreach (int candidate in current)
                            {
                                if (candidate == pid || known.Contains(candidate) || tracked.Contains(candidate)) continue;
                                handoff = candidate;
                                newCount++;
                            }
                            if (newCount == 1)
                            {
                                LogMessage("Roblox PID " + pid + " handed off to PID " + handoff + "; tracking the new process");
                                known.Add(pid);
                                pid = handoff;
                                RobloxInstance followed = Adopt(pid, false, profileName, account.Username);
                                if (followed != null) launched = followed;
                                lock (_syncLock) { _accountPids[account.Id] = pid; }
                                singletonClosed = false;
                                singletonWarned = false;
                                started = _launch.UtcNow;
                                visibleSince = DateTime.MinValue;
                                _launch.Wait(ct);
                                continue;
                            }
                            LogMessage("Roblox PID " + pid + " exited before its account window was ready");
                            result.Outcome = LaunchOutcome.MultiInstanceFailed;
                            result.Message = "Roblox exited before its account window was ready. Export the log and retry the profile.";
                            return result;
                        }

                        // Only adopt the player we started for this ticket (or its single hand-off),
                        // never an unrelated client that happens to appear during the same window.
                        if (!_launch.HasWindow(pid))
                        {
                            visibleSince = DateTime.MinValue;
                        }
                        else if (visibleSince == DateTime.MinValue)
                        {
                            visibleSince = _launch.UtcNow;
                        }
                        else if ((_launch.UtcNow - visibleSince).TotalSeconds >= 2)
                        {
                            OnUi(() => launched.Status = "Running");
                            LogMessage("New account client ready (PID " + pid + (singletonClosed ? ", singleton closed" : ", no singleton handle seen") + ")");
                            result.Outcome = LaunchOutcome.Ok;
                            return result;
                        }

                        _launch.Wait(ct);
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
                finally
                {
                    if (starter != null) starter.Dispose();
                }
            }
        }

        private static bool StarterGone(Process starter, int pid, List<int> current)
        {
            if (!current.Contains(pid)) return true;
            if (starter.Id != pid) return false;
            try { return starter.HasExited; } catch (Exception) { return false; }
        }

        /// <summary>Adds visible already-running games, including minimised ones. Hidden tray clients are not games.</summary>
        public int AdoptRunning()
        {
            HashSet<int> tracked = new HashSet<int>(TrackedPids());
            HashSet<int> visible = _launch.VisiblePids();
            int added = 0;
            foreach (int pid in _launch.GetPids())
            {
                if (tracked.Contains(pid) || !visible.Contains(pid))
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

            RobloxInstance instance = null;
            bool added = false;
            OnUi(() =>
            {
                lock (_syncLock)
                {
                    instance = Instances.FirstOrDefault(i => i.Pid == pid && i.IsActive && i.StartedUtc == started);
                    if (instance == null)
                    {
                        instance = new RobloxInstance();
                        instance.Id = _nextId++;
                        instance.Name = string.IsNullOrEmpty(accountName) ? "Instance " + instance.Id : accountName;
                        instance.Pid = pid;
                        instance.StartedUtc = started;
                        instance.IsAdopted = adopted;
                        instance.Status = adopted ? "Running" : "Awaiting login";
                        instance.Uptime = "0s";
                        instance.AccountName = accountName;
                        instance.ProfileName = profile;
                        Instances.Add(instance);
                        added = true;
                    }
                    if (!string.IsNullOrEmpty(accountName))
                    {
                        instance.AccountName = accountName;
                        instance.Name = accountName;
                        instance.IsAdopted = false;
                    }
                    if (profile != null) instance.ProfileName = profile;
                }
            });
            if (added) LogMessage("Instance " + instance.Id + " added (PID " + pid + (adopted ? ", adopted" : "") + ")");
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
            HashSet<int> singletonsClosed = new HashSet<int>();

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
                        if (!singletonsClosed.Contains(pid) && _launch.CloseSingletons(pid) > 0)
                        {
                            singletonsClosed.Add(pid);
                            LogMessage("Closed singleton handle of new client (PID " + pid + ")");
                        }
                        if (WindowManager.FindWindowForPid(pid, 0) == IntPtr.Zero) continue;
                        known.Add(pid);
                        if (Adopt(pid, false, profile) != null)
                        {
                            adoptedCount++;
                            lastAdopt = DateTime.UtcNow;
                        }
                    }

                    if (adoptedCount > 0
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

        /// <summary>Running RobloxPlayerInstaller PIDs. Read-only: never ends a process.</summary>
        public static List<int> GetInstallerPids()
        {
            List<int> pids = new List<int>();
            try { foreach (Process p in Process.GetProcessesByName("RobloxPlayerInstaller")) { try { pids.Add(p.Id); } catch { } finally { p.Dispose(); } } } catch { }
            return pids;
        }

        public static bool AnyRunning()
        {
            return GetPids().Count > 0;
        }

        private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumProc proc, IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hwnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

        internal static HashSet<int> PidsWithVisibleWindow()
        {
            HashSet<int> visible = new HashSet<int>();
            EnumWindows(delegate(IntPtr hwnd, IntPtr lParam)
            {
                uint pid;
                GetWindowThreadProcessId(hwnd, out pid);
                if (pid != 0 && IsWindowVisible(hwnd))
                {
                    visible.Add((int)pid);
                }
                return true;
            }, IntPtr.Zero);
            return visible;
        }

        /// <summary>
        /// Recent Roblox builds keep a hidden "tray" client alive (autostart with --launch-to-tray, and a
        /// client whose window was closed stays resident). A new roblox-player launch is handed to that
        /// resident process instead of starting a second client, so only one game ever runs. Only
        /// positively identified --launch-to-tray clients without a visible window are ended.
        /// Age alone is not evidence of an idle tray: a hidden/slow game must not be killed.
        /// Tracked games are protected even while hidden or loading. Never throws.
        /// </summary>
        public static int EndIdleTrayClients(IEnumerable<int> protectedPids)
        {
            int ended = 0;
            try
            {
                HashSet<int> visible = PidsWithVisibleWindow();
                HashSet<int> protectedSet = new HashSet<int>(protectedPids);
                Dictionary<int, string> commandLines = new Dictionary<int, string>();
                try
                {
                    using (System.Management.ManagementObjectSearcher searcher = new System.Management.ManagementObjectSearcher(
                        "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'RobloxPlayerBeta.exe'"))
                    using (System.Management.ManagementObjectCollection rows = searcher.Get())
                    {
                        foreach (System.Management.ManagementBaseObject row in rows)
                        {
                            commandLines[Convert.ToInt32(row["ProcessId"])] = row["CommandLine"] as string;
                            row.Dispose();
                        }
                    }
                }
                catch
                {
                }

                foreach (Process p in Process.GetProcessesByName("RobloxPlayerBeta"))
                {
                    try
                    {
                        string cmd;
                        commandLines.TryGetValue(p.Id, out cmd);
                        if (IsIdleTrayClient(visible.Contains(p.Id), protectedSet.Contains(p.Id), cmd)
                            && !PidsWithVisibleWindow().Contains(p.Id))
                        {
                            p.Kill();
                            ended++;
                        }
                    }
                    catch
                    {
                    }
                    finally
                    {
                        p.Dispose();
                    }
                }
            }
            catch
            {
            }
            return ended;
        }

        internal static bool IsIdleTrayClient(bool visible, bool trackedGame, string commandLine)
        {
            if (visible || trackedGame || string.IsNullOrEmpty(commandLine)) return false;
            foreach (string argument in commandLine.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (string.Equals(argument.Trim('"'), "--launch-to-tray", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }
}


