using System;
using System.Collections.Generic;

namespace CookieJar.Core
{
    /// <summary>What a process probe found. Exited means the process is gone.</summary>
    public enum ProcState { Exited, Unknown, Alive }

    /// <summary>
    /// Status, uptime and adopt/exit transitions of a Roblox instance, without any process calls. The caller
    /// samples the process and passes the result in; the clock is injectable so tests control time.
    /// </summary>
    public sealed class InstanceStatus
    {
        public const string StartingText = "Starting";
        public const string AwaitingLoginText = "Awaiting login";
        public const string RunningText = "Running";
        public const string StoppedText = "Stopped";

        /// <summary>A launched or adopted client still in a starting status is promoted to Running after this many seconds.</summary>
        public const int PromoteAfterSeconds = 3;

        private readonly Func<DateTime> _utcNow;

        public InstanceStatus() : this(null) { }

        public InstanceStatus(Func<DateTime> utcNow)
        {
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        public DateTime UtcNow { get { return _utcNow(); } }

        /// <summary>Uptime as shown in the list: "42s", "3m 5s" or "2h 10m".</summary>
        public static string UptimeText(TimeSpan uptime)
        {
            if (uptime.TotalHours >= 1)
            {
                return ((int)uptime.TotalHours) + "h " + uptime.Minutes + "m";
            }
            if (uptime.TotalMinutes >= 1)
            {
                return ((int)uptime.TotalMinutes) + "m " + uptime.Seconds + "s";
            }
            return uptime.Seconds + "s";
        }

        /// <summary>Status given to an instance the moment it is adopted: Running if it was already running, else Awaiting login.</summary>
        public static string StatusAfterAdopt(bool adopted)
        {
            return adopted ? RunningText : AwaitingLoginText;
        }

        public static bool IsStartingStatus(string status)
        {
            return status == StartingText || status == AwaitingLoginText;
        }

        /// <summary>True when a starting instance has been alive long enough to count as Running.</summary>
        public bool ShouldPromoteToRunning(string status, DateTime startedUtc)
        {
            return IsStartingStatus(status) && (UtcNow - startedUtc).TotalSeconds > PromoteAfterSeconds;
        }

        /// <summary>Number of instances that count as active (Status set and not Stopped).</summary>
        public static int CountActive(IEnumerable<RobloxInstance> instances)
        {
            int n = 0;
            foreach (RobloxInstance i in instances)
            {
                if (i.IsActive) n++;
            }
            return n;
        }

        /// <summary>Puts a newly adopted or launched instance into its first status.</summary>
        public void Adopt(RobloxInstance instance, bool adopted, DateTime startedUtc)
        {
            instance.StartedUtc = startedUtc;
            instance.IsAdopted = adopted;
            instance.Status = StatusAfterAdopt(adopted);
            instance.Uptime = UptimeText(TimeSpan.Zero);
        }

        /// <summary>Marks the instance stopped now (the process was ended by us).</summary>
        public void MarkStopped(RobloxInstance instance)
        {
            instance.ExitedUtc = UtcNow;
            instance.Status = StoppedText;
        }

        /// <summary>Applies one sample from the periodic refresh. Exited instances are stopped; others get memory, uptime and promotion.</summary>
        public void ApplySample(RobloxInstance instance, ProcState state, long workingSetBytes)
        {
            if (state == ProcState.Exited)
            {
                if (instance.Status != StoppedText)
                {
                    instance.ExitedUtc = UtcNow;
                    instance.Status = StoppedText;
                }
                return;
            }

            instance.MemoryMb = state == ProcState.Alive ? workingSetBytes / (1024.0 * 1024.0) : 0;
            instance.Uptime = UptimeText(UtcNow - instance.StartedUtc);
            if (ShouldPromoteToRunning(instance.Status, instance.StartedUtc))
            {
                instance.Status = RunningText;
            }
        }
    }
}
