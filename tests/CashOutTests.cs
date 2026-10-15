// CashOutTests.cs - manual "Cash out" card button (ManualCashOut) against a fake game window that shows the CASH OUT
// reference screenshot. Uses FakeInputBackend (no real mouse input). C# 5.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace RbxMulti
{
    public static class CashOutTests
    {
        private enum CashMode { Disappear, Ignore }

        private sealed class FakeCash : Form
        {
            private readonly Bitmap _with, _without;
            public readonly Rectangle Button;
            public volatile bool Gone;
            public CashMode Mode = CashMode.Disappear;
            public volatile bool ButtonVisible = true;
            public int Clicks;

            public FakeCash(Bitmap cash, Rectangle button)
            {
                Button = button;
                _with = new Bitmap(cash);
                _without = new Bitmap(cash);
                using (Graphics g = Graphics.FromImage(_without))
                using (Brush br = new SolidBrush(Color.FromArgb(110, 120, 170)))
                    g.FillRectangle(br, Rectangle.Inflate(button, 14, 14));
                Text = "Fake Roblox cashout window";
                StartPosition = FormStartPosition.Manual;
                Location = new Point(40, 30);
                ClientSize = cash.Size;
                TopMost = true;
                DoubleBuffered = true;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.DrawImage(Gone || !ButtonVisible ? _without : _with, 0, 0, ClientSize.Width, ClientSize.Height);
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                Clicks++;
                if (Button.Contains(e.X, e.Y) && Mode == CashMode.Disappear && ButtonVisible) { Gone = true; Invalidate(); }
                base.OnMouseDown(e);
            }
        }

        private static RobloxInstance Inst(int id, string name, string status, string account)
        {
            RobloxInstance r = new RobloxInstance();
            r.Id = id; r.Name = name; r.Status = status; r.AccountName = account;
            r.Pid = System.Diagnostics.Process.GetCurrentProcess().Id;
            return r;
        }

        public static void Run(string dir, Action<string, bool, string> check, Action<string> info)
        {
            Console.WriteLine();
            Console.WriteLine("=== Part 3: manual CASH OUT (card button) against a fake game window ===");
            using (Bitmap cash = new Bitmap(Path.Combine(dir, "cashout_ref.png")))
            {
                Rectangle? found = CashOutDetector.Find(cash);
                check("cashout_ref.png: CASH OUT detected (fixture sanity)", found.HasValue, found.HasValue ? found.Value.ToString() : "none");
                if (!found.HasValue) return;
                FakeCash form = null;
                ManualResetEvent ready = new ManualResetEvent(false);
                Thread ui = new Thread(delegate()
                {
                    Application.EnableVisualStyles();
                    form = new FakeCash(cash, found.Value);
                    form.Shown += delegate { ready.Set(); };
                    Application.Run(form);
                });
                ui.SetApartmentState(ApartmentState.STA);
                ui.IsBackground = true;
                ui.Start();
                ready.WaitOne(10000);
                Thread.Sleep(400);
                IntPtr hwnd = form.Handle;

                string savedRoot = InputDiagnostics.RootOverride;
                IInputBackend savedB = InputSim.Backend;
                string root = Path.Combine(Path.GetTempPath(), "rbx_cash_" + Guid.NewGuid().ToString("N"));
                InputDiagnostics.RootOverride = root;
                try
                {
                    Func<RobloxInstance, IntPtr> resolve = delegate(RobloxInstance i) { return hwnd; };
                    RobloxInstance inst = Inst(21, "Instance 21", "Running", "cashuser");
                    AntiIdleClicker clicker = new AntiIdleClicker();
                    clicker.Configure(inst.Id, inst.Pid, false, 0, false, 0);
                    List<string> log = new List<string>();
                    Action<string> lg = delegate(string s) { lock (log) { log.Add(s); } };
                    CashOutHistory.Clear(inst.Id);

                    // C1 success: the button disappears after the click
                    InputSim.Backend = InputTests.NewFake(hwnd);
                    InputSim.Method = ClickMethod.Auto;
                    form.Gone = false; form.ButtonVisible = true; form.Mode = CashMode.Disappear; form.Invalidate(); Thread.Sleep(250);
                    CashOutResult r1 = ManualCashOut.Run(inst, clicker, resolve, CancellationToken.None, lg);
                    info("C1: " + r1 + " method=" + r1.Method + " point=" + (r1.ClickPoint.HasValue ? r1.ClickPoint.Value.ToString() : "-") + " " + r1.Millis + " ms");
                    check("C1 button disappears after the click -> Success ('Cashed out' chip), method + click point recorded", r1.Kind == CashOutKind.Success && r1.Chip == "Cashed out" && !string.IsNullOrEmpty(r1.Method) && r1.ClickPoint.HasValue, r1.ToString());
                    Point centre = AntiIdleClicker.CashOutClickPoint(found.Value);
                    check("C1 the click went to the centre of the detected button (within 3 px)", r1.ClickPoint.HasValue && Math.Abs(r1.ClickPoint.Value.X - centre.X) <= 3 && Math.Abs(r1.ClickPoint.Value.Y - centre.Y) <= 3, "point=" + r1.ClickPoint + " centre=" + centre);
                    check("C1 card counter incremented (Clicks: 1) and last method recorded", clicker.GetStats(inst.Id).Clicks == 1 && !string.IsNullOrEmpty(clicker.GetStats(inst.Id).LastMethod), "clicks=" + clicker.GetStats(inst.Id).Clicks);
                    List<CashOutEvent> h1 = CashOutHistory.Get(inst.Id);
                    check("C1 history has the event (manual, success, method, point)", h1.Count == 1 && h1[0].Manual && h1[0].Kind == CashOutKind.Success && h1[0].ClickPoint.HasValue && h1[0].Line().Contains("manual") && h1[0].Line().Contains("cashed out"), h1.Count > 0 ? h1[0].Line() : "none");
                    string joined; lock (log) { joined = string.Join("|", log.ToArray()); }
                    check("C1 the attempt was written to the activity log (log callback)", joined.Contains("[CashOut]") && joined.Contains("Success"), joined);
                    string[] ev = Directory.Exists(root) ? Directory.GetDirectories(root, "cashout_*") : new string[0];
                    string rep = ev.Length == 1 && File.Exists(Path.Combine(ev[0], "report.txt")) ? File.ReadAllText(Path.Combine(ev[0], "report.txt")) : "";
                    check("C1 evidence folder cashout_<time>_<user> with report.txt (method, point, timings, verdict), before.png and after.png", ev.Length == 1 && ev[0].Contains("_cashuser") && rep.Contains("Click method used") && rep.Contains("Click point (client px)") && rep.Contains("Timings") && rep.Contains("Verdict: Success") && File.Exists(Path.Combine(ev[0], "before.png")) && File.Exists(Path.Combine(ev[0], "after.png")), ev.Length == 0 ? "no folder" : ev[0]);

                    // C2 button absent
                    form.Gone = false; form.ButtonVisible = false; form.Invalidate(); Thread.Sleep(250);
                    int clicksBefore = form.Clicks;
                    CashOutResult r2 = ManualCashOut.Run(inst, clicker, resolve, CancellationToken.None, lg);
                    check("C2 button absent -> red 'CASH OUT button not visible in this window', nothing clicked", r2.Kind == CashOutKind.Failed && r2.Message == "CASH OUT button not visible in this window." && form.Clicks == clicksBefore, r2.ToString());
                    check("C2 failure is in the history and the counter did not change", CashOutHistory.Get(inst.Id).Count == 2 && CashOutHistory.Get(inst.Id)[0].Kind == CashOutKind.Failed && clicker.GetStats(inst.Id).Clicks == 1, "");

                    // C3 click ignored by the game
                    form.Gone = false; form.ButtonVisible = true; form.Mode = CashMode.Ignore; form.Invalidate(); Thread.Sleep(250);
                    CashOutResult r3 = ManualCashOut.Run(inst, clicker, resolve, CancellationToken.None, lg);
                    check("C3 click ignored (button stays) -> red 'Click did not register'", r3.Kind == CashOutKind.Failed && r3.Message.StartsWith("Click did not register"), r3.ToString());
                    check("C3 counter unchanged, last problem recorded", clicker.GetStats(inst.Id).Clicks == 1 && !string.IsNullOrEmpty(clicker.GetStats(inst.Id).LastError), "err=" + clicker.GetStats(inst.Id).LastError);

                    // C4 classification of the other outcomes
                    ClickReport cr = new ClickReport(); cr.Delivered = true; cr.Confirmed = true; cr.VerifyRequested = true;
                    check("C4 delivered + confirmed but the verify capture failed -> amber 'Clicked - unconfirmed'", ManualCashOut.Classify(cr, true).Kind == CashOutKind.Unconfirmed && ManualCashOut.Classify(cr, true).Chip == "Clicked - unconfirmed", "");
                    ClickReport cv = new ClickReport(); cv.Delivered = false; cv.ErrorCode = "covered"; cv.Error = "Another window is on top.";
                    check("C4 window covered -> red specific text", ManualCashOut.Classify(cv, false).Kind == CashOutKind.Failed && ManualCashOut.Classify(cv, false).Message.Contains("covered"), ManualCashOut.Classify(cv, false).Message);
                    cv.ErrorCode = "focus";
                    check("C4 focus refused -> red specific text", ManualCashOut.Classify(cv, false).Message.Contains("front"), ManualCashOut.Classify(cv, false).Message);

                    // C5 refusals
                    check("C5 stopped instance -> disabled with an explanatory reason", ManualCashOut.BlockReason(Inst(22, "x", "Stopped", "u"), resolve, false) != null && ManualCashOut.BlockReason(Inst(22, "x", "Stopped", "u"), resolve, false).Contains("Running"), ManualCashOut.BlockReason(Inst(22, "x", "Stopped", "u"), resolve, false));
                    check("C5 running instance without window -> reason", ManualCashOut.BlockReason(inst, delegate(RobloxInstance i) { return IntPtr.Zero; }, true) != null, "");
                    check("C5 running instance with window -> allowed", ManualCashOut.BlockReason(inst, resolve, true) == null, "");
                    bool got = TeleportService.TryAcquireExclusive();
                    CashOutResult r5 = ManualCashOut.Run(inst, clicker, resolve, CancellationToken.None, lg);
                    if (got) TeleportService.ReleaseExclusive();
                    check("C5 one at a time with Teleport: refused while the shared guard is held", got && r5.Kind == CashOutKind.Failed && r5.Message.Contains("already running"), r5.ToString());

                    // C6 history cap
                    for (int i = 0; i < 60; i++) { CashOutEvent e = new CashOutEvent(); e.Time = DateTime.Now.AddSeconds(i); e.Kind = CashOutKind.Failed; e.Message = "m" + i; CashOutHistory.Add(inst.Id, e); }
                    List<CashOutEvent> hh = CashOutHistory.Get(inst.Id);
                    check("C6 history keeps only the last 50 events, newest first", hh.Count == 50 && hh[0].Message == "m59", "count=" + hh.Count);

                    // C7 evidence folders limited to 20
                    for (int i = 0; i < 25; i++) Directory.CreateDirectory(Path.Combine(root, "cashout_20200101-0000" + i.ToString("00") + "_old"));
                    form.Gone = false; form.ButtonVisible = true; form.Mode = CashMode.Disappear; form.Invalidate(); Thread.Sleep(250);
                    InputSim.Backend = InputTests.NewFake(hwnd);
                    CashOutResult r7 = ManualCashOut.Run(inst, clicker, resolve, CancellationToken.None, lg);
                    int folders = Directory.GetDirectories(root, "cashout_*").Length;
                    check("C7 evidence folders are pruned to the newest 20", r7.Kind == CashOutKind.Success && folders <= ManualCashOut.KeepEvidenceFolders && folders >= 1, "success=" + r7.Kind + " folders=" + folders);
                }
                finally
                {
                    InputSim.Backend = savedB;
                    InputDiagnostics.RootOverride = savedRoot;
                    try { form.Invoke(new Action(delegate { form.Close(); })); } catch (Exception) { }
                    try { Directory.Delete(root, true); } catch (Exception) { }
                }
            }
        }
    }
}
