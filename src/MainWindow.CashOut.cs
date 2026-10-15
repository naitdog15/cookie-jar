// MainWindow.CashOut.cs
//
// Glue for the manual "Cash out" card button (partial class MainWindow, C# 5). ICashOutHost implementation, UI thread only.
// One at a time (shared with Teleport through TeleportService.TryAcquireExclusive); the work runs on a background thread, the
// result comes back through the dispatcher, is logged (every attempt) and shown on the card chip.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;

namespace RbxMulti
{
    public partial class MainWindow : ICashOutHost
    {
        private RobloxInstance _cashSource;
        private readonly Dictionary<int, CashOutResult> _cashLast = new Dictionary<int, CashOutResult>();
        private readonly Dictionary<int, DateTime> _cashLastUtc = new Dictionary<int, DateTime>();

        public bool CashOutBusy { get { return _cashSource != null; } }

        public RobloxInstance CashOutSource { get { return _cashSource; } }

        public string CashOutBlockReason(RobloxInstance inst)
        {
            if (_cashSource != null) return "A CASH OUT test is already running (one at a time).";
            if (_tpSource != null) return "A teleport is running (one at a time). Wait for it to finish or press Cancel.";
            if (_hospSource != null) return "The hospital route is running (one at a time). Wait for it to finish.";
            return ManualCashOut.BlockReason(inst, null, false);
        }

        public CashOutResult LastCashOut(RobloxInstance inst, out DateTime utc)
        {
            utc = DateTime.MinValue;
            if (inst == null) return null;
            CashOutResult r;
            if (!_cashLast.TryGetValue(inst.Id, out r)) return null;
            utc = _cashLastUtc[inst.Id];
            return r;
        }

        public void StartManualCashOut(RobloxInstance inst)
        {
            if (inst == null || Clicker == null) { return; }
            string reason = CashOutBlockReason(inst);
            if (reason == null) { reason = ManualCashOut.BlockReason(inst, null, true); }
            if (reason != null)
            {
                RecordCashOut(inst, new CashOutResult(CashOutKind.Failed, reason), false);
                return;
            }
            _cashSource = inst;
            AppendLog("CASH OUT (manual): " + TeleportText.UserOf(inst) + " started.");
            RefreshTeleportUi();
            AntiIdleClicker clicker = Clicker;
            Thread t = new Thread(delegate()
            {
                CashOutResult r;
                try { r = ManualCashOut.Run(inst, clicker, null, CancellationToken.None, AppendLogSafe); }
                catch (Exception ex) { r = new CashOutResult(CashOutKind.Failed, "Unexpected error: " + ex.GetType().Name); }
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    _cashSource = null;
                    RecordCashOut(inst, r, true);
                }));
            });
            t.IsBackground = true;
            t.Name = "ManualCashOut";
            t.Start();
        }

        private void RecordCashOut(RobloxInstance inst, CashOutResult r, bool alreadyInHistory)
        {
            _cashLast[inst.Id] = r;
            _cashLastUtc[inst.Id] = DateTime.UtcNow;
            if (!alreadyInHistory)
            {
                CashOutEvent e = new CashOutEvent();
                e.Time = DateTime.Now; e.Kind = r.Kind; e.Message = r.Message; e.Manual = true;
                CashOutHistory.Add(inst.Id, e);
                AppendLog("CASH OUT (manual) " + TeleportText.UserOf(inst) + ": " + r.Chip);
            }
            RefreshTeleportUi();
        }
    }
}
