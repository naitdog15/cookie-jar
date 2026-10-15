// MainWindow.Hospital.cs
//
// Glue for the "Hospital" card button (partial class MainWindow, C# 5). UI thread only, except the route itself, which runs
// on its own background thread (HospitalRoute.Run) and returns through the dispatcher.
// One at a time: shares TeleportService's exclusive guard with Teleport and manual CASH OUT (HospitalRoute takes it), and
// the block-reason texts of the other two features mention a running route.

using System;
using System.Collections.Generic;
using System.Threading;

namespace RbxMulti
{
    public partial class MainWindow
    {
        public const string HospitalButtonTip = "Go to the hospital: opens the backpack, Gifts, +, Yes on the Gifts Display prompt, then turns the character to the hospital marker";

        private RobloxInstance _hospSource;
        private CancellationTokenSource _hospCts;
        private readonly Dictionary<int, string> _hospLast = new Dictionary<int, string>();

        public bool HospitalRunningFor(RobloxInstance inst)
        {
            return _hospSource != null && inst != null && _hospSource.Id == inst.Id;
        }

        /// <summary>The card button: starts the route, or cancels it while it runs for this instance.</summary>
        public void HospitalButtonClicked(RobloxInstance inst)
        {
            if (HospitalRunningFor(inst))
            {
                CancellationTokenSource cts = _hospCts;
                if (cts != null) cts.Cancel();
                AppendLog("Hospital " + TeleportText.UserOf(inst) + ": cancel requested.");
                return;
            }
            StartHospitalRoute(inst);
        }

        // checkWindow = false from the card refresh (every tick); true when a start is requested.
        public string HospitalBlockReason(RobloxInstance inst, bool checkWindow)
        {
            if (_hospSource != null) return "The hospital route is already running (one at a time).";
            if (_cashSource != null) return "A CASH OUT test is running (one at a time). Wait for it to finish.";
            if (_tpSource != null) return "A teleport is running (one at a time). Wait for it to finish or press Cancel.";
            return HospitalRoute.BlockReason(inst, null, checkWindow);
        }

        /// <summary>Text of the last route result for this instance, or null.</summary>
        public string LastHospitalResult(RobloxInstance inst)
        {
            if (inst == null) return null;
            string s;
            return _hospLast.TryGetValue(inst.Id, out s) ? s : null;
        }

        public void StartHospitalRoute(RobloxInstance inst)
        {
            if (inst == null) return;
            string reason = HospitalBlockReason(inst, true);
            if (reason != null)
            {
                _hospLast[inst.Id] = reason;
                AppendLog("Hospital: " + reason);
                RefreshTeleportUi();
                return;
            }

            _hospSource = inst;
            CancellationTokenSource cts = new CancellationTokenSource();
            _hospCts = cts;
            AppendLog("Hospital route started: " + TeleportText.UserOf(inst) + " (Cancel on the card to stop it; moving the mouse also stops it).");
            RefreshTeleportUi();
            Thread t = new Thread(delegate()
            {
                HospitalResult r;
                try { r = HospitalRoute.Run(inst, null, cts.Token, AppendLogSafe); }
                catch (Exception ex) { r = new HospitalResult(false, "Unexpected error: " + ex.GetType().Name); }
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    _hospSource = null;
                    _hospCts = null;
                    cts.Dispose();
                    _hospLast[inst.Id] = r.Message;
                    AppendLog("Hospital " + TeleportText.UserOf(inst) + ": " + (r.Ok ? "done - " : "stopped - ") + r.Message);
                    RefreshTeleportUi();
                }));
            });
            t.IsBackground = true;
            t.Name = "HospitalRoute";
            t.Start();
        }
    }
}
