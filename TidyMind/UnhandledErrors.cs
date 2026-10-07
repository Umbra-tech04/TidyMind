using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace TidyMind
{
    // The last line of defence for exceptions nothing else caught: each is written to error.log and reported, and the
    // app keeps running where it safely can. Every change is saved as it's made, so at most the last one is lost.
    // The UI thread covers clicks, DispatcherTimers and async continuations; other threads and unobserved tasks are
    // logged too. In the reminder process (no UI) errors are only logged and the process ends.
    public static class UnhandledErrors
    {
        // The same failure firing again and again (e.g. while redrawing) would otherwise stack up dialogs forever.
        private const int MaxErrorsInWindow = 3;
        private static readonly TimeSpan ErrorWindow = TimeSpan.FromSeconds(10);
        private const long MaxLogBytes = 1024 * 1024;

        private static readonly Queue<DateTime> recentErrors = new Queue<DateTime>();
        private static readonly object logLock = new object();
        private static bool background;
        private static bool reporting;

        public static string LogPath => AppPaths.Data("error.log");

        public static void Install(Application app, bool backgroundProcess)
        {
            background = backgroundProcess;
            app.DispatcherUnhandledException += OnUiThreadError;
            AppDomain.CurrentDomain.UnhandledException += OnOtherThreadError;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskError;
        }

        private static void OnUiThreadError(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Log("UI thread", e.Exception);
            e.Handled = true;

            Application app = Application.Current;
            if (background)
            {
                app.Shutdown();
                return;
            }
            if (reporting)
                return; // one dialog at a time: a failure repeating while it's open is only logged

            // Failed before the main window came up (or after it closed): nothing to keep running.
            bool noWindow = app.MainWindow == null || !app.MainWindow.IsVisible;
            bool closing = CountError() || noWindow;

            reporting = true;
            try
            {
                MessageBox.Show(
                    "Something went wrong: " + e.Exception.Message + "\n\n"
                    + (closing
                        ? "TidyMind has to close. "
                        : "TidyMind will keep running, but your last change may not have been saved. ")
                    + "The details were written to " + LogPath + ".",
                    "Unexpected Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                reporting = false;
            }

            if (closing)
                app.Shutdown();
        }

        // Another thread's exception ends the process; all that's left is to record it and say so.
        private static void OnOtherThreadError(object sender, UnhandledExceptionEventArgs e)
        {
            Exception exception = e.ExceptionObject as Exception;
            Log("background thread", exception);
            if (background || !e.IsTerminating)
                return;

            MessageBox.Show("Something went wrong and TidyMind has to close: " + exception?.Message + "\n\n"
                + "Your last change may not have been saved. The details were written to " + LogPath + ".",
                "Unexpected Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        // A background task (e.g. decoding a picture) that failed and that nothing waited for.
        private static void OnUnobservedTaskError(object sender, UnobservedTaskExceptionEventArgs e)
        {
            Log("background task", e.Exception);
            e.SetObserved();
        }

        // True once errors come too thick and fast to carry on.
        private static bool CountError()
        {
            DateTime now = DateTime.Now;
            recentErrors.Enqueue(now);
            while (recentErrors.Count > 0 && now - recentErrors.Peek() > ErrorWindow)
                recentErrors.Dequeue();
            return recentErrors.Count >= MaxErrorsInWindow;
        }

        // Best effort: logging must never throw from inside the error handler. Rolled over at 1 MB (one old copy kept).
        private static void Log(string where, Exception exception)
        {
            try
            {
                lock (logLock)
                {
                    Directory.CreateDirectory(AppPaths.DataFolder);
                    FileInfo log = new FileInfo(LogPath);
                    if (log.Exists && log.Length > MaxLogBytes)
                        File.Move(LogPath, AppPaths.Data("error.old.log"), overwrite: true);

                    File.AppendAllText(LogPath, DateTime.Now.ToString("u") + "  (" + where + ")" + Environment.NewLine
                        + exception + Environment.NewLine + Environment.NewLine);
                }
            }
            catch (Exception)
            {
            }
        }
    }
}
