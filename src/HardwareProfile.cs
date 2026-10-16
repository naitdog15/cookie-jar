using System;
using System.Collections.Generic;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace RbxMulti
{
	public class HardwareProfile
	{
		// RAM kept free for Windows and background apps.
		public const double OsReserveGb = 4.0;

		private const double GiB = 1024.0 * 1024.0 * 1024.0;

		// WMI AdapterRAM is a uint32; values at or above this are treated as capped/unknown.
		private const long AdapterRamCapThreshold = 4294967295L - 1048576L;

		private const string DisplayClassPath = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11cd-bfc1-08002be10318}";

		public int CpuCores { get; set; }
		public int LogicalProcessors { get; set; }
		public double RamGb { get; set; }
		public string CpuName { get; set; }
		public string GpuName { get; set; }
		public double VramGb { get; set; }
		public double FreeRamGb { get; set; }

		// RAM budgeted per running Roblox instance.
		public double PerInstanceRamGb
		{
			get { return 2.0; }
		}

		public int RecommendedMax
		{
			get
			{
				double ramAvailable = RamGb - OsReserveGb;
				int maxFromRam = (int)Math.Floor(ramAvailable / PerInstanceRamGb);
				// Extra hardware threads help background clients, but are not full cores.
				int cores = Math.Max(1, CpuCores);
				int threads = Math.Max(cores, LogicalProcessors);
				int cpuBudget = cores + (threads - cores) / 2;
				int limit = Math.Min(cpuBudget, maxFromRam);

				// Low-VRAM GPUs (under 4 GB) are limited to 3 instances.
				if (VramGb > 0 && VramGb < 4.0)
				{
					limit = Math.Min(limit, 3);
				}

				if (limit < 1) limit = 1;
				return limit;
			}
		}

		public int HardMax
		{
			get
			{
				// Higher counts assume lighter/background clients (1.5 GB each).
				// Actual free-memory checks in Restrictor still gate every launch.
				int ramBudget = (int)Math.Floor((RamGb - OsReserveGb) / 1.5);
				int cpuBudget = Math.Max(Math.Max(1, CpuCores), LogicalProcessors);
				int hard = Math.Min(cpuBudget, ramBudget);
				if (VramGb > 0 && VramGb < 4.0) hard = Math.Min(hard, 5);
				return Math.Max(RecommendedMax, hard);
			}
		}

		public string Summary
		{
			get
			{
				return string.Format(CultureInfo.InvariantCulture,
					"{0} | {1}C/{2}T | {3:0.0} GB RAM | {4} {5:0.0} GB",
					CpuName, CpuCores, LogicalProcessors, RamGb, GpuName, VramGb);
			}
		}

		public static HardwareProfile Detect()
		{
			HardwareProfile profile = new HardwareProfile();
			profile.CpuCores = 1;
			profile.LogicalProcessors = 1;
			profile.RamGb = 4.0;
			profile.CpuName = "Unknown";
			profile.GpuName = "Unknown";
			profile.VramGb = 0.5;
			profile.FreeRamGb = 1.0;

			try { DetectCpu(profile); } catch { }
			try { DetectRam(profile); } catch { }
			try { DetectGpu(profile); } catch { }

			try { profile.FreeRamGb = GetFreeRamGb(); } catch { }

			return profile;
		}

		/// <summary>Free physical RAM in GiB. Uses GlobalMemoryStatusEx, falls back to WMI.</summary>
		public static double GetFreeRamGb()
		{
			try
			{
				MEMORYSTATUSEX ms;
				if (TryGetMemoryStatus(out ms) && ms.ullAvailPhys > 0)
				{
					return ms.ullAvailPhys / GiB;
				}
			}
			catch { }

			double wmi = GetWmiFreeRamGb();
			return wmi >= 0 ? wmi : 1.0;
		}

		private static void DetectCpu(HardwareProfile profile)
		{
			using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT NumberOfCores, NumberOfLogicalProcessors, Name FROM Win32_Processor"))
			using (ManagementObjectCollection collection = searcher.Get())
			{
				foreach (ManagementBaseObject item in collection)
				{
					using (ManagementObject processor = item as ManagementObject)
					{
						if (processor == null) continue;
						try
						{
							object cores = processor["NumberOfCores"];
							object logical = processor["NumberOfLogicalProcessors"];
							object name = processor["Name"];

							if (cores != null)
							{
								profile.CpuCores = Convert.ToInt32(cores);
							}
							if (logical != null)
							{
								profile.LogicalProcessors = Convert.ToInt32(logical);
							}
							if (name != null)
							{
								profile.CpuName = name.ToString().Trim();
							}
						}
						catch { }
					}
					break;
				}
			}
		}

		private static void DetectRam(HardwareProfile profile)
		{
			bool ok = false;
			try
			{
				using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem"))
				using (ManagementObjectCollection collection = searcher.Get())
				{
					foreach (ManagementBaseObject item in collection)
					{
						using (ManagementObject computer = item as ManagementObject)
						{
							if (computer == null) continue;
							try
							{
								object totalMemory = computer["TotalPhysicalMemory"];
								if (totalMemory != null)
								{
									long bytes = Convert.ToInt64(totalMemory);
									profile.RamGb = bytes / GiB;
									ok = bytes > 0;
								}
							}
							catch { }
						}
						break;
					}
				}
			}
			catch { }

			if (!ok)
			{
				try
				{
					MEMORYSTATUSEX ms;
					if (TryGetMemoryStatus(out ms) && ms.ullTotalPhys > 0)
					{
						profile.RamGb = ms.ullTotalPhys / GiB;
					}
				}
				catch { }
			}
		}

		private static void DetectGpu(HardwareProfile profile)
		{
			string bestName = null;
			int bestScore = -1;

			try
			{
				using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController"))
				using (ManagementObjectCollection collection = searcher.Get())
				{
					foreach (ManagementBaseObject item in collection)
					{
						using (ManagementObject gpu = item as ManagementObject)
						{
							if (gpu == null) continue;
							try
							{
								object name = gpu["Name"];
								if (name == null) continue;

								string text = name.ToString().Trim();
								int score = ScoreGpu(text);
								if (score > bestScore)
								{
									bestScore = score;
									bestName = text;
								}
							}
							catch { }
						}
					}
				}
			}
			catch { }

			if (!string.IsNullOrEmpty(bestName))
			{
				profile.GpuName = bestName;
				profile.VramGb = DetectVramGb(bestName);
			}
		}

		// Higher score wins: discrete > Radeon > integrated > virtual/basic.
		private static int ScoreGpu(string name)
		{
			string upper = name.ToUpperInvariant();

			if (upper.Contains("MICROSOFT BASIC") || upper.Contains("VIRTUAL") ||
				upper.Contains("REMOTE") || upper.Contains("PARSEC"))
			{
				return 0;
			}
			if (upper.Contains("NVIDIA") || upper.Contains("GEFORCE") || upper.Contains("RTX") ||
				upper.Contains("GTX") || upper.Contains("RADEON RX") || upper.Contains("ARC(") ||
				upper.Contains("ARC "))
			{
				return 3;
			}
			if (upper.Contains("RADEON"))
			{
				return 2;
			}
			if (upper.Contains("INTEL"))
			{
				return 1;
			}
			return 1;
		}

		// VRAM in GB: registry (avoids the 4 GB uint32 cap), then WMI AdapterRAM, then name table.
		private static double DetectVramGb(string gpuName)
		{
			double matchBest = 0;
			double anyBest = 0;

			try
			{
				using (RegistryKey classKey = Registry.LocalMachine.OpenSubKey(DisplayClassPath))
				{
					if (classKey != null)
					{
						for (int i = 0; i < 100; i++)
						{
							try
							{
								using (RegistryKey key = classKey.OpenSubKey(i.ToString("D4")))
								{
									if (key == null) continue;

									long bytes = ToLongBytes(key.GetValue("HardwareInformation.qwMemorySize"));
									if (bytes <= 0) continue;

									double gb = bytes / GiB;
									if (gb > anyBest) anyBest = gb;

									string desc = key.GetValue("DriverDesc") as string;
									if (desc != null && NamesMatch(desc, gpuName) && gb > matchBest)
									{
										matchBest = gb;
									}
								}
							}
							catch { }
						}
					}
				}
			}
			catch { }

			if (matchBest > 0) return matchBest;
			if (anyBest > 0) return anyBest;

			double wmi = GetWmiAdapterRamGb(gpuName);
			if (wmi > 0) return wmi;

			return HeuristicVramGb(gpuName);
		}

		private static long ToLongBytes(object value)
		{
			if (value == null) return 0;
			if (value is long) return (long)value;
			if (value is int) return (long)(int)value;

			byte[] raw = value as byte[];
			if (raw != null && raw.Length >= 8)
			{
				return BitConverter.ToInt64(raw, 0);
			}
			return 0;
		}

		private static bool NamesMatch(string a, string b)
		{
			if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
			string ua = a.ToUpperInvariant();
			string ub = b.ToUpperInvariant();
			return ua.Contains(ub) || ub.Contains(ua);
		}

		// Returns GB from WMI AdapterRAM for the named adapter, or -1 when absent or capped.
		private static double GetWmiAdapterRamGb(string gpuName)
		{
			try
			{
				using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT Name, AdapterRAM FROM Win32_VideoController"))
				using (ManagementObjectCollection collection = searcher.Get())
				{
					foreach (ManagementBaseObject item in collection)
					{
						using (ManagementObject gpu = item as ManagementObject)
						{
							if (gpu == null) continue;
							try
							{
								object name = gpu["Name"];
								object vram = gpu["AdapterRAM"];
								if (name == null || vram == null) continue;
								if (!NamesMatch(name.ToString(), gpuName)) continue;

								long bytes = Convert.ToInt64(vram);
								if (bytes <= 0 || bytes >= AdapterRamCapThreshold) return -1;
								return bytes / GiB;
							}
							catch { }
						}
					}
				}
			}
			catch { }

			return -1;
		}

		private static readonly KeyValuePair<string, double>[] VramTable = new KeyValuePair<string, double>[]
		{
			new KeyValuePair<string, double>("RTX 4090", 24.0),
			new KeyValuePair<string, double>("RTX 4080", 16.0),
			new KeyValuePair<string, double>("RTX 4070", 12.0),
			new KeyValuePair<string, double>("RTX 4060", 8.0),
			new KeyValuePair<string, double>("RTX 3090", 24.0),
			new KeyValuePair<string, double>("RTX 3080", 10.0),
			new KeyValuePair<string, double>("RTX 3070", 8.0),
			new KeyValuePair<string, double>("RTX 3060", 12.0),
			new KeyValuePair<string, double>("GTX 1660", 6.0),
			new KeyValuePair<string, double>("RX 6600", 8.0)
		};

		private static double HeuristicVramGb(string gpuName)
		{
			if (string.IsNullOrEmpty(gpuName)) return 0.5;
			string upper = gpuName.ToUpperInvariant();
			foreach (KeyValuePair<string, double> entry in VramTable)
			{
				if (upper.Contains(entry.Key.ToUpperInvariant()))
				{
					return entry.Value;
				}
			}
			return 0.5;
		}

		private static double GetWmiFreeRamGb()
		{
			try
			{
				using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT FreePhysicalMemory FROM Win32_OperatingSystem"))
				using (ManagementObjectCollection collection = searcher.Get())
				{
					foreach (ManagementBaseObject item in collection)
					{
						using (ManagementObject os = item as ManagementObject)
						{
							if (os == null) continue;
							object freeMem = os["FreePhysicalMemory"];
							if (freeMem != null)
							{
								long kilobytes = Convert.ToInt64(freeMem);
								return kilobytes / (1024.0 * 1024.0);
							}
						}
						break;
					}
				}
			}
			catch { }

			return -1;
		}

		private static bool TryGetMemoryStatus(out MEMORYSTATUSEX ms)
		{
			ms = new MEMORYSTATUSEX();
			ms.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
			try
			{
				return GlobalMemoryStatusEx(ref ms);
			}
			catch
			{
				return false;
			}
		}

		[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

		[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
		private struct MEMORYSTATUSEX
		{
			public uint dwLength;
			public uint dwMemoryLoad;
			public ulong ullTotalPhys;
			public ulong ullAvailPhys;
			public ulong ullTotalPageFile;
			public ulong ullAvailPageFile;
			public ulong ullTotalVirtual;
			public ulong ullAvailVirtual;
			public ulong ullAvailExtendedVirtual;
		}
	}
}
