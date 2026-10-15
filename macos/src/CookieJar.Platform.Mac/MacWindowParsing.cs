using System;
using System.Collections.Generic;
using System.Globalization;

namespace CookieJar.Platform.Mac
{
    /// <summary>
    /// One window from the CGWindowList listing. Id is the CGWindowID: unique, and stable for as long as the window
    /// exists (it does not shift when other windows open, close or change order). Bounds are top-left screen points.
    /// </summary>
    public sealed class MacWindowRow
    {
        /// <summary>Smallest side of a window that counts as an application window (menus and overlays are smaller).</summary>
        public const int MinWindowSize = 32;

        public MacWindowRow(long id, int pid, int layer, ScreenRect bounds, bool onScreen)
        {
            Id = id; Pid = pid; Layer = layer; Bounds = bounds; OnScreen = onScreen;
        }
        public long Id { get; }
        public int Pid { get; }
        /// <summary>CGWindowLevel; 0 is a normal window, menus, the dock and overlays use other layers.</summary>
        public int Layer { get; }
        public ScreenRect Bounds { get; }
        /// <summary>kCGWindowIsOnscreen: false for a minimised window and also for one on another Space.</summary>
        public bool OnScreen { get; }

        /// <summary>A normal application window: layer 0 and a real size.</summary>
        public bool IsAppWindow
        {
            get { return Layer == 0 && Bounds.Width >= MinWindowSize && Bounds.Height >= MinWindowSize; }
        }
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
    /// A window is named by its CGWindowID (a positive number that fits 32 bits), the same number `screencapture -l` takes.
    /// Window state is read with CGWindowListCopyWindowInfo (JXA, no Accessibility needed); changes (raise, minimise,
    /// move) go through System Events, which finds the target window by process and current position at that moment,
    /// never by list position and never by title text (so nothing another app controls is pasted into a script).
    /// </summary>
    public static class MacWindowParsing
    {
        /// <summary>Height removed from the outer frame of an ordinary titled window for TryGetContentBounds.</summary>
        // MAC-UNVERIFIED: 28 pt is the standard unified title bar; toolbars and tabbed windows make it taller.
        public const int TitleBarHeight = 28;

        /// <summary>Two rectangles closer than this on every edge count as the same (points).</summary>
        public const int SameRectTolerance = 2;

        // Placeholders (@PID@, @X@, @Y@, @W@, @H@, @NX@, @NY@, @NW@, @NH@, @VALUE@) are filled by the Build*Script helpers
        // with whole numbers only. AppleScript comments are not used so the text can be sent as one -e argument.

        /// <summary>
        /// Lists windows as `id|pid|layer|x|y|w|h|onscreen`, front to back; @PID@ 0 lists every process.
        /// Run with `osascript -l JavaScript`.
        /// </summary>
        // MAC-UNVERIFIED: assumes JXA can call CGWindowListCopyWindowInfo after ObjC.import('CoreGraphics') and that
        // ObjC.deepUnwrap turns the CFArray of dictionaries into plain objects (kCGWindowNumber, kCGWindowOwnerPID,
        // kCGWindowLayer, kCGWindowBounds {X,Y,Width,Height}, kCGWindowIsOnscreen).
        public const string WindowListScript = @"(function () {
  ObjC.import('CoreGraphics');
  var info = ObjC.deepUnwrap(ObjC.castRefToObject(
    $.CGWindowListCopyWindowInfo($.kCGWindowListOptionAll | $.kCGWindowListExcludeDesktopElements, $.kCGNullWindowID)));
  var wanted = @PID@;
  var rows = [];
  for (var i = 0; i < info.length; i++) {
    var w = info[i];
    if (wanted !== 0 && w.kCGWindowOwnerPID !== wanted) continue;
    var b = w.kCGWindowBounds || {};
    rows.push([w.kCGWindowNumber, w.kCGWindowOwnerPID, w.kCGWindowLayer, b.X, b.Y, b.Width, b.Height,
      w.kCGWindowIsOnscreen ? 1 : 0].join('|'));
  }
  return rows.join('\n');
})();";

