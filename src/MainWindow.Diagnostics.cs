using System;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace RbxMulti
{
    public partial class MainWindow
    {
        private bool _dumpLogBusy;

        private void OnDumpLog(object sender, RoutedEventArgs e)
        {
            if (_dumpLogBusy) return;
            SaveFileDialog dialog = new SaveFileDialog
            {
                Title = "Save Cookie Jar diagnostic log",
                Filter = "Text files (*.txt)|*.txt",
                DefaultExt = ".txt",
                AddExtension = true,
                OverwritePrompt = true,
                FileName = "CookieJar-log-" + AppVersion.Display + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".txt"
            };
            if (dialog.ShowDialog(this) != true) return;

            // Capture UI-owned state here; file I/O runs off the dispatcher while clients keep running.
            string path = dialog.FileName;
            string visibleLog = _logBox == null ? "" : _logBox.Text;
            string context = DiagnosticContext();
            Button button = (Button)sender;
            _dumpLogBusy = true;
            button.IsEnabled = false;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string error = null;
                try
                {
                    DiagnosticReport.Save(path, DiagnosticReport.Create(AppVersion.Display, context, LauncherLog.ReadRecent(), visibleLog));
                }
                catch (Exception ex) { error = ex.Message; }
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    _dumpLogBusy = false;
                    button.IsEnabled = true;
                    if (_exiting) return;
                    if (error != null)
                    {
                        AppendLog("Log export failed: " + error);
                        MessageBox.Show(this, "Could not save the diagnostic log.\n\n" + error + "\n\nTry another folder.", "Cookie Jar", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }
                    AppendLog("Diagnostic log saved to " + path);
                    MessageBox.Show(this, "Diagnostic log saved to:\n" + path
                        + "\n\nAttach this text file to your support message and describe the issue. Review it before sharing; activity can include account and profile display names.",
                        "Cookie Jar log saved", MessageBoxButton.OK, MessageBoxImage.Information);
                }));
            });
        }

        private string DiagnosticContext()
        {
            StringBuilder context = new StringBuilder();
            context.AppendLine("Windows: " + Environment.OSVersion.VersionString);
            context.AppendLine("64-bit OS/process: " + Environment.Is64BitOperatingSystem + "/" + Environment.Is64BitProcess);
            context.AppendLine("CLR: " + Environment.Version);
            context.AppendLine("Administrator: " + HandleCloser.IsElevated());
            context.AppendLine("Hardware: " + (_profile == null ? "detection pending" : _profile.Summary));
            context.AppendLine("Hardware instance-limit override: " + _overrideInstanceLimit);
            context.AppendLine("Auto RAM: " + _autoRam + "; background efficiency: " + _bgEfficiency + "; keep alive: " + _keepAlive);
            context.AppendLine("Launch/action in progress: " + IsWorking() + "; profile running: " + _profileRunning);
            context.AppendLine("Tracked clients: " + _manager.Instances.Count);
            foreach (RobloxInstance instance in _manager.Instances)
                context.AppendLine(string.Format(CultureInfo.InvariantCulture, "Client {0}: PID {1}; status {2}; memory {3:0} MB; adopted {4}",
                    instance.Id, instance.Pid, instance.StatusKind, instance.MemoryMb, instance.IsAdopted));
            return context.ToString();
        }
    }
}
