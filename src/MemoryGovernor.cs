// MemoryGovernor - per-instance working-set caps for Roblox client processes.
//
// HONEST SUMMARY
// This TRIMS the process working set via SetProcessWorkingSetSizeEx with a MAX cap
// (QUOTA_LIMITS_HARDWS_MAX_ENABLE). Pages beyond the cap are paged out under memory
// pressure, so performance may drop (stutter, disk paging). It is NOT a hard memory
// limit and does NOT crash or kill the client. Roblox may reset the cap, so it is
// re-applied every ~5 seconds (ReapplySeconds). No memory reading/writing or
// injection is used; only Win32 window-independent process APIs are called.
//
// PUBLIC API
//
//   enum MemoryPreset { Unlimited = 0, Low = 1, Medium = 2, High = 3, Custom = 4 }
//
//   class MemoryAllocation
//     int          Pid        - target process id
//     MemoryPreset Preset     - preset or Custom
//     int          CustomMb   - used only when Preset == Custom
//     string       Label      - optional display name used in warnings (null = "PID n")
//
//   static class MemoryGovernor
//     const int MinSafeMb = 800;         // caps below this are flagged as likely to stutter
//     const int ReapplySeconds = 5;      // timer period for re-applying caps
//
//     void Initialize(HardwareProfile hw);
//         Optional. Stores hw for budget math. null leaves the lazy path in place
//         (HardwareProfile.Detect() runs on first use inside try/catch). Idempotent.
//
//     void SetInstanceCountHint(int count);
//         Number of instances used by SuggestedMb scaling. Clamped to 1..16.
//
//     int SuggestedMb(MemoryPreset preset);
//         Unlimited -> 0, Custom -> 0. Base values: High 3584, Medium 2048, Low 1200 MB.
//         After the 64 MB rounding the unscaled results are High 3584, Medium 2048 and
//         Low 1216 MB (1200 rounds up to 1216).
//         Budget = max(2048, (RamGb - OsReserveGb) * 1024). If count * base > budget the
//         value is scaled by budget / (count * base), never below 600 MB. Result is
//         rounded to the nearest 64 MB. Never throws.
//
//     int ResolveMb(MemoryPreset preset, int customMb);
//         Custom -> customMb clamped to [400, 65536]. Other presets -> SuggestedMb.
//         0 means no cap.
//
//     bool Apply(int pid, MemoryPreset preset, int customMb);
//         Registers pid (replacing any previous entry), applies the cap immediately and
//         ensures the re-apply timer runs. Unlimited behaves as Clear. Returns true when
//         the cap (or the clear) was applied successfully.
//
//     void Clear(int pid);
//         Removes the entry and lifts the max cap (failures ignored). Stops the timer
//         when no entries remain.
//
//     void ClearAll();
//         Clear for every registered pid.
//
//     long GetWorkingSetMb(int pid);
//         Cached working set in MB (refreshed by the timer and on Apply). If not cached,
//         performs a one-off read. Returns -1 when the process is gone. Never throws.
//
//     int GetCapMb(int pid);
//         Currently applied cap in MB, or 0 when none.
//
//     List<string> ValidateAllocation(IList<MemoryAllocation> items);
//         Human-readable warnings (empty list = fine). Checks: (1) sum of resolved caps
//         (Unlimited counted as SuggestedMb(High)) exceeds free RAM; (2) sum exceeds the
//         safe budget (RamGb - OsReserveGb); (3) any cap > 0 and < MinSafeMb.
//         null or empty input returns an empty list.
//
// NATIVE CALLS: OpenProcess, SetProcessWorkingSetSizeEx, CloseHandle (kernel32).
// All public methods catch their own exceptions and never throw.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;

namespace RbxMulti
{
	public enum MemoryPreset
	{
		Unlimited = 0,
		Low = 1,
		Medium = 2,
		High = 3,
		Custom = 4
	}

	public class MemoryAllocation
	{
		public int Pid { get; set; }
		public MemoryPreset Preset { get; set; }
		public int CustomMb { get; set; }
		// Optional display name for warnings.
		public string Label { get; set; }
	}

