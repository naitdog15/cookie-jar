using System;
using System.Globalization;

namespace CookieJar.Core
{
	public enum RestrictLevel
	{
		Allow,
		Warn,
		Block
	}

	public class RestrictResult
	{
		public RestrictLevel Level { get; set; }
		public string Title { get; set; }
		public string Message { get; set; }

		public bool CanLaunch
		{
			get { return Level != RestrictLevel.Block; }
		}

		public static RestrictResult Create(RestrictLevel level, string title, string message)
		{
			RestrictResult result = new RestrictResult();
			result.Level = level;
			result.Title = title;
			result.Message = message;
			return result;
		}
	}

	public class Restrictor
	{
		private const int FallbackRecommended = 2;
		private const int FallbackHardMax = 4;
		public const double BlockFreeRamGb = 1.5;
		public const double WarnFreeRamGb = 3.0;
		public const double WarnCpuPercent = 90.0;
		public const double CriticalCpuPercent = 97.0;

		private readonly HardwareProfile _profile;
		private readonly int _recommended;
		private readonly int _hardMax;

		public Restrictor(HardwareProfile profile)
		{
			_profile = profile;
			if (profile == null)
			{
				_recommended = FallbackRecommended;
				_hardMax = FallbackHardMax;
			}
			else
			{
				_recommended = profile.RecommendedMax;
				_hardMax = profile.HardMax;
			}
		}

		public int MaxAllowed
		{
			get { return _hardMax; }
		}

		public int Recommended
		{
			get { return _recommended; }
		}

		public RestrictResult Evaluate(int runningCount, double freeRamGb, double cpuPercent)
		{
			string pcName = PcLabel();
			string cores = CoreCount();
			string ram = RamText();

			// 1. Hard limit
			if (runningCount >= _hardMax)
			{
				return RestrictResult.Create(
					RestrictLevel.Block,
					"Hard limit reached",
					string.Format(CultureInfo.InvariantCulture,
						"Hard limit reached: this PC ({0}, {1} cores, {2} GB RAM) can run at most {3} instances. Close one to continue.",
						pcName, cores, ram, _hardMax));
			}

			// 2. Not enough free RAM
			if (freeRamGb < BlockFreeRamGb)
			{
				return RestrictResult.Create(
					RestrictLevel.Block,
					"Not enough memory",
					string.Format(CultureInfo.InvariantCulture,
						"Only {0:F1} GB of RAM free; launching another instance could freeze Windows. Close an instance or other apps first.",
						freeRamGb));
			}

			// 3. Beyond recommended count
			if (runningCount >= _recommended)
			{
				return RestrictResult.Create(
					RestrictLevel.Warn,
					"Above recommended",
					string.Format(CultureInfo.InvariantCulture,
						"You are beyond the recommended {0} instances for this PC; expect stuttering or crashes.",
						_recommended));
			}

			// 4. Low free RAM
			if (freeRamGb < WarnFreeRamGb)
			{
				return RestrictResult.Create(
					RestrictLevel.Warn,
					"Low memory",
					string.Format(CultureInfo.InvariantCulture,
						"Low free RAM ({0:F1} GB). Close apps you do not need before launching.",
						freeRamGb));
			}

			// 5. CPU busy
			if (cpuPercent > WarnCpuPercent)
			{
				return RestrictResult.Create(
					RestrictLevel.Warn,
					"CPU busy",
					string.Format(CultureInfo.InvariantCulture,
						"CPU is at {0:F0}%. A new instance may slow everything down.",
						cpuPercent));
			}

			// 6. All clear
			return RestrictResult.Create(
				RestrictLevel.Allow,
				"Ready",
				string.Format(CultureInfo.InvariantCulture,
					"Ready: {0} of {1} recommended instances in use.",
					runningCount, _recommended));
		}

		public string StatusLine(int runningCount)
		{
			return string.Format(CultureInfo.InvariantCulture,
				"{0} / {1} recommended (hard max {2})",
				runningCount, _recommended, _hardMax);
		}

		public RestrictLevel LevelFor(int runningCount)
		{
			if (runningCount >= _hardMax)
			{
				return RestrictLevel.Block;
			}
			if (runningCount >= _recommended)
			{
				return RestrictLevel.Warn;
			}
			return RestrictLevel.Allow;
		}

		private string PcLabel()
		{
			if (_profile == null || string.IsNullOrEmpty(_profile.CpuName) || _profile.CpuName == "Unknown")
			{
				return "this PC";
			}
			return _profile.CpuName.Trim();
		}

		private string CoreCount()
		{
			if (_profile == null)
			{
				return "?";
			}
			return _profile.CpuCores.ToString(CultureInfo.InvariantCulture);
		}

		private string RamText()
		{
			if (_profile == null)
			{
				return "?";
			}
			return string.Format(CultureInfo.InvariantCulture, "{0:F0}", _profile.RamGb);
		}
	}
}
