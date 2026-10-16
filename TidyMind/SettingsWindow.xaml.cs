using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
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
            BuildLanguageChoices();
        }

        // One segment per language; picking one switches the whole app at once (Loc.Switch), this window included.
        private void BuildLanguageChoices()
        {
            foreach (LanguageOption language in Loc.Languages)
            {
                RadioButton choice = new RadioButton
                {
                    Content = language.Name,
                    GroupName = "Language",
                    Style = (Style)FindResource("SegmentStyle"),
                    IsChecked = language.Code == Loc.Current
                };
                choice.Checked += (s, e) =>
                {
                    if (language.Code != Loc.Current && !Loc.Switch(language.Code))
                        Msg.Show(this, Loc.T("Settings.Language.NotSaved"), Loc.T("Settings.Language.Title"),
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                };
                LanguageChoices.Children.Add(choice);
            }
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
                FileName = Loc.F("Settings.Backup.FileName", DateTime.Now.ToString("yyyy-MM-dd-HHmm", CultureInfo.InvariantCulture)),
                Filter = Loc.T("Settings.Backup.Filter")
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
                Msg.Show(this, Loc.F("Settings.Backup.Failed", ex.Message), Loc.T("Settings.Backup.Caption"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }

            Msg.Show(this, Loc.F("Settings.Backup.Saved", dialog.FileName), Loc.T("Settings.Backup.Caption"), MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        // Checked, confirmed, then DataBackup.Restore (which keeps the current data on any failure), then a restart, so
        // nothing still in memory from the old data can be saved over the restored files.
        private void RestoreButton_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog dialog = new OpenFileDialog { Filter = Loc.T("Settings.Restore.Filter") };
            if (dialog.ShowDialog(this) != true) return;

            string? problem = DataBackup.CheckBackup(dialog.FileName);
            if (problem != null)
            {
                Msg.Show(this, problem, Loc.T("Settings.Restore.Caption"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (Msg.Show(this, Loc.F("Settings.Restore.Confirm", dialog.FileName), Loc.T("Settings.Restore.Caption"),
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
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
                Msg.Show(this, Loc.F("Settings.Restore.Failed", ex.Message)
                    + (safetyCopy != null && File.Exists(safetyCopy) ? "\n" + Loc.F("Settings.Restore.FailedSafetyCopy", safetyCopy) : ""),
                    Loc.T("Settings.Restore.Caption"), MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }

            Msg.Show(this, Loc.F("Settings.Restore.Done", safetyCopy), Loc.T("Settings.Restore.Caption"), MessageBoxButton.OK,
                MessageBoxImage.Information);
            DataBackup.Restart();
        }

        // ---- Notifications ------------------------------------------------

        private static readonly TimeSpan ScheduledTestWait = TimeSpan.FromSeconds(40);

        // Two notifications, to tell where a failure is: one straight from this process, and one through a one-off
        // Task Scheduler task made by the same code as a reminder's (same user, session and settings). The outcome is
        // said in plain words; the exact errors go to reminders.log.
        private async void TestNotificationButton_Click(object sender, RoutedEventArgs e)
        {
            TestNotifyButton.IsEnabled = false;
            NotifyNote.Text = Loc.T("Settings.Notify.Waiting");
            NotificationResult direct = ReminderManager.TestNow();

            Guid token = Guid.NewGuid();
            string scheduled;
            try
            {
                ReminderManager.ScheduleTest(token, DateTime.Now.AddSeconds(5));
                NotificationResult? answer = null;
                for (TimeSpan waited = TimeSpan.Zero; answer == null && waited < ScheduledTestWait; waited += TimeSpan.FromMilliseconds(500))
                {
                    await System.Threading.Tasks.Task.Delay(500);
                    answer = ReminderManager.ReadTestResult(token);
                }
                scheduled = answer == null ? Loc.T("Settings.Notify.NoAnswer") : Describe(answer);
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException || ex is UnauthorizedAccessException || ex is IOException)
            {
                scheduled = Loc.F("Settings.Notify.NotScheduled", ex.Message);
            }
            finally
            {
                ReminderManager.RemoveTest(token);
            }

            NotifyNote.SetBinding(TextBlock.TextProperty, Loc.Bind("Settings.Notify.Note"));
            TestNotifyButton.IsEnabled = true;
            Msg.Show(this, Loc.F("Settings.Notify.Result", Describe(direct), scheduled), Loc.T("Settings.Notify.Caption"),
                MessageBoxButton.OK, direct.Outcome == NotificationOutcome.Failed ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }

        private static string Describe(NotificationResult result) => result.Outcome switch
        {
            NotificationOutcome.Shown => Loc.T("Settings.Notify.Shown"),
            NotificationOutcome.TurnedOff => Loc.T("Settings.Notify.Off"),
            _ => Loc.F("Settings.Notify.Failed", result.Detail)
        };

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
                Msg.Show(this, Loc.F("Settings.DataFolder.Failed", AppPaths.DataFolder, ex.Message),
                    Loc.T("Settings.DataFolder.Caption"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
