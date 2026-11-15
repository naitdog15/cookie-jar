using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;

namespace RbxMulti
{
    public partial class MainWindow
    {
        private InfoBanner _robloxNotice;
        private Button _robloxRepairButton;
        private StackPanel _robloxNoticePanel;
        private StaleRobloxCopy _staleCopy;
        private bool _robloxAuditBusy, _robloxRepairBusy;
        private string _lastRobloxAuditText;

        private UIElement BuildRobloxNotice()
        {
            _robloxNoticePanel = new StackPanel();
            _robloxNoticePanel.Visibility = Visibility.Collapsed;
            _robloxNoticePanel.Margin = new Thickness(0, 8, 0, 0);
            _robloxNotice = new InfoBanner();
            _robloxNoticePanel.Children.Add(_robloxNotice);
            _robloxRepairButton = PillButton.Create("Remove outdated Roblox copy", PillStyle.Secondary);
            _robloxRepairButton.HorizontalAlignment = HorizontalAlignment.Right;
            _robloxRepairButton.Margin = new Thickness(0, 8, 0, 0);
            _robloxRepairButton.Visibility = Visibility.Collapsed;
            _robloxRepairButton.Click += OnRemoveOutdatedRobloxClick;
            _robloxNoticePanel.Children.Add(_robloxRepairButton);
            return _robloxNoticePanel;
        }

        /// <summary>Non-blocking: audits on the thread pool, then updates the notice on the UI thread.</summary>
        private void RefreshRobloxNotice(string reason)
        {
            if (_robloxAuditBusy || _robloxRepairBusy) return;
            _robloxAuditBusy = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                RobloxCopyAudit audit = null;
                try { audit = RobloxRepair.Audit(new RobloxRepairEnvironment(), RobloxLocator.GetCandidatesFresh()); } catch (Exception) { }
                try
                {
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        _robloxAuditBusy = false;
                        if (!_exiting) ApplyRobloxAudit(audit, reason);
                    }));
                }
                catch (Exception) { }
            });
        }

        private void ApplyRobloxAudit(RobloxCopyAudit audit, string reason)
        {
            if (audit == null || audit.Warnings.Count == 0)
            {
                _robloxNoticePanel.Visibility = Visibility.Collapsed;
                _staleCopy = null;
                if (_lastRobloxAuditText != null) AppendLog("Roblox installs: no outdated copy found");
                _lastRobloxAuditText = null;
                return;
            }

            _staleCopy = audit.Repairable;
            string text = string.Join(" ", audit.Warnings.ToArray()) + (audit.Repairable != null
                ? " Cookie Jar launches the newest copy, but the old per-user copy still opens roblox:// links from the browser and can start update loops."
                : " This copy is not a per-user install, so Cookie Jar will not remove it; update or repair Roblox with its own installer.");
            _robloxNotice.Show("Warning", text);
            _robloxNoticePanel.Visibility = Visibility.Visible;
            _robloxRepairButton.Visibility = audit.Repairable != null ? Visibility.Visible : Visibility.Collapsed;
            if (text != _lastRobloxAuditText)
            {
                foreach (string w in audit.Warnings) AppendLog("(" + reason + ") " + w);
                _lastRobloxAuditText = text;
            }
        }

        private void OnRemoveOutdatedRobloxClick(object sender, RoutedEventArgs e)
        {
            StaleRobloxCopy plan = _staleCopy;
            if (plan == null || _robloxRepairBusy) return;

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Folder: " + plan.Folder);
            foreach (string k in plan.HkcuKeys) sb.AppendLine("Registry key: HKEY_CURRENT_USER\\" + k);
            sb.AppendLine();
            sb.AppendLine("A .reg backup of each key is saved first to %LOCALAPPDATA%\\RobloxMultiLauncher\\backups.");
            sb.AppendLine("Program Files, HKEY_LOCAL_MACHINE, Roblox Studio, logs and settings are not changed.");
            sb.AppendLine("Close every Roblox game and the Roblox installer first.");
            sb.AppendLine();
            sb.Append("Newest copy kept: " + plan.NewestExe + " (" + plan.NewestVersion + ")");

            if (MessageBox.Show(this, sb.ToString(), "Remove outdated Roblox copy", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            {
                AppendLog("Outdated Roblox copy kept (removal declined)");
                return;
            }

            _robloxRepairBusy = true;
            _robloxRepairButton.IsEnabled = false;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string report = null, backup = null;
                RepairOutcome outcome = RepairOutcome.Failed;
                try { outcome = RobloxRepair.Execute(new RobloxRepairEnvironment(), plan, out report, out backup); }
                catch (Exception ex) { report = ex.Message; }
                try
                {
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        _robloxRepairBusy = false;
                        _robloxRepairButton.IsEnabled = true;
                        AppendLog("Remove outdated Roblox copy: " + outcome + ". " + (report ?? "").Replace(Environment.NewLine, " | "));
                        _robloxNotice.Show(outcome == RepairOutcome.Removed ? "Info" : "Warning", "Remove outdated Roblox copy: " + outcome + ". " + (report ?? ""));
                        _lastRobloxAuditText = null;
                        RobloxLocator.InvalidateCache();
                        if (outcome == RepairOutcome.Removed || outcome == RepairOutcome.PartiallyRemoved) RefreshRobloxNotice("after removal");
                    }));
                }
                catch (Exception) { }
            });
        }
    }
}