	public static class MemoryGovernor
	{
		public const int MinSafeMb = 800;
		public const int ReapplySeconds = 5;

		private const int CustomMinMb = 400;
		private const int CustomMaxMb = 65536;
		private const int MinWorkingSetMb = 50;
		private const int ScaledFloorMb = 600;
		private const int RoundStepMb = 64;
		private const int BudgetFloorMb = 2048;
		private const int CountMin = 1;
		private const int CountMax = 16;
		private const int FallbackRamGb = 4;

		// kernel32 access and flag values.
		private const uint PROCESS_SET_QUOTA = 0x0100;
		private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
		private const uint QUOTA_LIMITS_HARDWS_MIN_DISABLE = 0x2;
		private const uint QUOTA_LIMITS_HARDWS_MAX_ENABLE = 0x4;
		// Documented as QUOTA_LIMITS_HARDWS_MAX_DISABLE (0x8) in the Windows SDK.
		private const uint QUOTA_LIMITS_HARDWS_MAX_DISABLE = 0x8;

		// Single lock guarding every piece of shared state below.
		private static readonly object Lock = new object();
		private static readonly Dictionary<int, int> CapByPid = new Dictionary<int, int>();
		private static readonly Dictionary<int, long> WsByPid = new Dictionary<int, long>();
		private static Timer timer;
		private static HardwareProfile hardware;
		private static int countHint = 1;
		private static int tickBusy;

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