        /// <summary>Prints the unix id of the frontmost process, or nothing when there is none.</summary>
        public const string FrontmostScript = @"tell application ""System Events""
	set frontProcs to every process whose frontmost is true
	if (count of frontProcs) is 0 then return """"
	return (unix id of item 1 of frontProcs) as string
end tell";

        // Finds the window of the process whose position and size match the CGWindowList row (within 2 points) and then
        // runs one action on it. If no window matches, the script raises an error and the call reports failure.
        private const string ActionTemplate = @"tell application ""System Events""
	set p to first process whose unix id is @PID@
	set target to missing value
	repeat with i from 1 to count of windows of p
		set w to window i of p
		set {px, py} to position of w
		set {sw, sh} to size of w
		if px > (@X@ - 3) and px < (@X@ + 3) and py > (@Y@ - 3) and py < (@Y@ + 3) and sw > (@W@ - 3) and sw < (@W@ + 3) and sh > (@H@ - 3) and sh < (@H@ + 3) then
			set target to w
			exit repeat
		end if
	end repeat
	if target is missing value then error ""window not found""
@ACTION@
end tell";

        private const string RaiseAction = @"	set frontmost of p to true
	perform action ""AXRaise"" of target";

        private const string SetMinimisedAction = @"	set value of attribute ""AXMinimized"" of target to @VALUE@";

        private const string SetBoundsAction = @"	set position of target to {@NX@, @NY@}
	set size of target to {@NW@, @NH@}";

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

        /// <summary>The window-list script for one process (pid 0 lists every process).</summary>
        public static string WindowsScript(int pid)
        {
            return WindowListScript.Replace("@PID@", Num(Math.Max(0, pid)));
        }

        public static string FrontmostProcessScript()
        {
            return FrontmostScript;
        }

        /// <summary>Raises the window of pid that currently has these bounds.</summary>
        public static string RaiseScript(int pid, ScreenRect current)
        {
            return Action(pid, current, RaiseAction);
        }

        /// <summary>Minimises or restores the window of pid that currently has these bounds.</summary>
        public static string SetMinimisedScript(int pid, ScreenRect current, bool minimised)
        {
            return Action(pid, current, SetMinimisedAction.Replace("@VALUE@", minimised ? "true" : "false"));
        }

        /// <summary>Moves and resizes the window of pid that currently has the bounds `current`.</summary>
        public static string SetBoundsScript(int pid, ScreenRect current, ScreenRect target)
        {
            return Action(pid, current, SetBoundsAction
                .Replace("@NX@", Num(target.X)).Replace("@NY@", Num(target.Y))
                .Replace("@NW@", Num(target.Width)).Replace("@NH@", Num(target.Height)));
        }

        private static string Action(int pid, ScreenRect current, string action)
        {
            return ActionTemplate.Replace("@ACTION@", action).Replace("@PID@", Num(pid))
                .Replace("@X@", Num(current.X)).Replace("@Y@", Num(current.Y))
                .Replace("@W@", Num(current.Width)).Replace("@H@", Num(current.Height));
        }

        /// <summary>True for a usable CGWindowID: positive and within 32 bits (what `screencapture -l` accepts).</summary>
        public static bool IsValidWindowId(long windowId)
        {
            return windowId > 0 && windowId <= uint.MaxValue;
        }

        /// <summary>Parses the window listing. Blank, malformed or out-of-range lines are skipped.</summary>
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
            if (f.Length != 8) return false;

            long id;
            if (!long.TryParse(f[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out id) || !IsValidWindowId(id)) return false;
            int pid, layer;
            if (!int.TryParse(f[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out pid) || pid <= 0) return false;
            if (!int.TryParse(f[2].Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out layer)) return false;

            double x, y, w, h;
            if (!TryNumber(f[3], out x) || !TryNumber(f[4], out y) || !TryNumber(f[5], out w) || !TryNumber(f[6], out h)) return false;
            if (w < 0 || h < 0) return false;

            row = new MacWindowRow(id, pid, layer, new ScreenRect(Round(x), Round(y), Round(w), Round(h)), f[7].Trim() == "1");
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

        /// <summary>
        /// Content area of an outer window frame. A frame that covers a whole display (full screen or borderless) has
        /// no title bar, so the content is the frame itself; any other frame loses TitleBarHeight at the top. False when
        /// the frame is empty or too short to have a content area.
        /// </summary>
        // MAC-UNVERIFIED: "covers a whole display" is a heuristic for full screen / borderless windows; a titled window
        // that happens to be exactly screen sized would be reported without its title bar.
        public static bool ToContentBounds(ScreenRect frame, IReadOnlyList<MonitorInfo> monitors, out ScreenRect content)
        {
            content = frame;
            if (frame.Width <= 0 || frame.Height <= 0) return false;
            if (monitors != null)
            {
                foreach (MonitorInfo m in monitors)
                {
                    if (SameRect(frame, m.Bounds)) return true;
                }
            }
            if (frame.Height <= TitleBarHeight) return false;
            content = new ScreenRect(frame.X, frame.Y + TitleBarHeight, frame.Width, frame.Height - TitleBarHeight);
            return true;
        }

        /// <summary>True when every edge of a and b is within SameRectTolerance points.</summary>
        public static bool SameRect(ScreenRect a, ScreenRect b)
        {
            return Math.Abs(a.X - b.X) <= SameRectTolerance && Math.Abs(a.Y - b.Y) <= SameRectTolerance
                && Math.Abs(a.Width - b.Width) <= SameRectTolerance && Math.Abs(a.Height - b.Height) <= SameRectTolerance;
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
