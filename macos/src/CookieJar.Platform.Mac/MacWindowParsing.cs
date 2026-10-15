using System;
using System.Collections.Generic;
using System.Globalization;

namespace CookieJar.Platform.Mac
{
    /// <summary>One window from the System Events listing. Bounds are top-left screen pixels.</summary>
    public sealed class MacWindowRow
    {
        public MacWindowRow(int index, ScreenRect bounds, bool minimised, bool visible)
        {
            Index = index; Bounds = bounds; Minimised = minimised; Visible = visible;
        }
        /// <summary>1-based window index in System Events.</summary>
        public int Index { get; }
        public ScreenRect Bounds { get; }
        public bool Minimised { get; }
        public bool Visible { get; }
    }

    /// <summary>One display from the NSScreen listing, in Cocoa coordinates (y grows upwards from the primary display).</summary>
    public sealed class MacScreenRow
    {
        public MacScreenRow(int id, double frameX, double frameY, double frameWidth, double frameHeight,
            double visibleX, double visibleY, double visibleWidth, double visibleHeight, double scale, bool isPrimary)
        {
            Id = id;
            FrameX = frameX; FrameY = frameY; FrameWidth = frameWidth; FrameHeight = frameHeight;
            VisibleX = visibleX; VisibleY = visibleY; VisibleWidth = visibleWidth; VisibleHeight = visibleHeight;
            Scale = scale; IsPrimary = isPrimary;
        }
        public int Id { get; }
        public double FrameX { get; }
        public double FrameY { get; }
        public double FrameWidth { get; }
        public double FrameHeight { get; }
        public double VisibleX { get; }
        public double VisibleY { get; }
        public double VisibleWidth { get; }
        public double VisibleHeight { get; }
        public double Scale { get; }
        public bool IsPrimary { get; }
    }

    /// <summary>
    /// Scripts and pure parsers for the osascript output used by MacWindowService. No I/O; never throws.
    /// Window id = (pid &lt;&lt; 20) | windowIndex, so one long names a window of one process.
    /// </summary>
    public static class MacWindowParsing
    {
        private const int IndexBits = 20;
        private const long IndexMask = (1L << IndexBits) - 1;

        /// <summary>Height removed from the outer frame for TryGetContentBounds.</summary>
        // MAC-UNVERIFIED: 28 px is the standard unified title bar; toolbars and tabbed windows make it taller.
        public const int TitleBarHeight = 28;

        // Placeholders (@PID@, @IDX@, @VALUE@, @X@, @Y@, @W@, @H@) are filled by the Build*Script helpers.
        // AppleScript comments are not used so the text can be sent as one -e argument.

        /// <summary>Lists each window of the process as `index|x|y|w|h|minimised|visible`.</summary>
        public const string WindowsOfProcessScript = @"tell application ""System Events""
	set rowsText to """"
	repeat with p in (every process whose unix id is @PID@)
		set n to count of windows of p
		repeat with i from 1 to n
			set w to window i of p
			set {px, py} to position of w
			set {sw, sh} to size of w
			set mn to ((value of attribute ""AXMinimized"" of w) is true)
			set vis to ((visible of w) is true)
			set rowsText to rowsText & i & ""|"" & px & ""|"" & py & ""|"" & sw & ""|"" & sh & ""|"" & (mn as string) & ""|"" & (vis as string) & linefeed
		end repeat
	end repeat
	return rowsText
end tell";

        /// <summary>Prints the unix id of the frontmost process, or nothing when there is none.</summary>
        public const string FrontmostScript = @"tell application ""System Events""
	set frontProcs to every process whose frontmost is true
	if (count of frontProcs) is 0 then return """"
	return (unix id of item 1 of frontProcs) as string
end tell";

        private const string RaiseTemplate = @"tell application ""System Events""
	set p to first process whose unix id is @PID@
	set frontmost of p to true
	perform action ""AXRaise"" of window @IDX@ of p
end tell";

        private const string SetMinimisedTemplate = @"tell application ""System Events""
	set value of attribute ""AXMinimized"" of window @IDX@ of (first process whose unix id is @PID@) to @VALUE@
end tell";

