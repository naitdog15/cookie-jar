using System;
using System.Collections.Generic;
using CookieJar.Core;

namespace CookieJar.Tests
{
    public static class InstanceTests
    {
        private static RobloxInstance WithStatus(string status)
        {
            RobloxInstance i = new RobloxInstance();
            i.Status = status;
            return i;
        }

        public static void Run()
        {
            // ---- StatusKind per Status string (same mapping as src/RobloxInstance.cs)
            T.Check("StatusKind: null status is Stopped", WithStatus(null).StatusKind == "Stopped", WithStatus(null).StatusKind);
            T.Check("StatusKind: Starting is Starting", WithStatus("Starting").StatusKind == "Starting", WithStatus("Starting").StatusKind);
            T.Check("StatusKind: Awaiting login is Starting", WithStatus("Awaiting login").StatusKind == "Starting", WithStatus("Awaiting login").StatusKind);
            T.Check("StatusKind: Running is Running", WithStatus("Running").StatusKind == "Running", WithStatus("Running").StatusKind);
            T.Check("StatusKind: running in lower case is Running (case-insensitive)", WithStatus("running").StatusKind == "Running", "");
            T.Check("StatusKind: Stopped is Stopped", WithStatus("Stopped").StatusKind == "Stopped", WithStatus("Stopped").StatusKind);
            T.Check("StatusKind: Error: text is Error", WithStatus("Error: boom").StatusKind == "Error", WithStatus("Error: boom").StatusKind);
            T.Check("StatusKind: error in lower case is Error", WithStatus("error: x").StatusKind == "Error", "");
            T.Check("StatusKind: an unknown status falls back to Stopped", WithStatus("Something else").StatusKind == "Stopped", WithStatus("Something else").StatusKind);

            // ---- IsActive
            T.Check("IsActive: null is not active", !WithStatus(null).IsActive, "");
            T.Check("IsActive: Stopped is not active", !WithStatus("Stopped").IsActive, "");
            T.Check("IsActive: Starting is active", WithStatus("Starting").IsActive, "");
            T.Check("IsActive: Running is active", WithStatus("Running").IsActive, "");

            // ---- MemoryText
            RobloxInstance mem = new RobloxInstance();
            mem.MemoryMb = 512;
            T.Check("MemoryText: 512 MB", mem.MemoryText == "512 MB", mem.MemoryText);
            mem.MemoryMb = 1024;
            T.Check("MemoryText: 1024 MB is 1.0 GB", mem.MemoryText == "1.0 GB", mem.MemoryText);
            mem.MemoryMb = 1536;
            T.Check("MemoryText: 1536 MB is 1.5 GB", mem.MemoryText == "1.5 GB", mem.MemoryText);
            mem.MemoryMb = 1023.4;
            T.Check("MemoryText: just under 1 GB stays in MB", mem.MemoryText == "1023 MB", mem.MemoryText);

            // ---- ToString
            RobloxInstance named = new RobloxInstance();
            named.Name = "Instance 1";
            named.Pid = 4321;
            T.Check("ToString: name and pid", named.ToString() == "Instance 1 (PID 4321)", named.ToString());

            // ---- PropertyChanged raised only on real changes
            RobloxInstance watched = new RobloxInstance();
            List<string> raised = new List<string>();
            watched.PropertyChanged += delegate(object s, System.ComponentModel.PropertyChangedEventArgs e) { raised.Add(e.PropertyName); };
            watched.Status = "Running";
            T.Check("PropertyChanged: Status raises Status, StatusKind and IsActive",
                raised.Contains("Status") && raised.Contains("StatusKind") && raised.Contains("IsActive"), string.Join(",", raised.ToArray()));
            raised.Clear();
            watched.Status = "Running";
            T.Check("PropertyChanged: setting the same Status raises nothing", raised.Count == 0, string.Join(",", raised.ToArray()));
            raised.Clear();
            watched.MemoryMb = 300;
            T.Check("PropertyChanged: MemoryMb raises MemoryText", raised.Contains("MemoryText"), string.Join(",", raised.ToArray()));

            // ---- Uptime text boundaries (src/InstanceManager.cs Tick)
            T.Check("Uptime: 0 s", InstanceStatus.UptimeText(TimeSpan.Zero) == "0s", InstanceStatus.UptimeText(TimeSpan.Zero));
            T.Check("Uptime: 59 s stays in seconds", InstanceStatus.UptimeText(TimeSpan.FromSeconds(59)) == "59s", InstanceStatus.UptimeText(TimeSpan.FromSeconds(59)));
            T.Check("Uptime: 60 s is 1m 0s", InstanceStatus.UptimeText(TimeSpan.FromSeconds(60)) == "1m 0s", InstanceStatus.UptimeText(TimeSpan.FromSeconds(60)));
            T.Check("Uptime: 61 s is 1m 1s", InstanceStatus.UptimeText(TimeSpan.FromSeconds(61)) == "1m 1s", InstanceStatus.UptimeText(TimeSpan.FromSeconds(61)));
            T.Check("Uptime: 59 m 59 s stays in minutes", InstanceStatus.UptimeText(TimeSpan.FromSeconds(3599)) == "59m 59s", InstanceStatus.UptimeText(TimeSpan.FromSeconds(3599)));
            T.Check("Uptime: 3600 s is 1h 0m", InstanceStatus.UptimeText(TimeSpan.FromSeconds(3600)) == "1h 0m", InstanceStatus.UptimeText(TimeSpan.FromSeconds(3600)));
            T.Check("Uptime: 1 h 1 m 30 s is 1h 1m", InstanceStatus.UptimeText(TimeSpan.FromSeconds(3690)) == "1h 1m", InstanceStatus.UptimeText(TimeSpan.FromSeconds(3690)));
            T.Check("Uptime: 25 h keeps counting in hours (src has no days unit)", InstanceStatus.UptimeText(TimeSpan.FromHours(25)) == "25h 0m", InstanceStatus.UptimeText(TimeSpan.FromHours(25)));

            // ---- Status transitions with an injected clock
            DateTime now = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
            InstanceStatus status = new InstanceStatus(() => now);

            T.Check("Adopt: a launched client starts as Awaiting login", InstanceStatus.StatusAfterAdopt(false) == "Awaiting login", InstanceStatus.StatusAfterAdopt(false));
            T.Check("Adopt: an already running client starts as Running", InstanceStatus.StatusAfterAdopt(true) == "Running", InstanceStatus.StatusAfterAdopt(true));

            RobloxInstance fresh = new RobloxInstance();
            status.Adopt(fresh, false, now);
            T.Check("Adopt: sets status, uptime, start time and adopted flag",
                fresh.Status == "Awaiting login" && fresh.Uptime == "0s" && fresh.StartedUtc == now && !fresh.IsAdopted, fresh.Status + " / " + fresh.Uptime);

            T.Check("Promote: Starting 3 s old is not promoted yet", !status.ShouldPromoteToRunning("Starting", now.AddSeconds(-3)), "");
            T.Check("Promote: Starting 4 s old is promoted", status.ShouldPromoteToRunning("Starting", now.AddSeconds(-4)), "");
            T.Check("Promote: Awaiting login 10 s old is promoted", status.ShouldPromoteToRunning("Awaiting login", now.AddSeconds(-10)), "");
            T.Check("Promote: Running is not promoted again", !status.ShouldPromoteToRunning("Running", now.AddSeconds(-10)), "");

            RobloxInstance sampled = new RobloxInstance();
            sampled.Status = "Starting";
            sampled.StartedUtc = now.AddSeconds(-10);
            status.ApplySample(sampled, ProcState.Alive, 512L * 1024 * 1024);
            T.Check("Sample: alive instance gets memory, uptime and is promoted to Running",
                sampled.MemoryText == "512 MB" && sampled.Uptime == "10s" && sampled.Status == "Running",
                sampled.MemoryText + " / " + sampled.Uptime + " / " + sampled.Status);

            status.ApplySample(sampled, ProcState.Unknown, 1024L * 1024 * 1024);
            T.Check("Sample: an unknown probe reports 0 MB and keeps the status", sampled.MemoryMb == 0 && sampled.Status == "Running", sampled.MemoryMb.ToString());

            status.ApplySample(sampled, ProcState.Exited, 0);
            T.Check("Sample: an exited process is Stopped with ExitedUtc set",
                sampled.Status == "Stopped" && sampled.ExitedUtc == now && !sampled.IsActive, sampled.Status);

            RobloxInstance alreadyStopped = new RobloxInstance();
            alreadyStopped.Status = "Stopped";
            status.ApplySample(alreadyStopped, ProcState.Exited, 0);
            T.Check("Sample: an exited process already Stopped keeps ExitedUtc empty", alreadyStopped.ExitedUtc == null, "");

            RobloxInstance ended = new RobloxInstance();
            ended.Status = "Running";
            ended.StartedUtc = now.AddMinutes(-5);
            status.MarkStopped(ended);
            T.Check("MarkStopped: ended instance is Stopped now", ended.Status == "Stopped" && ended.ExitedUtc == now, ended.Status);

            // ---- active count (RunningCount in src/InstanceManager.cs)
            List<RobloxInstance> list = new List<RobloxInstance> { WithStatus("Running"), WithStatus("Stopped"), WithStatus(null), WithStatus("Starting") };
            T.Check("CountActive: counts Running and Starting, not Stopped or null", InstanceStatus.CountActive(list) == 2, InstanceStatus.CountActive(list).ToString());
        }
    }
}
