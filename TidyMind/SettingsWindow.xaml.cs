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

        // The slider applies at once; settings.json is written once it rests, not on every step of a drag.
        private readonly System.Windows.Threading.DispatcherTimer backgroundSave =
            new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };

        // Import a project or collection (MainWindow.ImportPackage); true if something was imported, and Settings then
        // closes so it shows.
        private readonly Func<Window, bool> onImport;

        public SettingsWindow(Action onExportAll, Func<Window, bool> onImport)
        {
            InitializeComponent();
            this.onExportAll = onExportAll;
            this.onImport = onImport;
            VersionText.Text = "TidyMind " + Updates.CurrentVersion.ToString(3);
            DataFolderText.Text = AppPaths.DataFolder;
            DataFolderText.ToolTip = AppPaths.DataFolder;
            BuildLanguageChoices();
            // Taller than a small screen: it scrolls instead of running off it.
            MaxHeight = SystemParameters.WorkArea.Height - 24;
            BackgroundVisibilitySlider.Value = PageBackground.ImageVisibility;
            BackgroundVisibilitySlider.ValueChanged += BackgroundVisibilitySlider_ValueChanged;
            ShowBackgroundVisibility();
            Loc.Track(this, ShowBackgroundVisibility);
            backgroundSave.Tick += (s, e) => SaveBackgroundVisibility();
            Closed += (s, e) =>
            {
                if (backgroundSave.IsEnabled)
                    SaveBackgroundVisibility(); // closed right after a change
            };
            AutoUpdateSwitch.IsChecked = AppSettings.Load().CheckForUpdates != false;
            ShowUpdates();
            Loc.Track(this, ShowUpdates);
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

        private void ImportButton_Click(object sender, RoutedEventArgs e)
        {
            if (onImport(this))
                Close();
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

        // ---- Updates ------------------------------------------------------

        // What the last "Check now" found; until then, when the last check was.
        private Func<string>? updateResult;
        private UpdateRelease? available;

        private void ShowUpdates()
        {
            UpdateVersionText.Text = Loc.F("Settings.Updates.Version", Updates.CurrentVersion.ToString(3));
            UpdateNote.Text = updateResult?.Invoke() ?? LastChecked();
            CheckUpdatesButton.Content = Loc.T(available != null ? "Settings.Updates.Install" : "Settings.Updates.Check");
        }

        private static string LastChecked()
        {
            DateTime? last = AppSettings.Load().LastUpdateCheck;
            if (last == null)
                return Loc.T("Settings.Updates.NeverChecked");
            DateTime local = last.Value.ToLocalTime();
            return Loc.F("Settings.Updates.LastChecked", local.ToString(Loc.T("Format.ShortDate"), Loc.Culture),
                local.ToString("HH:mm", Loc.Culture));
        }

        // Not remembered if settings.json can't be written; the switch then shows what's stored.
        // Every background picture on screen follows the slider as it moves (the open page behind this window too).
        private void BackgroundVisibilitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            int percent = (int)Math.Round(e.NewValue);
            if (percent == PageBackground.ImageVisibility)
                return;
            PageBackground.ImageVisibility = percent;
            ShowBackgroundVisibility();
            backgroundSave.Stop();
            backgroundSave.Start();
        }

        private void ShowBackgroundVisibility() =>
            BackgroundVisibilityText.Text = Loc.F("Settings.BackgroundImage.Value", PageBackground.ImageVisibility);

        // The default isn't written out (null), so a later default can still reach whoever never moved the slider.
        private void SaveBackgroundVisibility()
        {
            backgroundSave.Stop();
            int percent = PageBackground.ImageVisibility;
            AppSettings.Update(s => s.BackgroundImageVisibility = percent == PageBackground.DefaultImageVisibility ? null : percent);
        }

        private void AutoUpdateSwitch_Click(object sender, RoutedEventArgs e)
        {
            bool on = AutoUpdateSwitch.IsChecked == true;
            if (!AppSettings.Update(s => s.CheckForUpdates = on ? null : false))
                AutoUpdateSwitch.IsChecked = AppSettings.Load().CheckForUpdates != false;
            Updates.Note("automatic check turned " + (AppSettings.Load().CheckForUpdates != false ? "on" : "off"));
        }

        // A found update opens in the update dialog over the main window, once this one has closed.
        private async void CheckUpdatesButton_Click(object sender, RoutedEventArgs e)
        {
            if (available != null)
            {
                UpdateRelease release = available;
                Window owner = Owner ?? Application.Current.MainWindow;
                Close();
                _ = Dispatcher.BeginInvoke(new Action(() => Updates.Offer(owner, release)));
                return;
            }

            CheckUpdatesButton.IsEnabled = false;
            UpdateNote.Text = Loc.T("Settings.Updates.Checking");
            UpdateCheckResult result = await Updates.CheckNowAsync();
            available = result.Newer;
            UpdateProblem problem = result.Problem;
            updateResult = () => result.Newer != null ? Loc.F("Settings.Updates.Available", result.Newer.Version.ToString(3))
                : CheckResult(problem);
            CheckUpdatesButton.IsEnabled = true;
            ShowUpdates();
        }

        // Full sentences, one per outcome.
        private static string CheckResult(UpdateProblem problem) => problem switch
        {
            UpdateProblem.None => Loc.T("Settings.Updates.UpToDate"),
            UpdateProblem.Offline => Loc.T("Settings.Updates.Failed.Offline"),
            UpdateProblem.Timeout => Loc.T("Settings.Updates.Failed.Timeout"),
            UpdateProblem.NoRelease => Loc.T("Settings.Updates.Failed.NoRelease"),
            UpdateProblem.RateLimited => Loc.T("Settings.Updates.Failed.RateLimited"),
            UpdateProblem.ServerError => Loc.T("Settings.Updates.Failed.Server"),
            _ => Loc.T("Settings.Updates.Failed.BadAnswer")
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
