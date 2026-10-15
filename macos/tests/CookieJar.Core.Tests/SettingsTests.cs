using System;
using System.IO;
using System.Linq;
using System.Text;
using CookieJar.Core;

namespace CookieJar.Tests
{
    public static class SettingsTests
    {
        // Exactly what MainWindow.SaveMiniBarSettings writes on Windows (order and spelling matter).
        private static readonly string[] WindowsSample = new string[]
        {
            "keepalive=false",
            "minibar=true",
            "bgefficiency=true",
            "traytip=false",
            "keepsessions=true",
            "keepmin=4",
            "keepkey=false",
            "pausehospital=false",
            "declinetrades=true",
            "declineinvites=true",
            "clickmethod=Auto",
            "listwait=25"
        };

        public static void Run()
        {
            DefaultsMatchWindows();
            WindowsSampleRoundTripsUnchanged();
            FileRoundTripsUnchanged();
            MissingFileGivesDefaults();
            ClampsNumbers();
            BoolParsing();
            ClickMethodValues();
            UnknownAndMalformedLinesAreIgnored();
        }

        private static void DefaultsMatchWindows()
        {
            MiniBarSettings s = new MiniBarSettings();
            T.Check("default keep-alive is off", !s.KeepAlive);
            T.Check("default background efficiency is on", s.BackgroundEfficiency);
            T.Check("default mini bar is off", !s.MiniBarOn);
            T.Check("default tray tip flag is off", !s.TrayTipShown);
            T.Check("default keep sessions is on", s.KeepSessions);
            T.Check("default keep minutes is 4", s.KeepMinutes == 4, s.KeepMinutes.ToString());
            T.Check("default keep key is off", !s.KeepKey);
            T.Check("default pause hospital is off", !s.PauseHospital);
            T.Check("default decline trades is on", s.DeclineTrades);
            T.Check("default decline invites is on", s.DeclineInvites);
            T.Check("default click method is Auto", s.ClickMethod == "Auto");
            T.Check("default list wait is 25", s.ListWaitSeconds == 25, s.ListWaitSeconds.ToString());
        }

        private static void WindowsSampleRoundTripsUnchanged()
        {
            MiniBarSettings s = new MiniBarSettings();
            s.Load(WindowsSample);
            string[] lines = s.ToLines();
            T.Check("Windows minibar.settings sample round-trips unchanged", lines.SequenceEqual(WindowsSample), string.Join("|", lines));
        }

        private static void FileRoundTripsUnchanged()
        {
            using (TempAppPaths paths = new TempAppPaths())
            {
                string file = Path.Combine(paths.DataDirectory, MiniBarSettings.FileName);
                File.WriteAllLines(file, WindowsSample, Encoding.UTF8);

                MiniBarSettings loaded = MiniBarSettings.Load(paths);
                T.Check("Load reads the Windows file", loaded.MiniBarOn && !loaded.KeepAlive);
                T.Check("Save writes the same lines back", loaded.Save(paths));

                string[] back = File.ReadAllLines(file, Encoding.UTF8);
                T.Check("File round-trip is unchanged", back.SequenceEqual(WindowsSample), string.Join("|", back));
            }
        }

        private static void MissingFileGivesDefaults()
        {
            using (TempAppPaths paths = new TempAppPaths())
            {
                MiniBarSettings s = MiniBarSettings.Load(paths);
                T.Check("Missing file gives the default keep-alive off", !s.KeepAlive);
                T.Check("Missing file gives the default efficiency on", s.BackgroundEfficiency);
                T.Check("Missing file gives the default keep minutes", s.KeepMinutes == 4);
            }
        }

        private static void ClampsNumbers()
        {
            MiniBarSettings s = new MiniBarSettings();
            s.Load(new string[] { "keepmin=99" });
            T.Check("keepmin clamps down to 15", s.KeepMinutes == 15, s.KeepMinutes.ToString());

            s.Load(new string[] { "keepmin=0" });
            T.Check("keepmin clamps up to 1", s.KeepMinutes == 1, s.KeepMinutes.ToString());

            s.Load(new string[] { "keepmin=abc" });
            T.Check("unparsable keepmin keeps the previous value", s.KeepMinutes == 1, s.KeepMinutes.ToString());

            s.Load(new string[] { "listwait=1" });
            T.Check("listwait clamps up to 5", s.ListWaitSeconds == 5, s.ListWaitSeconds.ToString());

            s.Load(new string[] { "listwait=500" });
            T.Check("listwait clamps down to 60", s.ListWaitSeconds == 60, s.ListWaitSeconds.ToString());

            s.Load(new string[] { "listwait=30" });
            T.Check("listwait inside range is kept", s.ListWaitSeconds == 30);
        }

        private static void BoolParsing()
        {
            MiniBarSettings s = new MiniBarSettings();
            s.Load(new string[] { "keepalive=TRUE", "minibar= true ", "bgefficiency=yes", "traytip=1" });
            T.Check("bool values are case-insensitive", s.KeepAlive);
            T.Check("bool values are trimmed", s.MiniBarOn);
            T.Check("bool values other than true are false", !s.BackgroundEfficiency);
            T.Check("traytip=1 is false", !s.TrayTipShown);

            s.Load(new string[] { "keepsessions=maybe" });
            T.Check("keepsessions is on unless exactly false", s.KeepSessions);
            s.Load(new string[] { "keepsessions=FALSE" });
            T.Check("keepsessions=FALSE turns it off", !s.KeepSessions);

            s.Load(new string[] { "declinetrades=garbage", "declineinvites=garbage" });
            T.Check("declinetrades falls back to on", s.DeclineTrades);
            T.Check("declineinvites falls back to on", s.DeclineInvites);
            s.Load(new string[] { "declinetrades= false ", "declineinvites=False" });
            T.Check("declinetrades=false is off", !s.DeclineTrades);
            T.Check("declineinvites=False is off", !s.DeclineInvites);

            s.Load(new string[] { "pausehospital=true", "keepkey=true" });
            T.Check("pausehospital=true is on", s.PauseHospital);
            T.Check("keepkey=true is on", s.KeepKey);
        }

        private static void ClickMethodValues()
        {
            MiniBarSettings s = new MiniBarSettings();
            s.Load(new string[] { "clickmethod=sendinput" });
            T.Check("clickmethod is case-insensitive SendInput", s.ClickMethod == "SendInput", s.ClickMethod);
            s.Load(new string[] { "clickmethod=Legacy" });
            T.Check("clickmethod Legacy", s.ClickMethod == "Legacy", s.ClickMethod);
            s.Load(new string[] { "clickmethod=weird" });
            T.Check("unknown clickmethod becomes Auto", s.ClickMethod == "Auto", s.ClickMethod);
        }

        private static void UnknownAndMalformedLinesAreIgnored()
        {
            MiniBarSettings s = new MiniBarSettings();
            s.Load(new string[] { "=true", "novalue", "", "unknownkey=true", " keepalive = true " });
            T.Check("lines without a key are skipped and the keep-alive line still applies", s.KeepAlive);
            string[] lines = s.ToLines();
            T.Check("unknown keys are not written back", !lines.Any(l => l.StartsWith("unknownkey", StringComparison.Ordinal)));
            T.Check("ToLines always writes the twelve known keys", lines.Length == 12, lines.Length.ToString());
        }
    }
}
