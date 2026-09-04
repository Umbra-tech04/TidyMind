using System.Windows;

namespace TidyMind
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            this.ShutdownMode = ShutdownMode.OnExplicitShutdown;

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