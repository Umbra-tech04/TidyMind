using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace TidyMind
{
    public partial class App : Application
    {
        private const string InstanceMutexName = @"Local\TidyMind_SingleInstance";
        private const string ActivateEventName = @"Local\TidyMind_Activate";
        private const int ASFW_ANY = -1;

        // Held for the app's whole life; static so it's never collected (which would release it).
        private static Mutex instanceMutex;
        private static EventWaitHandle activateSignal;
        private static RegisteredWaitHandle activateWait;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Launched by a Task Scheduler reminder: show the toast and exit, no UI. Checked before the one-copy
            // rule, so reminders still fire while the app is open.
            if (TryHandleRemindArgument(e.Args))
            {
                Shutdown();
                return;
            }

            if (!ClaimSingleInstance())
            {
                Shutdown();
                return;
            }

            if (!LegacyDataMigration.TryRun(out string problem))
            {
                MessageBox.Show("TidyMind couldn't copy your data into its new folder:\n" + AppPaths.DataFolder
                    + "\n\n" + problem + "\n\nYour existing data was left untouched. Free up disk space or check the "
                    + "folder's permissions, then start TidyMind again.", "Couldn't Start", MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Shutdown();
                return;
            }

            QuickTodoService.RemoveOldCompleted(DateTime.Today);

            EventManager.RegisterClassHandler(typeof(Window), Window.PreviewKeyDownEvent,
                new KeyEventHandler(GlobalPreviewKeyDown));
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
                new RoutedEventHandler(AnimateWindowOpen));

            MainWindow window = new MainWindow();
            this.MainWindow = window;
            this.ShutdownMode = ShutdownMode.OnMainWindowClose;
            window.Show();
        }

        private bool TryHandleRemindArgument(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--remind" && i + 1 < args.Length)
                {
                    if (Guid.TryParse(args[i + 1], out Guid reminderId))
                    {
                        // The same data folder as the app; if it isn't ready yet (first start of this version,
                        // copy failed), the reminder is tried again later rather than dropped.
                        if (LegacyDataMigration.TryRun(out _))
                            ReminderManager.FireReminder(reminderId);
                        else
                            ReminderManager.RetryLater(reminderId);
                    }

                    return true;
                }
            }

            return false;
        }

        // One copy of the app at a time: each view keeps its lists in memory and writes them back whole, so a second
        // copy would silently overwrite the first one's changes. Starting TidyMind again brings the open one forward.
        private bool ClaimSingleInstance()
        {
            instanceMutex = new Mutex(true, InstanceMutexName, out bool createdNew);
            if (!createdNew)
            {
                instanceMutex.Dispose();
                instanceMutex = null;

                // This process was just started by the user, so it may hand the foreground to the running copy.
                AllowSetForegroundWindow(ASFW_ANY);
                if (EventWaitHandle.TryOpenExisting(ActivateEventName, out EventWaitHandle signal))
                {
                    using (signal)
                        signal.Set();
                }
                return false;
            }

            activateSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
            activateWait = ThreadPool.RegisterWaitForSingleObject(activateSignal,
                (state, timedOut) => Dispatcher.BeginInvoke(new Action(BringToFront)),
                null, Timeout.Infinite, executeOnlyOnce: false);
            return true;
        }

        protected override void OnExit(ExitEventArgs e)
        {
            activateWait?.Unregister(null);
            base.OnExit(e);
        }

        private void BringToFront()
        {
            Window window = this.MainWindow;
            if (window == null)
                return; // still starting up: it'll come up on its own

            if (window.WindowState == WindowState.Minimized)
                window.WindowState = WindowState.Normal;
            window.Activate();
        }

        [DllImport("user32.dll")]
        private static extern bool AllowSetForegroundWindow(int processId);

        // Fades the content, not the window: top-level Opacity needs AllowsTransparency. All windows share the
        // BgMain background, so the fade never shows a colour change.
        private void AnimateWindowOpen(object sender, RoutedEventArgs e)
        {
            if (sender is MainWindow || !(sender is Window window) || !(window.Content is UIElement content))
                return;

            // Hidden from the very first frame; the fade starts only once the window has actually drawn,
            // otherwise a slow first render (e.g. ProjectWindow) flashes at full opacity or eats the animation.
            content.Opacity = 0;

            window.ContentRendered += (s, args) =>
            {
                DoubleAnimation fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(400))
                {
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut }
                };
                fade.Completed += (o, done) =>
                {
                    content.Opacity = 1;
                    content.BeginAnimation(UIElement.OpacityProperty, null);
                };
                content.BeginAnimation(UIElement.OpacityProperty, fade);
            };
        }

        // The selection if there is one, otherwise the whole text.
        private void SelectableTextCopy_Click(object sender, RoutedEventArgs e)
        {
            if (((ContextMenu)((MenuItem)sender).Parent).PlacementTarget is TextBox box)
                TextCopy.Copy(box.SelectionLength > 0 ? box.SelectedText : box.Text);
        }

        // Registered on the Window class, so this sees every key press in every window — one place for all shortcuts.
        private void GlobalPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (OverlayManager.TryHandleShortcut(e))
                e.Handled = true;
        }
    }
}
