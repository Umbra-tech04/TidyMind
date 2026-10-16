using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media.Animation;

namespace TidyMind
{
    public partial class App : Application
    {
        private const string InstanceMutexName = @"Local\TidyMind_SingleInstance";
        private const string ActivateEventName = @"Local\TidyMind_Activate";
        private const int ASFW_ANY = -1;
        private const string RemoveTasksArgument = "--remove-reminder-tasks";

        // TidyMind's ID to Windows: taskbar grouping, reminder notifications, the user's notification settings. Must
        // equal MyAppUserModelId in installer/TidyMind.iss; never change it.
        public const string UserModelId = "TidyMind.App";

        // Held for the app's whole life; static so it's never collected (which would release it).
        private static Mutex instanceMutex;
        private static EventWaitHandle activateSignal;
        private static RegisteredWaitHandle activateWait;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            int remindAt = Array.IndexOf(e.Args, "--remind");
            bool removeTasks = Array.IndexOf(e.Args, RemoveTasksArgument) >= 0;
            UnhandledErrors.Install(this, backgroundProcess: removeTasks || (remindAt >= 0 && remindAt < e.Args.Length - 1));

            // Run by the uninstaller (installer/TidyMind.iss) before the program files go: deletes the reminder tasks
            // and exits, no UI, no data read or changed. Exit code 1 if that failed (the uninstall carries on regardless;
            // the reason goes to error.log).
            if (removeTasks)
            {
                int exitCode = 0;
                try
                {
                    ReminderManager.RemoveAllReminderTasks();
                }
                catch (Exception ex)
                {
                    exitCode = 1;
                    UnhandledErrors.LogQuietly("removing reminder tasks (uninstall)", ex);
                }
                Shutdown(exitCode);
                return;
            }

            // The saved language (or the Windows display language): for every message from here on, the reminder's
            // notification included.
            Loc.Initialize();
            FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
                new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(Loc.Culture.IetfLanguageTag)));

            // Launched by a Task Scheduler reminder: show the toast and exit, no UI. Checked before the one-copy
            // rule, so reminders still fire while the app is open.
            if (TryHandleRemindArgument(e.Args))
            {
                Shutdown();
                return;
            }

            // Before any window, so the taskbar groups them under the Start Menu shortcut. Only here: a reminder names
            // the ID itself, and its fallback balloon must go out under the exe when no shortcut has the ID.
            SetCurrentProcessExplicitAppUserModelID(UserModelId);

            WaitForRestartedFrom(e.Args);
            if (!ClaimSingleInstance())
            {
                Shutdown();
                return;
            }

            if (!LegacyDataMigration.TryRun(out string problem))
            {
                Msg.Show(Loc.F("App.Start.LegacyCopy", AppPaths.DataFolder, problem), Loc.T("App.Start.Caption"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown();
                return;
            }

            // Without the list of memories there's nothing to show, and starting empty would let the next save
            // replace it; so the app stops here and leaves the file for the user to fix or restore.
            if (!ProfileManager.TryLoadProfiles(out List<Profile> profiles, out problem))
            {
                Msg.Show(Loc.F("App.Start.Profiles", AppPaths.Data("profiles.json"), problem), Loc.T("App.Start.Caption"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown();
                return;
            }

            if (!MemoryLayoutMigration.TryRun(profiles, out problem))
            {
                Msg.Show(Loc.F("App.Start.Layout", AppPaths.Data("Memories"), problem), Loc.T("App.Start.Caption"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown();
                return;
            }

            QuickTodoService.RemoveOldCompleted(DateTime.Today);

            // Tasks pointing at an exe that moved, missing, or left over for deleted reminders get fixed (off the UI
            // thread), so every reminder the app shows will also fire.
            ReminderManager.RepairTasksInBackground(force: true);

            EventManager.RegisterClassHandler(typeof(Window), Window.PreviewKeyDownEvent,
                new KeyEventHandler(GlobalPreviewKeyDown));
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
                new RoutedEventHandler(AnimateWindowOpen));
            // A window opened after a language switch: the start-up default above is still the old language.
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
                new RoutedEventHandler((s, args) => Loc.SetLanguage((Window)s)));
            EventManager.RegisterClassHandler(typeof(DatePicker), FrameworkElement.LoadedEvent,
                new RoutedEventHandler((s, args) => Loc.PrepareDatePicker((DatePicker)s)));
            TextEditing.DisableOvertype();

            MainWindow window = new MainWindow(profiles);
            this.MainWindow = window;
            this.ShutdownMode = ShutdownMode.OnMainWindowClose;
            window.Show();
        }

        // Started by a restore ("--after-restore <pid>", DataBackup.Restart): the copy that did the restore is still
        // closing and holds the one-copy lock, so wait (briefly) for it to be gone.
        private static void WaitForRestartedFrom(string[] args)
        {
            int at = Array.IndexOf(args, "--after-restore");
            if (at < 0 || at + 1 >= args.Length || !int.TryParse(args[at + 1], out int pid))
                return;

            try
            {
                using (System.Diagnostics.Process previous = System.Diagnostics.Process.GetProcessById(pid))
                    previous.WaitForExit(15000);
            }
            catch (ArgumentException)
            {
                // Already gone.
            }
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

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

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
