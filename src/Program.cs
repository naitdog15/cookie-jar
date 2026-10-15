using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;

namespace RbxMulti
{
    /// <summary>Real entry point (build uses /main:RbxMulti.Startup). MainWindow.cs also has Program.Main; it is unused.</summary>
    public static class Startup
    {
        [STAThread]
        public static int Main(string[] args)
        {
            bool createdNew;
            Mutex mtx = null;
            try
            {
                mtx = new Mutex(true, "RbxMulti.Launcher.SingleInstance", out createdNew);
                if (!createdNew)
                {
                    MessageBox.Show("Cookie Jar is already running. Check the notification area.", "Cookie Jar",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return 1;
                }

                Application app = new Application();
                // Hiding to the tray must never end the app; MainWindow calls Shutdown() on a real exit.
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                app.DispatcherUnhandledException += delegate(object s, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
                {
                    MessageBox.Show("Unexpected error: " + e.Exception.Message, "Cookie Jar",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    e.Handled = true;
                };
                bool mini = AutoStart.HasMiniBarFlag(args);
                Stopwatch clock = Stopwatch.StartNew();
                SplashWindow splash = mini ? null : SplashWindow.TryShow();
                MainWindow main;
                try
                {
                    main = new MainWindow(mini);
                }
                catch (Exception)
                {
                    if (splash != null) splash.Close();
                    throw;
                }
                if (splash != null) splash.CloseAfter(clock, TimeSpan.FromMilliseconds(1800));
                app.MainWindow = main;
                if (mini)
                {
                    // Main window stays hidden; only the mini bar is shown. Running clients are adopted by the constructor.
                    main.StartMiniBarMode();
                    return app.Run();
                }
                return app.Run(main);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to start: " + ex.Message, "Cookie Jar",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return 2;
            }
            finally
            {
                if (mtx != null)
                {
                    try { mtx.ReleaseMutex(); } catch (ApplicationException) { }
                    mtx.Close();
                }
            }
        }
    }
}
