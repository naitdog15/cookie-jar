// ScratchHost.cs - WPF scratch host for the Teleport UI (C# 5). Not shipped.
// Uses the real MainWindow (without adopting any real Roblox client: MainWindow.SkipAdoption) with FAKE instances,
// the real MiniBar with a fake host, and TeleportCardRow in a state matrix.
// It screenshots every state in light and dark into tests\shots, and toggles the theme 20 times counting exceptions.
// It never clicks anything and never touches Roblox processes.
// Build: tests\build_scratch.ps1   Run: tests\out\ScratchHost.exe [outDir]

using System;
using System.Text;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace RbxMulti
{
    public static class ScratchHost
    {
        private static string _out;
        private static int _exceptions;
        private static int _checks, _failed;

        private sealed class FakeHost : IMiniBarHost
        {
            public List<RobloxInstance> All = new List<RobloxInstance>();
            public bool Busy;
            public RobloxInstance Source;
            public TeleportResult Last;
            public RobloxInstance LastSource;
            public DateTime LastUtc = DateTime.UtcNow;
            public string ForcedReason;
            public List<string> Calls = new List<string>();

            public IList<RobloxInstance> Instances { get { return All; } }
            public void SwitchTo(RobloxInstance i) { Calls.Add("switch"); }
            public void Stop(RobloxInstance i) { Calls.Add("stop"); }
            public void Play() { }
            public void StopAll() { }
            public void ShowMainWindow() { }
            public bool CanPlay { get { return true; } }
            public MiniBarStats GetStats()
            {
                MiniBarStats s = new MiniBarStats();
                s.Running = 3; s.Recommended = 6; s.HardMax = 8; s.CpuPercent = 22; s.UsedRamPercent = 48; s.FreeRamGb = 9.2; s.TotalRamGb = 16;
                return s;
            }
            public void ExitLauncher() { }

            public bool TeleportBusy { get { return Busy; } }
            public string TeleportStatusText { get { return Busy ? "Waiting for the friends list to load... 8 s" : ""; } }
            public RobloxInstance TeleportSource { get { return Source; } }
            public TeleportResult LastTeleportResult { get { return Last; } }
            public RobloxInstance LastTeleportSource { get { return LastSource; } }
            public DateTime LastTeleportUtc { get { return LastUtc; } }

            public int TeleportTargetsStamp { get { return _stampSeed++; } }   // always "changed": the fake rebuilds every time
            private int _stampSeed;
            public IList<RobloxInstance> TeleportTargets(RobloxInstance source)
            {
                List<RobloxInstance> l = new List<RobloxInstance>();
                foreach (RobloxInstance i in All)
                    if (!ReferenceEquals(i, source) && i.StatusKind == "Running" && !string.IsNullOrEmpty(i.AccountName)) l.Add(i);
                return l;
            }

            public string TeleportBlockReason(RobloxInstance source, RobloxInstance target)
            {
                if (ForcedReason != null) return ForcedReason;
                if (Busy) return "A teleport is already running (one at a time). Wait for it to finish or press Cancel.";
                if (source.StatusKind != "Running") return "Start this instance and wait for the green Running status first (status: " + source.Status + ").";
                if (target == null) return TeleportTargets(source).Count == 0 ? "Needs a second instance that is Running (green) and was started from a stored account." : "Pick the instance to teleport to.";
                return null;
            }

            public void StartTeleport(RobloxInstance source, RobloxInstance target) { Calls.Add("start:" + source.AccountName + "->" + target.AccountName); }
            public void CancelTeleport() { Calls.Add("cancel"); }
        }

        private static RobloxInstance Inst(int id, string name, string status, string account)
        {
            RobloxInstance r = new RobloxInstance();
            r.Id = id; r.Name = name; r.Status = status; r.AccountName = account;
            r.Pid = System.Diagnostics.Process.GetCurrentProcess().Id;
            r.StartedUtc = DateTime.UtcNow.AddMinutes(-12);
            r.MemoryMb = 1450;
            r.Uptime = "12m 3s";
            return r;
        }

        private static void Check(string name, bool ok, string detail)
        {
            _checks++;
            if (!ok) _failed++;
            Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name + (string.IsNullOrEmpty(detail) ? "" : "  [" + detail + "]"));
        }

        private static void Pump(int ms)
        {
            DispatcherFrame f = new DispatcherFrame();
            DispatcherTimer t = new DispatcherTimer(DispatcherPriority.Normal);
            t.Interval = TimeSpan.FromMilliseconds(ms);
            t.Tick += delegate { t.Stop(); f.Continue = false; };
            t.Start();
            Dispatcher.PushFrame(f);
        }

        private static void Save(Visual v, double w, double h, string file)
        {
            FrameworkElement fe = v as FrameworkElement;
            if (fe != null)
            {
                fe.Measure(new Size(w, h));
                fe.Arrange(new Rect(0, 0, w, h));
                fe.UpdateLayout();
            }
            RenderTargetBitmap rtb = new RenderTargetBitmap((int)Math.Ceiling(w * 2), (int)Math.Ceiling(h * 2), 192, 192, PixelFormats.Pbgra32);
            rtb.Render(v);
            PngBitmapEncoder enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (FileStream fs = File.Create(Path.Combine(_out, file))) enc.Save(fs);
        }

        [STAThread]
        public static int Main(string[] args)
        {
            _out = args.Length > 0 ? args[0] : Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)), "shots_teleport");
            Directory.CreateDirectory(_out);
            Application app = new Application();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            app.DispatcherUnhandledException += delegate(object s, DispatcherUnhandledExceptionEventArgs e)
            {
                _exceptions++;
                Console.WriteLine("UNHANDLED: " + e.Exception);
                e.Handled = true;
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e) { _exceptions++; Console.WriteLine("DOMAIN UNHANDLED: " + e.ExceptionObject); };

            try
            {
                RunAll();
            }
            catch (Exception ex)
            {
                _exceptions++;
                Console.WriteLine("SCRIPT EXCEPTION: " + ex);
            }
            Console.WriteLine();
            Console.WriteLine("RESULT: " + (_checks - _failed) + " checks passed, " + _failed + " failed, " + _exceptions + " exceptions. Screenshots: " + _out);
            Environment.Exit(_failed == 0 && _exceptions == 0 ? 0 : 1);
            return 0;
        }

        private static void RunAll()
        {
            // ------------------------------------------------------------------ state matrix (TeleportCardRow)
            foreach (bool dark in new bool[] { false, true })
            {
                Theme.Apply(dark);
                string tag = dark ? "dark" : "light";
                StackPanel col = new StackPanel();
                col.Background = Theme.Bg;
                col.Margin = new Thickness(0);
                List<TeleportCardRow> rows = new List<TeleportCardRow>();
                List<FakeHost> hosts = new List<FakeHost>();

                Func<string, RobloxInstance, FakeHost, UIElement> addCase = delegate(string title, RobloxInstance inst, FakeHost h)
                {
                    TeleportCardRow row = new TeleportCardRow(h, inst);
                    rows.Add(row);
                    hosts.Add(h);
                    StackPanel body = new StackPanel();
                    body.Children.Add(UiUtil.Label(title, 12, true, Theme.Accent));
                    body.Children.Add(row);
                    Card c = new Card();
                    c.Padding = new Thickness(20);
                    c.Margin = new Thickness(12, 8, 12, 4);
                    c.Child = body;
                    col.Children.Add(c);
                    return c;
                };

                RobloxInstance a = Inst(1, "Instance 1", "Running", "alice_77");
                RobloxInstance b = Inst(2, "Instance 2", "Running", "kugixoxo");
                RobloxInstance c3 = Inst(3, "Instance 3", "Running", "cutiewutie87x");
                RobloxInstance stopped = Inst(4, "Instance 4", "Stopped", "dave");
                RobloxInstance starting = Inst(5, "Instance 5", "Starting", "erin");

                FakeHost h1 = new FakeHost(); h1.All.AddRange(new RobloxInstance[] { stopped, a, b });
                addCase("1. Disabled: this instance is not running", stopped, h1);
                FakeHost h2 = new FakeHost(); h2.All.AddRange(new RobloxInstance[] { a, starting });
                addCase("2. Disabled: no other Running instance", a, h2);
                FakeHost h3 = new FakeHost(); h3.All.AddRange(new RobloxInstance[] { a, b, c3 });
                addCase("3. Enabled: two possible targets", a, h3);
                FakeHost h4 = new FakeHost(); h4.All.AddRange(new RobloxInstance[] { a, b, c3 }); h4.Busy = true; h4.Source = a;
                addCase("4. Busy: this instance is teleporting (spinner + Cancel)", a, h4);
                FakeHost h5 = new FakeHost(); h5.All.AddRange(new RobloxInstance[] { a, b, c3 }); h5.Busy = true; h5.Source = b;
                addCase("5. Disabled: another instance is teleporting", a, h5);
                FakeHost h6 = new FakeHost(); h6.All.AddRange(new RobloxInstance[] { a, b }); h6.Last = new TeleportResult(TeleportStatus.Success, "Teleported (the scene changed clearly)."); h6.LastSource = a; h6.LastUtc = DateTime.UtcNow;
                addCase("6. Result: green Teleported", a, h6);
                FakeHost h7 = new FakeHost(); h7.All.AddRange(new RobloxInstance[] { a, b }); h7.Last = new TeleportResult(TeleportStatus.Likely, "Clicked Teleport and the friends panel closed, but the scene did not change clearly; could not confirm."); h7.LastSource = a; h7.LastUtc = DateTime.UtcNow;
                addCase("7. Result: amber Clicked - unconfirmed", a, h7);
                FakeHost h8 = new FakeHost(); h8.All.AddRange(new RobloxInstance[] { a, b }); h8.Last = new TeleportResult(TeleportStatus.Failed, "Target is not in the friends list, is offline, or has not loaded into the game yet (no matching row for the username). Both accounts must be friends in Adopt Me."); h8.LastSource = a; h8.LastUtc = DateTime.UtcNow;
                addCase("8. Result: red failure message (long)", a, h8);
                FakeHost h9 = new FakeHost(); h9.All.AddRange(new RobloxInstance[] { a, b }); h9.Last = new TeleportResult(TeleportStatus.Cancelled, "Cancelled."); h9.LastSource = a; h9.LastUtc = DateTime.UtcNow;
                addCase("9. Result: cancelled", a, h9);

                Window w = new Window();
                w.Width = 980; w.Height = 1500; w.Left = -5000; w.Top = 0; w.WindowStyle = WindowStyle.None; w.ShowInTaskbar = false;
                w.Background = Theme.Bg;
                w.Content = col;
                w.Resources.MergedDictionaries.Count.ToString();
                ThemedPopups.Install(w.Resources);
                w.Show();
                foreach (TeleportCardRow r in rows) r.Refresh();
                Pump(300);
                double height = 0;
                col.Measure(new Size(980, double.PositiveInfinity));
                height = col.DesiredSize.Height;
                Save(col, 980, height, "teleport_matrix_" + tag + ".png");

                if (!dark)
                {
                    // Assertions on states (once).
                    Func<int, Button> goOf = delegate(int i) { return FindButton(rows[i], "_Teleport"); };
                    Check("1 stopped: Teleport disabled", !goOf(0).IsEnabled, "tooltip=" + Tip(goOf(0)));
                    Check("1 stopped: tooltip explains why", Tip(goOf(0)).IndexOf("Running", StringComparison.OrdinalIgnoreCase) >= 0, "");
                    Check("2 no other running: Teleport disabled with reason", !goOf(1).IsEnabled && Tip(goOf(1)).IndexOf("second instance", StringComparison.OrdinalIgnoreCase) >= 0, Tip(goOf(1)));
                    Check("3 enabled with 2 targets, first selected", goOf(2).IsEnabled && FindCombo(rows[2]).Items.Count == 2 && FindCombo(rows[2]).SelectedIndex == 0, "items=" + FindCombo(rows[2]).Items.Count);
                    Check("3 targets are listed by username", FindCombo(rows[2]).Items[0].ToString() == "kugixoxo" && FindCombo(rows[2]).Items[1].ToString() == "cutiewutie87x", FindCombo(rows[2]).Items[0] + ", " + FindCombo(rows[2]).Items[1]);
                    Check("3 enabled tooltip carries the friends/automation note", Tip(goOf(2)).IndexOf("friends in Adopt Me", StringComparison.OrdinalIgnoreCase) >= 0, "");
                    Check("4 busy: Cancel visible and Teleport disabled", FindButton(rows[3], "_Cancel").Visibility == Visibility.Visible && !goOf(3).IsEnabled, "");
                    Check("5 another busy: Cancel hidden, Teleport disabled, reason", FindButton(rows[4], "_Cancel").Visibility != Visibility.Visible && !goOf(4).IsEnabled && Tip(goOf(4)).IndexOf("already running", StringComparison.OrdinalIgnoreCase) >= 0, Tip(goOf(4)));
                    Check("6-9 result chips visible", ChipVisible(rows[5]) && ChipVisible(rows[6]) && ChipVisible(rows[7]) && ChipVisible(rows[8]), "");
                    Check("chip text success/unconfirmed", ChipText(rows[5]) == "Teleported" && ChipText(rows[6]) == "Clicked - unconfirmed", ChipText(rows[5]) + " / " + ChipText(rows[6]));
                    Check("chip text failure is the specific message", ChipText(rows[7]).StartsWith("Target is not in the friends list"), ChipText(rows[7]));
                    // Click the enabled button and see the host receive the call.
                    goOf(2).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check("clicking Teleport calls the host with source and the selected target", h3.Calls.Contains("start:alice_77->kugixoxo"), string.Join(",", h3.Calls.ToArray()));
                    FindButton(rows[3], "_Cancel").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check("clicking Cancel calls the host", h4.Calls.Contains("cancel"), "");
                    // Chip expiry
                    h6.LastUtc = DateTime.UtcNow.AddSeconds(-(TeleportCardRow.ChipSeconds + 2));
                    rows[5].Refresh();
                    Check("result chip disappears after " + TeleportCardRow.ChipSeconds + " s", !ChipVisible(rows[5]), "");
                }
                w.Close();
            }

            // ------------------------------------------------------------------ mini bar with a fake host
            foreach (bool dark in new bool[] { false, true })
            {
                Theme.Apply(dark);
                string tag = dark ? "dark" : "light";
                FakeHost host = new FakeHost();
                RobloxInstance a = Inst(1, "Instance 1", "Running", "alice_77");
                RobloxInstance b = Inst(2, "Instance 2", "Running", "kugixoxo");
                RobloxInstance c = Inst(3, "Instance 3", "Running", "cutiewutie87x");
                RobloxInstance d = Inst(4, "Instance 4", "Starting", "erin");
                host.All.AddRange(new RobloxInstance[] { a, b, c, d });
                MiniBar mini = new MiniBar(host);
                mini.Left = -5000; mini.Top = 0; mini.Width = 700; mini.Height = 48;
                mini.Show();
                Pump(300);

                string[] states = new string[] { "idle", "busy", "success", "unconfirmed", "failed" };
                foreach (string st in states)
                {
                    host.Busy = false; host.Source = null; host.Last = null; host.LastSource = null;
                    if (st == "busy") { host.Busy = true; host.Source = a; }
                    if (st == "success") { host.Last = new TeleportResult(TeleportStatus.Success, "Teleported (the scene changed clearly)."); host.LastSource = a; host.LastUtc = DateTime.UtcNow; }
                    if (st == "unconfirmed") { host.Last = new TeleportResult(TeleportStatus.Likely, "Clicked Teleport; could not confirm."); host.LastSource = a; host.LastUtc = DateTime.UtcNow; }
                    if (st == "failed") { host.Last = new TeleportResult(TeleportStatus.Failed, "Target is offline / not in the game yet."); host.LastSource = a; host.LastUtc = DateTime.UtcNow; }
                    mini.RefreshTeleport();
                    Pump(120);
                    Save((Visual)mini.Content, 700, 48, "minibar_" + st + "_" + tag + ".png");
                }

                // Open the chip menu of instance 1 and render the popup.
                host.Busy = false; host.Source = null; host.Last = null;
                mini.RefreshNow();
                Pump(120);
                FieldInfo fi = typeof(MiniBar).GetField("_chips", BindingFlags.NonPublic | BindingFlags.Instance);
                System.Collections.IList chips = (System.Collections.IList)fi.GetValue(mini);
                object chip0 = chips[0];
                Border root = (Border)chip0.GetType().GetField("Root").GetValue(chip0);
                MouseButtonEventArgs up = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left);
                up.RoutedEvent = UIElement.MouseLeftButtonUpEvent;
                up.Source = root;
                root.RaiseEvent(up);
                Pump(400);
                bool saved = false;
                string menuText = "";
                foreach (PresentationSource src in PresentationSource.CurrentSources)
                {
                    FrameworkElement fe = src.RootVisual as FrameworkElement;
                    if (fe != null && fe.GetType().Name == "PopupRoot" && fe.ActualWidth > 20)
                    {
                        Save(fe, fe.ActualWidth, fe.ActualHeight, "minibar_menu_" + tag + ".png");
                        saved = true;
                        menuText = DescribeMenu(fe);
                    }
                }
                Check("mini bar chip menu opened (" + tag + ") and rendered", saved, menuText);
                if (!dark) Check("mini bar menu lists 'Teleport to <user>' for the OTHER running instances only, not the Starting one and not itself",
                    menuText.Contains("Teleport to kugixoxo") && menuText.Contains("Teleport to cutiewutie87x") && !menuText.Contains("alice_77") && !menuText.Contains("erin"), menuText);
                // close popups
                foreach (PresentationSource src in PresentationSource.CurrentSources)
                {
                    FrameworkElement fe = src.RootVisual as FrameworkElement;
                    if (fe != null && fe.GetType().Name == "PopupRoot") { try { Keyboard.Focus(null); } catch (Exception) { } }
                }
                mini.Close();
            }

            // ------------------------------------------------------------------ real MainWindow with fake instances
            Theme.Apply(false);
            MainWindow.SkipAdoption = true;
            MainWindow win = new MainWindow();
            win.Left = -5000; win.Top = 0;
            win.Show();
            Pump(500);
            ObservableCollection<RobloxInstance> coll = (ObservableCollection<RobloxInstance>)win.Instances;
            RobloxInstance m1 = Inst(101, "Instance 1", "Running", "alice_77");
            RobloxInstance m2 = Inst(102, "Instance 2", "Running", "kugixoxo");
            RobloxInstance m3 = Inst(103, "Instance 3", "Stopped", "dave");
            coll.Add(m1); coll.Add(m2); coll.Add(m3);
            Pump(1500);   // real timer ticks run too
            Check("MainWindow: Teleporter exists", win.Teleporter != null, "");
            IList<RobloxInstance> tg = win.TeleportTargets(m1);
            Check("MainWindow: targets of #101 = only #102 (Running, with username)", tg.Count == 1 && ReferenceEquals(tg[0], m2), "count=" + tg.Count);
            Check("MainWindow: block reason null for 101 -> 102 (quick)", win.TeleportBlockReason(m1, m2) == null, win.TeleportBlockReason(m1, m2));
            Check("MainWindow: reason for a Stopped source", win.TeleportBlockReason(m3, m2) != null, win.TeleportBlockReason(m3, m2));
            Check("MainWindow: reason for target == source", win.TeleportBlockReason(m1, m1) != null, win.TeleportBlockReason(m1, m1));
            Check("MainWindow: reason when no target is selected and none exists", win.TeleportBlockReason(m3, null) != null, win.TeleportBlockReason(m3, null));
            // Start with unresolvable windows -> the card shows a red chip and nothing is clicked.
            win.Teleporter.ResolveWindow = delegate(RobloxInstance i) { return IntPtr.Zero; };   // never let the fake instances resolve to a real window
            win.StartTeleport(m1, m2);   // fake instances have no Roblox window: refused before any click
            Pump(300);
            Check("MainWindow: StartTeleport on instances without a game window is refused with a red result", win.LastTeleportResult != null && win.LastTeleportResult.Status == TeleportStatus.Failed, win.LastTeleportResult == null ? "null" : win.LastTeleportResult.Message);
            Check("MainWindow: not busy after refusal", !win.TeleportBusy, "");

            // Card screenshots in light and dark with the failure chip shown.
            FieldInfo cf = typeof(MainWindow).GetField("_instanceList", BindingFlags.NonPublic | BindingFlags.Instance);
            StackPanel list = (StackPanel)cf.GetValue(win);
            foreach (bool dark in new bool[] { false, true })
            {
                Theme.Apply(dark);
                Pump(400);
                list.Measure(new Size(940, double.PositiveInfinity));
                Save(list, 940, list.DesiredSize.Height, "maincards_" + (dark ? "dark" : "light") + ".png");
                { Panel rp = win.Content as Panel; if (rp != null) rp.Background = Theme.Bg; Control rc = win.Content as Control; if (rc != null) rc.Background = Theme.Bg; }   // RTB has no window background
                Save((Visual)win.Content, win.ActualWidth, win.ActualHeight, "mainwindow_" + (dark ? "dark" : "light") + ".png");
                // Regression: labels built once (title, "Dark mode") must follow the theme.
                foreach (string want in new string[] { "Cookie Jar", "Dark mode", "Profiles" })
                {
                    TextBlock tb = FindText(win, want);
                    SolidColorBrush fb = tb == null ? null : tb.Foreground as SolidColorBrush;
                    Check("theme " + (dark ? "dark" : "light") + ": '" + want + "' uses the current Text colour",
                        fb != null && fb.Color == Theme.Text.Color,
                        tb == null ? "not found" : (fb == null ? "non-solid" : fb.Color + " vs " + Theme.Text.Color + " frozen=" + fb.IsFrozen + " same=" + ReferenceEquals(fb, Theme.Text)));
                }
            }

            // ------------------------------------------------------------------ leak regression: Advanced rows rebuilt on every
            // instance change used to stay registered in the window's theme label lists forever (~2 MB per change).
            {
                System.Collections.ICollection muted = (System.Collections.ICollection)Fld(win, "_mutedTexts");
                System.Collections.ICollection primary = (System.Collections.ICollection)Fld(win, "_primaryTexts");
                int before = muted.Count + primary.Count;
                for (int round = 0; round < 3; round++)
                {
                    List<RobloxInstance> extra = new List<RobloxInstance>();
                    for (int k = 0; k < 6; k++) { RobloxInstance x = Inst(900 + round * 10 + k, "Extra " + k, "Running", "extra" + k); extra.Add(x); coll.Add(x); }
                    Pump(100);
                    foreach (RobloxInstance x in extra) coll.Remove(x);
                    Pump(100);
                }
                int after = muted.Count + primary.Count;
                Check("leak: theme label lists do not grow when instances come and go", after == before, before + " -> " + after);
            }

            // ------------------------------------------------------------------ Cash out card button, chips and history
            {
                MethodInfo rec = typeof(MainWindow).GetMethod("RecordCashOut", BindingFlags.NonPublic | BindingFlags.Instance);
                Check("Cash out: disabled with a reason for a Stopped instance, enabled (no reason) for a Running one", win.CashOutBlockReason(m3) != null && win.CashOutBlockReason(m1) == null, win.CashOutBlockReason(m3));
                CashOutHistory.Clear(m1.Id);
                for (int i = 0; i < 4; i++)
                {
                    CashOutEvent ce = new CashOutEvent(); ce.Time = DateTime.Now.AddMinutes(-4 + i); ce.Manual = i % 2 == 0; ce.Method = "SendInput"; ce.ClickPoint = new System.Drawing.Point(546, 371);
                    ce.Kind = i == 1 ? CashOutKind.Failed : CashOutKind.Success; ce.Message = i == 1 ? "CASH OUT button not visible in this window." : "";
                    CashOutHistory.Add(m1.Id, ce);
                }
                Pump(1300);
                TextBlock histToggle = FindFirst<TextBlock>(list, "Click history (4)  \u25B8");
                Check("Cash out: card has the expandable 'Click history' toggle", histToggle != null, "");
                if (histToggle != null)
                {
                    MouseButtonEventArgs up = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left); up.RoutedEvent = UIElement.MouseLeftButtonUpEvent; up.Source = histToggle;
                    histToggle.RaiseEvent(up);
                }
                CashOutResult[] samples = new CashOutResult[] {
                    new CashOutResult(CashOutKind.Success, "CASH OUT clicked and the button is gone."),
                    new CashOutResult(CashOutKind.Unconfirmed, "Clicked, but the window could not be captured afterwards."),
                    new CashOutResult(CashOutKind.Failed, "CASH OUT button not visible in this window.") };
                string[] sn = new string[] { "ok", "amber", "red" };
                foreach (bool dark in new bool[] { false, true })
                {
                    Theme.Apply(dark);
                    for (int s = 0; s < samples.Length; s++)
                    {
                        rec.Invoke(win, new object[] { m1, samples[s], true });
                        Pump(500);
                        list.Measure(new Size(940, double.PositiveInfinity));
                        Save(list, 940, list.DesiredSize.Height, "cashout_card_" + sn[s] + "_" + (dark ? "dark" : "light") + ".png");
                        if (s == 0) Check("Cash out: green 'Cashed out' chip text on the card (" + (dark ? "dark" : "light") + ")", AllText(list).Contains("Cashed out") && AllText(list).Contains("Click history (4)"), "");
                        if (s == 1) Check("Cash out: amber chip text", AllText(list).Contains("Clicked - unconfirmed"), "");
                        if (s == 2) Check("Cash out: red chip text", AllText(list).Contains("CASH OUT button not visible in this window."), "");
                    }
                }
                Theme.Apply(false);
            }

            // ------------------------------------------------------------------ input layer UI: Advanced section, card keep-alive row, diagnostics dialog
            win.KeepAlive.WiggleHook = delegate(IntPtr h, bool k) { ClickReport cr = new ClickReport(); cr.Delivered = true; cr.TechUsed = ClickTech.SendInput; return cr; };   // never send real input from the scratch host
            CheckBox master = (CheckBox)Fld(win, "_autoMaster");
            CheckBox ksCheck = (CheckBox)Fld(win, "_ksCheck");
            TextBox ksMin = (TextBox)Fld(win, "_ksMinBox");
            ComboBox clickCombo = (ComboBox)Fld(win, "_clickCombo");
            ComboBox diagCombo = (ComboBox)Fld(win, "_diagCombo");
            Button diagButton = (Button)Fld(win, "_diagButton");
            Expander adv = (Expander)Fld(win, "_advancedExpander");
            Check("input UI: Click method defaults to Auto", clickCombo.SelectedItem != null && clickCombo.SelectedItem.ToString() == "Auto" && clickCombo.Items.Count == 3, clickCombo.SelectedItem == null ? "null" : clickCombo.SelectedItem.ToString());
            Check("input UI: keep sessions checkbox defaults ON, interval 4", ksCheck.IsChecked == true && ksMin.Text == "4", ksMin.Text);
            Check("keep-alive inactive while the auto-click master switch is off", !win.KeepAliveActive && !win.KeepAlive.Enabled, "");
            master.IsChecked = true;
            Pump(1300);
            Check("keep-alive becomes active with the master switch on", win.KeepAliveActive && win.KeepAlive.Enabled && win.KeepAlive.IntervalMinutes == 4, "");
            ksMin.Text = "99"; typeof(MainWindow).GetMethod("CommitKeepMinutes", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(win, null);
            Check("keep-alive interval 99 clamps to 15 in the UI", ksMin.Text == "15" && win.KeepAlive.IntervalMinutes == 15, ksMin.Text);
            ksMin.Text = "0"; typeof(MainWindow).GetMethod("CommitKeepMinutes", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(win, null);
            Check("keep-alive interval 0 clamps to 1 in the UI", ksMin.Text == "1" && win.KeepAlive.IntervalMinutes == 1, ksMin.Text);
            ksMin.Text = "4"; typeof(MainWindow).GetMethod("CommitKeepMinutes", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(win, null);
            Check("diagnostics dropdown lists only the Running instances (2) and the button is enabled", diagCombo.Items.Count == 2 && diagButton.IsEnabled, "items=" + diagCombo.Items.Count);
            win.KeepAlive.SetStatsForTest(m1.Pid, DateTime.UtcNow.AddMinutes(-2), DateTime.UtcNow.AddMinutes(-15));
            Pump(100); TickNow(win);   // run the 1 s refresh deterministically (a timer wait was flaky under load)
            string cardText = AllText(list);
            Check("card shows 'Last keep-alive: 2m ago' and 'just now'-style text", cardText.Contains("Last keep-alive: 2m ago"), "");
            Check("card shows the warning chip after 12+ minutes without confirmed input", cardText.Contains("No confirmed input for 15 min"), "");
            adv.IsExpanded = true;
            Pump(500);
            // healthy state first (keep-alive 20 s ago, first seen 3 min ago), light theme only
            win.KeepAlive.SetStatsForTest(m1.Pid, DateTime.UtcNow.AddSeconds(-20), DateTime.UtcNow.AddMinutes(-3));
            Pump(100); TickNow(win);   // run the 1 s refresh deterministically (a timer wait was flaky under load)
            list.Measure(new Size(940, double.PositiveInfinity));
            Save(list, 940, list.DesiredSize.Height, "cards_keepalive_ok_light.png");
            Check("healthy card: no warning chip, text Last keep-alive: 20s ago", !AllText(list).Contains("No confirmed input") && AllText(list).Contains("Last keep-alive: 20s ago"), "");
            win.KeepAlive.SetStatsForTest(m1.Pid, DateTime.UtcNow.AddMinutes(-2), DateTime.UtcNow.AddMinutes(-15));
            Pump(100); TickNow(win);   // run the 1 s refresh deterministically (a timer wait was flaky under load)
            foreach (bool dark in new bool[] { false, true })
            {
                Theme.Apply(dark);
                Pump(500);
                list.Measure(new Size(940, double.PositiveInfinity));
                Save(list, 940, list.DesiredSize.Height, "cards_keepalive_" + (dark ? "dark" : "light") + ".png");
                UIElement advBody = (UIElement)adv.Content;
                advBody.Measure(new Size(900, double.PositiveInfinity));
                Save(advBody, 900, advBody.DesiredSize.Height, "advanced_input_" + (dark ? "dark" : "light") + ".png");
                Check("Advanced: 'Friends list wait (s)' field present, default 25 (" + (dark ? "dark" : "light") + ")", AllText(advBody).Contains("Friends list wait (s, 5-60)") && ((TextBox)Fld(win, "_listWaitBox")).Text == "25", "");
                DiagResult fakeRes = new DiagResult();
                fakeRes.Folder = @"C:\Users\you\AppData\Local\RobloxMultiLauncher\diag\20261009-164300";
                fakeRes.Summary = "- Focus: OK, game window came to the front.\r\n- Capture: PrintWindow 1000x578, matches the client area.\r\n- FRIENDS button found at client (956,426) = screen (994,477), 95% across / 73% down.\r\n- Mouse move test: delivered via SendInput.\r\n- SendInput: input accepted, but the game did NOT react (no panel change).\r\n- mouse_event: WORKS - the FRIENDS panel toggled.\r\n- PostMessage: input accepted, but the game did NOT react (no panel change).\r\n- RESULT: working click method(s): mouse_event.\r\n";
                DiagResultDialog dd = new DiagResultDialog(win, fakeRes);
                dd.Left = -5000; dd.Top = 0; dd.Show();
                Pump(400);
                Save(dd, dd.ActualWidth, dd.ActualHeight, "diag_dialog_" + (dark ? "dark" : "light") + ".png");
                dd.Close();
            }

            // 20 theme toggles on the real MainWindow (Advanced expanded, keep-alive row, warning chip)
            int mwOk = 0; int mwExc = _exceptions;
            for (int i = 0; i < 20; i++)
            {
                try
                {
                    bool dk = (i % 2) == 0;
                    Theme.Apply(dk);
                    Pump(120);
                    Color wantText = Theme.Text.Color;
                    if (((SolidColorBrush)ksCheck.Foreground).Color != wantText) { _exceptions++; Console.WriteLine("ksCheck colour mismatch at " + i); }
                    if (((SolidColorBrush)ksMin.Foreground).Color != wantText) { _exceptions++; Console.WriteLine("ksMin colour mismatch at " + i); }
                    mwOk++;
                }
                catch (Exception ex) { _exceptions++; Console.WriteLine("MainWindow toggle " + i + " exception: " + ex); }
            }
            Check("20 theme toggles on the real MainWindow with the new Advanced controls and card rows: zero exceptions, colours follow the theme", mwOk == 20 && _exceptions == mwExc, "exceptions=" + (_exceptions - mwExc));
            master.IsChecked = false;
            _exceptions = 0;
            // ------------------------------------------------------------------ 20 theme toggles on everything
            _exceptions = 0;
            FakeHost th = new FakeHost();
            th.All.AddRange(new RobloxInstance[] { m1, m2 });
            th.Last = new TeleportResult(TeleportStatus.Failed, "Teleport didn't happen."); th.LastSource = m1;
            MiniBar mb = new MiniBar(th);
            mb.Left = -5000; mb.Top = 100; mb.Show();
            TeleportCardRow tr = new TeleportCardRow(th, m1);
            Window tw = new Window(); tw.Left = -5000; tw.Width = 900; tw.Height = 300; tw.Content = tr; tw.Show();
            int ok = 0;
            for (int i = 0; i < 20; i++)
            {
                bool dark = (i % 2) == 0;
                try
                {
                    Theme.Apply(dark);
                    tr.ApplyTheme(); tr.Refresh();
                    mb.RefreshNow(); mb.RefreshTeleport();
                    Pump(60);
                    // brush correctness: the label colour follows the theme
                    Color want = Theme.TextMuted.Color;
                    ok++;
                    if (!dark != !Theme.IsDark) { _exceptions++; }
                    TextBlock lbl = FindFirst<TextBlock>(tr, "Teleport to");
                    if (lbl == null || ((SolidColorBrush)lbl.Foreground).Color != want) { _exceptions++; Console.WriteLine("label colour mismatch at toggle " + i); }
                }
                catch (Exception ex)
                {
                    _exceptions++;
                    Console.WriteLine("toggle " + i + " exception: " + ex);
                }
            }
            Check("20 theme toggles (MainWindow cards + mini bar + teleport row): zero exceptions, colours follow the theme", ok == 20 && _exceptions == 0, "exceptions=" + _exceptions);
            tw.Close(); mb.Close();
            // Remove the tray icon this scratch MainWindow created, then close.
            try
            {
                object tray = typeof(MainWindow).GetField("_tray", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(win);
                if (tray != null) ((IDisposable)tray).Dispose();
            }
            catch (Exception) { }
            win.Close();
        }

        private static object Fld(object o, string name)
        {
            return o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(o);
        }

        private static string AllText(DependencyObject root)
        {
            StringBuilder sb = new StringBuilder();
            AllTextInto(root, sb);
            return sb.ToString();
        }

        private static void AllTextInto(DependencyObject o, StringBuilder sb)
        {
            if (o == null) return;
            UIElement ue = o as UIElement;
            if (ue != null && ue.Visibility != Visibility.Visible) return;   // collapsed elements are not on screen
            TextBlock tb = o as TextBlock;
            if (tb != null) sb.Append(tb.Text).Append(" | ");
            int n = VisualTreeHelper.GetChildrenCount(o);
            for (int i = 0; i < n; i++) AllTextInto(VisualTreeHelper.GetChild(o, i), sb);
        }

        private static string DescribeMenu(FrameworkElement root)
        {
            List<string> items = new List<string>();
            Collect(root, items);
            return string.Join(" | ", items.ToArray());
        }

        private static void Collect(DependencyObject o, List<string> items)
        {
            if (o == null) return;
            MenuItem mi = o as MenuItem;
            if (mi != null) items.Add((mi.Header ?? "").ToString() + (mi.IsEnabled ? "" : " (disabled)"));
            int n = VisualTreeHelper.GetChildrenCount(o);
            for (int i = 0; i < n; i++) Collect(VisualTreeHelper.GetChild(o, i), items);
        }

        private static string Tip(Button b) { return b.ToolTip == null ? "" : b.ToolTip.ToString(); }

        private static Button FindButton(DependencyObject root, string content)
        {
            if (root == null) return null;
            Button b = root as Button;
            if (b != null && b.Content != null && b.Content.ToString() == content) return b;
            foreach (object ch in LogicalTreeHelper.GetChildren(root))
            {
                DependencyObject d = ch as DependencyObject;
                if (d != null) { Button r = FindButton(d, content); if (r != null) return r; }
            }
            return null;
        }

        private static void TickNow(MainWindow win)
        {
            typeof(MainWindow).GetMethod("OnTimerTick", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(win, new object[] { null, EventArgs.Empty });
            Pump(50);
        }

        private static TextBlock FindText(DependencyObject root, string text)
        {
            if (root == null) return null;
            TextBlock t = root as TextBlock;
            if (t != null && t.Text == text) return t;
            foreach (object ch in LogicalTreeHelper.GetChildren(root))
            {
                DependencyObject d = ch as DependencyObject;
                if (d != null) { TextBlock r = FindText(d, text); if (r != null) return r; }
            }
            return null;
        }

        private static ComboBox FindCombo(DependencyObject root)
        {
            ComboBox cb = root as ComboBox;
            if (cb != null) return cb;
            foreach (object ch in LogicalTreeHelper.GetChildren(root))
            {
                DependencyObject d = ch as DependencyObject;
                if (d != null) { ComboBox r = FindCombo(d); if (r != null) return r; }
            }
            return null;
        }

        private static T FindFirst<T>(DependencyObject root, string text) where T : class
        {
            TextBlock tb = root as TextBlock;
            if (tb != null && tb.Text == text) return tb as T;
            foreach (object ch in LogicalTreeHelper.GetChildren(root))
            {
                DependencyObject d = ch as DependencyObject;
                if (d != null) { T r = FindFirst<T>(d, text); if (r != null) return r; }
            }
            return null;
        }

        private static bool ChipVisible(TeleportCardRow row)
        {
            foreach (object ch in row.Children)
            {
                Border b = ch as Border;
                if (b != null) return b.Visibility == Visibility.Visible;
            }
            return false;
        }

        private static string ChipText(TeleportCardRow row)
        {
            foreach (object ch in row.Children)
            {
                Border b = ch as Border;
                if (b != null)
                {
                    StackPanel sp = (StackPanel)b.Child;
                    return ((TextBlock)sp.Children[1]).Text;
                }
            }
            return "";
        }
    }
}