        private const string SetBoundsTemplate = @"tell application ""System Events""
	tell window @IDX@ of (first process whose unix id is @PID@)
		set position to {@X@, @Y@}
		set size to {@W@, @H@}
	end tell
end tell";

        /// <summary>
        /// Prints one line per display: id|frameX|frameY|frameW|frameH|visX|visY|visW|visH|scale|isPrimary (Cocoa coordinates).
        /// Index 0 of NSScreen.screens is the primary display.
        /// </summary>
        // MAC-UNVERIFIED: assumes JXA can read NSScreen.screens after ObjC.import('AppKit') and that osascript prints the returned string.
        public const string ScreensScript = @"(function () {
  ObjC.import('AppKit');
  var screens = $.NSScreen.screens;
  var rows = [];
  for (var i = 0; i < screens.count; i++) {
    var s = screens.objectAtIndex(i);
    var f = s.frame;
    var v = s.visibleFrame;
    rows.push([i, f.origin.x, f.origin.y, f.size.width, f.size.height,
      v.origin.x, v.origin.y, v.size.width, v.size.height,
      s.backingScaleFactor, i === 0 ? 1 : 0].join('|'));
  }
  return rows.join('\n');
})();";

        public static string WindowsScript(int pid)
        {
            return WindowsOfProcessScript.Replace("@PID@", Num(pid));
        }

        public static string FrontmostProcessScript()
        {
            return FrontmostScript;
        }

        public static string RaiseScript(int pid, int index)
        {
            return RaiseTemplate.Replace("@PID@", Num(pid)).Replace("@IDX@", Num(index));
        }

        public static string SetMinimisedScript(int pid, int index, bool minimised)
        {
            return SetMinimisedTemplate.Replace("@PID@", Num(pid)).Replace("@IDX@", Num(index))
                .Replace("@VALUE@", minimised ? "true" : "false");
        }

        public static string SetBoundsScript(int pid, int index, ScreenRect bounds)
        {
            return SetBoundsTemplate.Replace("@PID@", Num(pid)).Replace("@IDX@", Num(index))
                .Replace("@X@", Num(bounds.X)).Replace("@Y@", Num(bounds.Y))
                .Replace("@W@", Num(bounds.Width)).Replace("@H@", Num(bounds.Height));
        }

        /// <summary>Window id from pid and 1-based index. Returns 0 (invalid) for a non-positive pid or an index out of range.</summary>
        public static long EncodeWindowId(int pid, int index)
        {
            if (pid <= 0 || index <= 0 || index > IndexMask) return 0;
            return ((long)pid << IndexBits) | (long)index;
        }

        /// <summary>Splits a window id. False for 0, negative or an index of 0.</summary>
        public static bool DecodeWindowId(long windowId, out int pid, out int index)
        {
            pid = 0;
            index = 0;
            if (windowId <= 0) return false;
            long p = windowId >> IndexBits;
            long i = windowId & IndexMask;
            if (p <= 0 || p > int.MaxValue || i <= 0) return false;
            pid = (int)p;
            index = (int)i;
            return true;
        }

        /// <summary>Parses the windows listing. Blank, malformed or index-0 lines are skipped.</summary>
        public static List<MacWindowRow> ParseWindowRows(string text)
        {
            var rows = new List<MacWindowRow>();
            if (string.IsNullOrEmpty(text)) return rows;
            foreach (string line in text.Split('\n'))
            {
                MacWindowRow row;
                if (TryParseWindowRow(line, out row)) rows.Add(row);
            }
            return rows;
        }

        public static bool TryParseWindowRow(string line, out MacWindowRow row)
        {
            row = null;
            if (string.IsNullOrWhiteSpace(line)) return false;

            string[] f = line.Trim().Split('|');
            if (f.Length != 7) return false;

            int index;
            if (!int.TryParse(f[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out index) || index <= 0) return false;

            double x, y, w, h;
            if (!TryNumber(f[1], out x) || !TryNumber(f[2], out y) || !TryNumber(f[3], out w) || !TryNumber(f[4], out h)) return false;
            if (w < 0 || h < 0) return false;

            row = new MacWindowRow(index, new ScreenRect(Round(x), Round(y), Round(w), Round(h)),
                IsTrueWord(f[5]), IsTrueWord(f[6]));
            return true;
        }

        /// <summary>Parses the frontmost-process listing. Returns 0 when there is no usable pid.</summary>
        public static int ParsePid(string text)
        {
            int pid;
            if (string.IsNullOrWhiteSpace(text)) return 0;
            if (!int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out pid)) return 0;
            return pid > 0 ? pid : 0;
        }

        /// <summary>Parses the display listing. Malformed lines are skipped.</summary>
        public static List<MacScreenRow> ParseScreenRows(string text)
        {
            var rows = new List<MacScreenRow>();
            if (string.IsNullOrEmpty(text)) return rows;
            foreach (string line in text.Split('\n'))
            {
                MacScreenRow row;
                if (TryParseScreenRow(line, out row)) rows.Add(row);
            }
            return rows;
        }

        public static bool TryParseScreenRow(string line, out MacScreenRow row)
        {
            row = null;
            if (string.IsNullOrWhiteSpace(line)) return false;

            string[] f = line.Trim().Split('|');
            if (f.Length != 11) return false;

            int id;
            if (!int.TryParse(f[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out id) || id < 0) return false;

            double fx, fy, fw, fh, vx, vy, vw, vh, scale;
            if (!TryNumber(f[1], out fx) || !TryNumber(f[2], out fy) || !TryNumber(f[3], out fw) || !TryNumber(f[4], out fh)) return false;
            if (!TryNumber(f[5], out vx) || !TryNumber(f[6], out vy) || !TryNumber(f[7], out vw) || !TryNumber(f[8], out vh)) return false;
            if (!TryNumber(f[9], out scale) || scale <= 0) return false;
            if (fw <= 0 || fh <= 0) return false;

            row = new MacScreenRow(id, fx, fy, fw, fh, vx, vy, vw, vh, scale, f[10].Trim() == "1");
            return true;
        }

        /// <summary>
        /// Converts Cocoa rectangles (origin bottom-left of the primary display) to top-left screen pixels.
        /// Empty or missing input gives an empty list.
        /// </summary>
        public static List<MonitorInfo> ToMonitors(IReadOnlyList<MacScreenRow> rows)
        {
            var list = new List<MonitorInfo>();
            if (rows == null || rows.Count == 0) return list;

            double primaryHeight = rows[0].FrameHeight;
            foreach (MacScreenRow r in rows)
            {
                if (r.IsPrimary) { primaryHeight = r.FrameHeight; break; }
            }
            if (primaryHeight <= 0) return list;

            foreach (MacScreenRow r in rows)
            {
                ScreenRect bounds = ToTopLeft(primaryHeight, r.FrameX, r.FrameY, r.FrameWidth, r.FrameHeight);
                ScreenRect work = ToTopLeft(primaryHeight, r.VisibleX, r.VisibleY, r.VisibleWidth, r.VisibleHeight);
                list.Add(new MonitorInfo(r.Id, bounds, work, r.Scale, r.IsPrimary));
            }
            return list;
        }

        /// <summary>Cocoa rectangle to top-left pixels: y' = primaryHeight - y - height.</summary>
        public static ScreenRect ToTopLeft(double primaryHeight, double x, double y, double width, double height)
        {
            return new ScreenRect(Round(x), TopLeftY(primaryHeight, y, height), Round(width), Round(height));
        }

        /// <summary>Top edge in top-left coordinates of a Cocoa rectangle whose bottom edge is y.</summary>
        public static int TopLeftY(double primaryHeight, double y, double height)
        {
            return Round(primaryHeight - y - height);
        }

        /// <summary>Content area of an outer window frame. False when the frame is too short to have one.</summary>
        public static bool ToContentBounds(ScreenRect frame, out ScreenRect content)
        {
            content = frame;
            if (frame.Height <= TitleBarHeight) return false;
            content = new ScreenRect(frame.X, frame.Y + TitleBarHeight, frame.Width, frame.Height - TitleBarHeight);
            return true;
        }

        private static bool IsTrueWord(string s)
        {
            return string.Equals(s == null ? "" : s.Trim(), "true", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryNumber(string s, out double value)
        {
            return double.TryParse((s ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                && !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static int Round(double value)
        {
            return (int)Math.Round(value);
        }

        private static string Num(int value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }
    }
}