		[DllImport("kernel32.dll", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool CloseHandle(IntPtr handle);

		[DllImport("kernel32.dll", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool SetProcessWorkingSetSizeEx(IntPtr process, UIntPtr minimum, UIntPtr maximum, uint flags);

		[DllImport("kernel32.dll", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

		[DllImport("kernel32.dll", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool K32GetProcessMemoryInfo(IntPtr process, ref ProcessMemoryCounters counters, uint size);

		[StructLayout(LayoutKind.Sequential)]
		private struct ProcessMemoryCounters
		{
			public uint cb;
			public uint PageFaultCount;
			public UIntPtr PeakWorkingSetSize;
			public UIntPtr WorkingSetSize;
			public UIntPtr QuotaPeakPagedPoolUsage;
			public UIntPtr QuotaPagedPoolUsage;
			public UIntPtr QuotaPeakNonPagedPoolUsage;
			public UIntPtr QuotaNonPagedPoolUsage;
			public UIntPtr PagefileUsage;
			public UIntPtr PeakPagefileUsage;
		}

		private const uint StillActive = 259;
		private const int ErrorInvalidParameter = 87;

		/// <summary>Result of QueryWorkingSet: the pid is gone/exited, could not be read, or was read.</summary>
		public enum ProcState { Exited, Unknown, Alive }

		/// <summary>
		/// Working set of one process via OpenProcess + K32GetProcessMemoryInfo. Process.WorkingSet64 on .NET Framework
		/// snapshots every process and thread on the system (~3 ms each with ~350 processes); this is ~0.002 ms.
		/// </summary>
		public static ProcState QueryWorkingSet(int pid, out long bytes)
		{
			bytes = 0;
			if (pid <= 0) return ProcState.Exited;
			IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
			if (handle == IntPtr.Zero)
			{
				// ERROR_INVALID_PARAMETER means no such pid; anything else (access denied) is unknown.
				return Marshal.GetLastWin32Error() == ErrorInvalidParameter ? ProcState.Exited : ProcState.Unknown;
			}
			try
			{
				uint code;
				if (GetExitCodeProcess(handle, out code) && code != StillActive) return ProcState.Exited;
				ProcessMemoryCounters c = new ProcessMemoryCounters();
				c.cb = (uint)Marshal.SizeOf(typeof(ProcessMemoryCounters));
				if (!K32GetProcessMemoryInfo(handle, ref c, c.cb)) return ProcState.Unknown;
				bytes = (long)c.WorkingSetSize.ToUInt64();
				return ProcState.Alive;
			}
			finally
			{
				CloseHandle(handle);
			}
		}

		// Stores the hardware profile used for budget math. Idempotent.
		public static void Initialize(HardwareProfile hw)
		{
			try
			{
				lock (Lock)
				{
					if (hw != null) hardware = hw;
				}
			}
			catch { }
		}

		// Sets the instance count used by SuggestedMb scaling (clamped to 1..16).
		public static void SetInstanceCountHint(int count)
		{
			try
			{
				lock (Lock)
				{
					countHint = ClampInt(count, CountMin, CountMax);
				}
			}
			catch { }
		}

		// Suggested cap in MB for a preset, scaled down when the instance count exceeds the budget.
		public static int SuggestedMb(MemoryPreset preset)
		{
			int baseMb = BaseMbFor(preset);
			if (baseMb <= 0) return 0;

			try
			{
				HardwareProfile hw = GetHardware();
				double budget = BudgetMb(hw);
				int count = GetCountHint();

				double value = baseMb;
				bool scaled = false;
				if (count * (double)baseMb > budget)
				{
					value = baseMb * (budget / (count * (double)baseMb));
					scaled = true;
				}
				if (value < ScaledFloorMb) value = ScaledFloorMb;

				int rounded = RoundToStep(value);
				// Rounding a scaled value down can dip under the floor; bump to the next step.
				if (scaled && rounded < ScaledFloorMb) rounded = ScaledFloorMb + RoundStepMb - (ScaledFloorMb % RoundStepMb);
				return rounded;
			}
			catch
			{
				return baseMb;
			}
		}

		// Cap in MB for a preset or custom value. 0 means no cap.
		public static int ResolveMb(MemoryPreset preset, int customMb)
		{
			try
			{
				if (preset == MemoryPreset.Custom)
				{
					return ClampInt(customMb, CustomMinMb, CustomMaxMb);
				}
				return SuggestedMb(preset);
			}
			catch
			{
				return 0;
			}
		}

		// Registers pid with a cap and applies it now. Unlimited clears the cap.
		public static bool Apply(int pid, MemoryPreset preset, int customMb)
		{
			try
			{
				if (pid <= 0) return false;

				int capMb = ResolveMb(preset, customMb);
				if (capMb <= 0)
				{
					return ReleaseEntry(pid);
				}

				lock (Lock)
				{
					CapByPid[pid] = capMb;
					EnsureTimerLocked();
				}

				bool ok = ApplyNativeCap(pid, capMb);
				long ws = ReadWorkingSetMb(pid);
				if (ws >= 0)
				{
					lock (Lock)
					{
						if (CapByPid.ContainsKey(pid)) WsByPid[pid] = ws;
					}
				}
				return ok;
			}
			catch
			{
				return false;
			}
		}

		// Removes the entry and lifts the max cap. Failures are ignored.
		public static void Clear(int pid)
		{
			try
			{
				if (pid <= 0) return;
				ReleaseEntry(pid);
			}
			catch { }
		}

		// Clears every registered process and stops the timer.
		public static void ClearAll()
		{
			try
			{
				List<int> pids = new List<int>();
				lock (Lock)
				{
					pids.AddRange(CapByPid.Keys);
				}

				foreach (int pid in pids)
				{
					ReleaseEntry(pid);
				}

				lock (Lock)
				{
					CapByPid.Clear();
					WsByPid.Clear();
					StopTimerLocked();
				}
			}
			catch { }
		}

		// Working set in MB. Uses the cache for tracked pids, otherwise reads once. -1 when gone.
		public static long GetWorkingSetMb(int pid)
		{
			try
			{
				if (pid <= 0) return -1;

				lock (Lock)
				{
					long cached;
					if (WsByPid.TryGetValue(pid, out cached)) return cached;
				}

				long ws = ReadWorkingSetMb(pid);
				if (ws >= 0)
				{
					lock (Lock)
					{
						if (CapByPid.ContainsKey(pid)) WsByPid[pid] = ws;
					}
				}
				return ws;
			}
			catch
			{
				return -1;
			}
		}

		// Currently applied cap in MB for pid, or 0.
		public static int GetCapMb(int pid)
		{
			try
			{
				lock (Lock)
				{
					int cap;
					if (CapByPid.TryGetValue(pid, out cap)) return cap;
				}
				return 0;
			}
			catch
			{
				return 0;
			}
		}

		// Human-readable warnings for a set of allocations. Empty list means no problems found.
		public static List<string> ValidateAllocation(IList<MemoryAllocation> items)
		{
			List<string> warnings = new List<string>();
			try
			{
				if (items == null || items.Count == 0) return warnings;

				HardwareProfile hw = GetHardware();
				double freeMb = HardwareProfile.GetFreeRamGb() * 1024.0;
				double budgetMb = BudgetMb(hw);
				double reserveGb = HardwareProfile.OsReserveGb;
				int highMb = SuggestedMb(MemoryPreset.High);

				long totalMb = 0;
				foreach (MemoryAllocation item in items)
				{
					if (item == null) continue;

					int resolved = item.Preset == MemoryPreset.Unlimited ? highMb : ResolveMb(item.Preset, item.CustomMb);
					totalMb += resolved;

					if (resolved > 0 && resolved < MinSafeMb)
					{
						warnings.Add(string.Format(CultureInfo.InvariantCulture,
							"{0} is capped at {1} MB, below the {2} MB safe minimum. It will likely stutter.",
							LabelFor(item), resolved, MinSafeMb));
					}
				}

				if (totalMb > freeMb)
				{
					warnings.Add(string.Format(CultureInfo.InvariantCulture,
						"Combined caps of {0} MB exceed free RAM of {1:0.0} GB ({2:0} MB). Windows will page memory to disk and the games may stutter.",
						totalMb, freeMb / 1024.0, freeMb));
				}

				if (totalMb > budgetMb)
				{
					warnings.Add(string.Format(CultureInfo.InvariantCulture,
						"Combined caps of {0} MB exceed the safe budget of {1:0} MB (installed RAM minus {2:0.0} GB reserved for Windows).",
						totalMb, budgetMb, reserveGb));
				}
			}
			catch { }
			return warnings;
		}

		// Clears the native cap and removes the entry for pid. Returns the native result.
		private static bool ReleaseEntry(int pid)
		{
			lock (Lock)
			{
				CapByPid.Remove(pid);
				WsByPid.Remove(pid);
				if (CapByPid.Count == 0) StopTimerLocked();
			}
			return ClearNativeCap(pid);
		}

		// Timer callback: re-applies caps, refreshes the cache, drops exited processes.
		private static void Tick(object state)
		{
			if (Interlocked.CompareExchange(ref tickBusy, 1, 0) != 0) return;

			try
			{
				List<int> pids = new List<int>();
				List<int> caps = new List<int>();
				lock (Lock)
				{
					foreach (KeyValuePair<int, int> pair in CapByPid)
					{
						pids.Add(pair.Key);
						caps.Add(pair.Value);
					}
				}

				List<int> gone = new List<int>();
				for (int i = 0; i < pids.Count; i++)
				{
					int pid = pids[i];
					bool exited = false;
					long ws = -1;
					try
					{
						long bytes;
						ProcState st = QueryWorkingSet(pid, out bytes);
						if (st == ProcState.Exited) exited = true;
						else if (st == ProcState.Alive) ws = bytes / (1024L * 1024L);
					}
					catch { }

					if (exited)
					{
						gone.Add(pid);
						continue;
					}

					ApplyNativeCap(pid, caps[i]);

					if (ws >= 0)
					{
						lock (Lock)
						{
							if (CapByPid.ContainsKey(pid)) WsByPid[pid] = ws;
						}
					}
				}

				lock (Lock)
				{
					foreach (int pid in gone)
					{
						CapByPid.Remove(pid);
						WsByPid.Remove(pid);
					}
					if (CapByPid.Count == 0) StopTimerLocked();
				}
			}
			catch { }
			finally
			{
				Interlocked.Exchange(ref tickBusy, 0);
			}
		}

		// Creates the re-apply timer if needed. Caller must hold Lock.
		private static void EnsureTimerLocked()
		{
			if (timer != null) return;
			int period = ReapplySeconds * 1000;
			timer = new Timer(new TimerCallback(Tick), null, period, period);
		}

		// Disposes the re-apply timer if running. Caller must hold Lock.
		private static void StopTimerLocked()
		{
			if (timer == null) return;
			timer.Dispose();
			timer = null;
		}

		// Sets the max working-set cap for pid. Returns false when the process cannot be opened or the call fails.
		private static bool ApplyNativeCap(int pid, int capMb)
		{
			IntPtr handle = IntPtr.Zero;
			try
			{
				handle = OpenProcess(PROCESS_SET_QUOTA | PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
				if (handle == IntPtr.Zero) return false;

				ulong maxBytes = (ulong)capMb * 1024UL * 1024UL;
				ulong nativeMax = UIntPtr.Size == 4 ? (ulong)uint.MaxValue : ulong.MaxValue;
				if (maxBytes > nativeMax) maxBytes = nativeMax;

				UIntPtr minimum = (UIntPtr)((ulong)MinWorkingSetMb * 1024UL * 1024UL);
				return SetProcessWorkingSetSizeEx(handle, minimum, (UIntPtr)maxBytes,
					QUOTA_LIMITS_HARDWS_MAX_ENABLE | QUOTA_LIMITS_HARDWS_MIN_DISABLE);
			}
			catch
			{
				return false;
			}
			finally
			{
				if (handle != IntPtr.Zero) CloseHandle(handle);
			}
		}

		// Removes the max cap for pid. Failures are ignored by callers.
		private static bool ClearNativeCap(int pid)
		{
			IntPtr handle = IntPtr.Zero;
			try
			{
				handle = OpenProcess(PROCESS_SET_QUOTA | PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
				if (handle == IntPtr.Zero) return false;

				// All-ones values (the -1 sentinel) are ignored by the kernel when MAX_DISABLE is set.
				UIntPtr allOnes = UIntPtr.Size == 4 ? new UIntPtr(uint.MaxValue) : new UIntPtr(ulong.MaxValue);
				return SetProcessWorkingSetSizeEx(handle, allOnes, allOnes, QUOTA_LIMITS_HARDWS_MAX_DISABLE);
			}
			catch
			{
				return false;
			}
			finally
			{
				if (handle != IntPtr.Zero) CloseHandle(handle);
			}
		}

		// One-off working-set read in MB. Returns -1 when the process is gone.
		private static long ReadWorkingSetMb(int pid)
		{
			long bytes;
			return QueryWorkingSet(pid, out bytes) == ProcState.Alive ? bytes / (1024L * 1024L) : -1;
		}

		// Returns the stored hardware profile, detecting it once if needed.
		private static HardwareProfile GetHardware()
		{
			lock (Lock)
			{
				if (hardware == null)
				{
					try
					{
						hardware = HardwareProfile.Detect();
					}
					catch { }

					if (hardware == null)
					{
						hardware = new HardwareProfile();
						hardware.RamGb = FallbackRamGb;
						hardware.FreeRamGb = 1.0;
					}
				}
				return hardware;
			}
		}

		private static int GetCountHint()
		{
			lock (Lock)
			{
				return countHint;
			}
		}

		// Safe budget in MB: installed RAM minus the OS reserve, floored at 2048 MB.
		private static double BudgetMb(HardwareProfile hw)
		{
			double budget = (hw.RamGb - HardwareProfile.OsReserveGb) * 1024.0;
			return Math.Max(BudgetFloorMb, budget);
		}

		private static int BaseMbFor(MemoryPreset preset)
		{
			switch (preset)
			{
				case MemoryPreset.High:
					return 3584;
				case MemoryPreset.Medium:
					return 2048;
				case MemoryPreset.Low:
					return 1200;
				default:
					return 0;
			}
		}

		private static int RoundToStep(double value)
		{
			return (int)(Math.Round(value / RoundStepMb, MidpointRounding.AwayFromZero) * RoundStepMb);
		}

		private static int ClampInt(int value, int min, int max)
		{
			if (value < min) return min;
			if (value > max) return max;
			return value;
		}

		private static string LabelFor(MemoryAllocation item)
		{
			if (!string.IsNullOrEmpty(item.Label)) return item.Label;
			return string.Format(CultureInfo.InvariantCulture, "PID {0}", item.Pid);
		}
	}
}
