using System.Configuration;
using System.Data;
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
                MainWindow mainWindow = new MainWindow(profileWindow.SelectedProfileName);
                this.MainWindow = mainWindow;
                this.ShutdownMode = ShutdownMode.OnMainWindowClose;
                mainWindow.Show();
            }
            else
            {
                Shutdown();
            }
        }

        public void SwitchProfile(Window currentWindow)
        {
            this.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            ProfileWindow profileWindow = new ProfileWindow();
            bool? result = profileWindow.ShowDialog();

            if (result == true)
            {
                MainWindow mainWindow = new MainWindow(profileWindow.SelectedProfileName);
                this.MainWindow = mainWindow;
                mainWindow.Show();
            }

            this.ShutdownMode = ShutdownMode.OnMainWindowClose;
            currentWindow.Close();
        }
    }
}