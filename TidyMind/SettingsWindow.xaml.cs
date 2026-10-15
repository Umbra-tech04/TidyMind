using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;

namespace TidyMind
{
    public partial class SettingsWindow : Window
    {
        private readonly Action onExportAll;

        public SettingsWindow(Action onExportAll)
        {
            InitializeComponent();
            this.onExportAll = onExportAll;
            VersionText.Text = "TidyMind " + DataBackup.AppVersion;
            DataFolderText.Text = AppPaths.DataFolder;
            DataFolderText.ToolTip = AppPaths.DataFolder;
        }

        // Esc closes, as in the other dialogs.
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Key != Key.Escape || e.Handled) return;
            e.Handled = true;
            Close();
        }

        private void LicensesLink_Click(object sender, RoutedEventArgs e) => new LicensesWindow { Owner = this }.ShowDialog();

        private void ExportAllButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
            onExportAll?.Invoke();
        }

        // ---- Backup -------------------------------------------------------

        private void BackupButton_Click(object sender, RoutedEventArgs e)
        {
            SaveFileDialog dialog = new SaveFileDialog
            {
                FileName = "TidyMind-backup-" + DateTime.Now.ToString("yyyy-MM-dd-HHmm") + ".zip",
                Filter = "ZIP files (*.zip)|*.zip"
            };
            if (dialog.ShowDialog(this) != true) return;

            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                DataBackup.Create(dialog.FileName);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException)
            {
                Mouse.OverrideCursor = null;
                MessageBox.Show(this, "The backup couldn't be saved.\n\n" + ex.Message, "Backup",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }

            MessageBox.Show(this, "Backup saved: " + dialog.FileName, "Backup", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // Checked, confirmed, then DataBackup.Restore (which keeps the current data on any failure), then a restart, so
        // nothing still in memory from the old data can be saved over the restored files.
        private void RestoreButton_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog dialog = new OpenFileDialog { Filter = "TidyMind backups (*.zip)|*.zip" };
            if (dialog.ShowDialog(this) != true) return;

            string? problem = DataBackup.CheckBackup(dialog.FileName);
            if (problem != null)
            {
                MessageBox.Show(this, problem, "Restore from Backup", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (MessageBox.Show(this, "Restore from this backup?\n" + dialog.FileName + "\n\nYour current data will be "
                    + "replaced. A copy of it is saved first, then TidyMind restarts.", "Restore from Backup", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                    MessageBoxResult.No) != MessageBoxResult.Yes)
                return;

            // Quick Notes saves what's typed when it closes: now, into the current data, not later over the restored one.
            QuickNotesWindow.CloseIfOpen();
            if (QuickNotesWindow.IsOpen)
                return; // it said why it couldn't save; nothing was changed

            string? safetyCopy = null;
            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                DataBackup.Restore(dialog.FileName, out safetyCopy);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException
                                       || ex is NotSupportedException || ex is ArgumentException)
            {
                Mouse.OverrideCursor = null;
                MessageBox.Show(this, "The backup wasn't restored: " + ex.Message + "\n\nYour current data was left as it was."
                    + (safetyCopy != null && File.Exists(safetyCopy) ? "\nA safety copy of it was also saved here:\n" + safetyCopy : ""),
                    "Restore from Backup", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }

            MessageBox.Show(this, "The backup was restored. Your previous data was saved here:\n" + safetyCopy
                + "\n\nTidyMind will now restart.", "Restore from Backup", MessageBoxButton.OK, MessageBoxImage.Information);
            DataBackup.Restart();
        }

        // ---- Data folder --------------------------------------------------

        private void OpenDataFolderButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                DataBackup.OpenFolder();
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is IOException
                                       || ex is UnauthorizedAccessException)
            {
                MessageBox.Show(this, "The data folder couldn't be opened:\n" + AppPaths.DataFolder + "\n\n" + ex.Message,
                    "Open Data Folder", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
