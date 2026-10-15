// MainWindow.Teleport.cs
//
// Teleport feature glue for MainWindow (partial class). C# 5.
//   ITeleportHost implementation used by the instance cards and the mini bar (UI thread only):
//     TeleportBusy, TeleportSource, LastTeleportResult / Source / Utc, TeleportTargets(), TeleportBlockReason(),
//     StartTeleport(), CancelTeleport().
//   One teleport at a time. The work runs on TeleportService's background thread; the result comes back through
//   the dispatcher, is logged and shown on the card chip and the mini bar chip.

using System;
using System.Collections.Generic;
using System.Windows;

namespace RbxMulti
{
    public partial class MainWindow
    {
        internal TeleportService Teleporter;

        private TeleportJob _tpJob;
        private RobloxInstance _tpSource;
        private TeleportResult _tpLast;
        private RobloxInstance _tpLastSource;
        private DateTime _tpLastUtc;
        private string _tpStatusText = "";

        public string TeleportStatusText
        {
            get { return _tpStatusText; }
        }

        private void OnTeleportStatus(string text)
        {
            Dispatcher.BeginInvoke(new Action(delegate
            {
                if (_tpSource == null) { return; }
                _tpStatusText = text;
                RefreshTeleportUi();
            }));
        }

        public bool TeleportBusy
        {
            get { return _tpSource != null; }
        }

        public RobloxInstance TeleportSource
        {
            get { return _tpSource; }
        }

        public TeleportResult LastTeleportResult
        {
            get { return _tpLast; }
        }

        public RobloxInstance LastTeleportSource
        {
            get { return _tpLastSource; }
        }

        public DateTime LastTeleportUtc
        {
            get { return _tpLastUtc; }
        }

        // Shared teleport targets: every Running instance with a username, built once per refresh pass (not once per
        // card). The stamp moves only when that set or a name changes, so cards rebuild their dropdown only then.
        private List<RobloxInstance> _tpEligible = new List<RobloxInstance>();
        private string _tpEligibleSig = "";
        private int _tpStamp;
        private bool _tpInPass;

        public int TeleportTargetsStamp
        {
            get { EnsureTeleportEligible(); return _tpStamp; }
        }

        private void EnsureTeleportEligible()
        {
            if (_tpInPass) return;   // already built for this pass
            List<RobloxInstance> list = new List<RobloxInstance>();
            System.Text.StringBuilder sig = new System.Text.StringBuilder();
            foreach (RobloxInstance inst in _manager.Instances)
            {
                if (inst.StatusKind != "Running") { continue; }
                if (string.IsNullOrEmpty(inst.AccountName)) { continue; }
                list.Add(inst);
                sig.Append(inst.Id).Append(':').Append(TeleportText.UserOf(inst)).Append('|');
            }
            _tpEligible = list;
            string s = sig.ToString();
            if (s != _tpEligibleSig) { _tpEligibleSig = s; _tpStamp++; }
        }

        /// <summary>Runs one card/mini-bar refresh pass with the target list built once and shared.</summary>
        internal void InTeleportPass(Action pass)
        {
            if (_tpInPass) { pass(); return; }
            EnsureTeleportEligible();
            _tpInPass = true;
            try { pass(); }
            finally { _tpInPass = false; }
        }

        public IList<RobloxInstance> TeleportTargets(RobloxInstance source)
        {
            EnsureTeleportEligible();
            List<RobloxInstance> list = new List<RobloxInstance>(_tpEligible.Count);
            foreach (RobloxInstance inst in _tpEligible)
            {
                if (!ReferenceEquals(inst, source)) list.Add(inst);
            }
            return list;
        }

        public string TeleportBlockReason(RobloxInstance source, RobloxInstance target)
        {
            if (Teleporter == null) { return "Teleport is not available."; }
            if (_tpSource != null)
            {
                return "A teleport is already running (one at a time). Wait for it to finish or press Cancel.";
            }
            if (_cashSource != null)
            {
                return "A CASH OUT test is running (one at a time). Wait for it to finish.";
            }
            if (_hospSource != null)
            {
                return "The hospital route is running (one at a time). Wait for it to finish.";
            }
            if (source == null) { return "No instance."; }
            if (source.StatusKind != "Running")
            {
                return "Start this instance and wait for the green Running status first (status: "
                    + (string.IsNullOrEmpty(source.Status) ? "unknown" : source.Status) + ").";
            }
            if (target == null)
            {
                if (TeleportTargets(source).Count == 0)
                {
                    return "Needs a second instance that is Running (green) and was started from a stored account.";
                }
                return "Pick the instance to teleport to.";
            }
            return Teleporter.CanTeleport(source, target, false);
        }

        public void StartTeleport(RobloxInstance source, RobloxInstance target)
        {
            if (_tpSource != null || Teleporter == null) { return; }

            // Full check including the window lookups, once, on the click.
            string reason = Teleporter.CanTeleport(source, target, true);
            if (reason != null)
            {
                SetTeleportResult(source, target, new TeleportResult(TeleportStatus.Failed, reason));
                return;
            }

            _tpSource = source;
            _tpStatusText = "";
            AppendLog("Teleport: " + TeleportText.UserOf(source) + " -> " + TeleportText.UserOf(target) + " started.");
            RefreshTeleportUi();
            _tpJob = Teleporter.Start(source, target, delegate(TeleportResult r)
            {
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    _tpJob = null;
                    _tpSource = null;
                    _tpStatusText = "";
                    SetTeleportResult(source, target, r);
                }));
            });
        }

        public void CancelTeleport()
        {
            TeleportJob job = _tpJob;
            if (job != null)
            {
                AppendLog("Teleport: cancelling...");
                job.Cancel();
            }
        }

        private void SetTeleportResult(RobloxInstance source, RobloxInstance target, TeleportResult r)
        {
            _tpLast = r;
            _tpLastSource = source;
            _tpLastUtc = DateTime.UtcNow;
            AppendLog("Teleport " + TeleportText.UserOf(source) + " -> " + TeleportText.UserOf(target) + ": "
                + TeleportText.Chip(r) + (r.Status == TeleportStatus.Failed ? "" : " (" + r.Message + ")"));
            RefreshTeleportUi();
        }

        private void RefreshTeleportUi()
        {
            InTeleportPass(delegate
            {
                foreach (KeyValuePair<RobloxInstance, InstanceCard> pair in _cards)
                {
                    try { pair.Value.RefreshTeleport(); } catch (Exception) { }
                }
                if (_miniBar != null)
                {
                    try { _miniBar.RefreshTeleport(); } catch (Exception) { }
                }
            });
        }
    }
}
