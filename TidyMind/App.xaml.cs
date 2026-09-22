using System;
using System.Windows;
using System.Windows.Input;

namespace TidyMind
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            if (TryHandleRemindArgument(e.Args))
            {
                Shutdown();
                return;
            }

            this.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            EventManager.RegisterClassHandler(typeof(Window), Window.PreviewKeyDownEvent,
                new KeyEventHandler(GlobalPreviewKeyDown));

            ProfileWindow profileWindow = new ProfileWindow();
            bool? result = profileWindow.ShowDialog();

            if (result == true)
            {
                OpenMemoryWindow(profileWindow, null);
            }
            else
            {
                Shutdown();
            }
        }

        public void SwitchProfile(Window currentWindow)
        {
            this.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            currentWindow.Hide();

            ProfileWindow profileWindow = new ProfileWindow();
            profileWindow.Width = currentWindow.ActualWidth;
            profileWindow.Height = currentWindow.ActualHeight;
            bool? result = profileWindow.ShowDialog();

            if (result == true)
            {
                currentWindow.Close();
                OpenMemoryWindow(profileWindow, null);
            }
            else
            {
                currentWindow.Close();
                Shutdown();
            }
        }

        // Handles "TidyMind.exe --remind {reminderId}", which is how the
        // Windows Task Scheduler task for a reminder fires it. In that case we
        // only show the notification and exit — no window is ever opened.
        private bool TryHandleRemindArgument(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--remind" && i + 1 < args.Length)
                {
                    if (Guid.TryParse(args[i + 1], out Guid reminderId))
                        ReminderManager.FireReminder(reminderId);

                    return true;
                }
            }

            return false;
        }

        private void GlobalPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.N && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                QuickNotesWindow.ShowOrFocus();
                e.Handled = true;
            }
            else if (e.Key == Key.R && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                RemindersWindow.ShowOrFocus();
                e.Handled = true;
            }
        }

        private void OpenMemoryWindow(ProfileWindow profileWindow, Window previousWindow)
        {
            if (profileWindow.SelectedProfileType == ProfileType.Collection)
            {
                CollectionWindow collectionWindow = new CollectionWindow(profileWindow.SelectedProfileName);
                collectionWindow.Width = profileWindow.ActualWidth;
                collectionWindow.Height = profileWindow.ActualHeight;
                this.MainWindow = collectionWindow;
                this.ShutdownMode = ShutdownMode.OnMainWindowClose;
                collectionWindow.Show();
            }
            else
            {
                MainWindow mainWindow = new MainWindow(profileWindow.SelectedProfileName);
                mainWindow.Width = profileWindow.ActualWidth;
                mainWindow.Height = profileWindow.ActualHeight;
                this.MainWindow = mainWindow;
                this.ShutdownMode = ShutdownMode.OnMainWindowClose;
                mainWindow.Show();
            }
        }
    }
}