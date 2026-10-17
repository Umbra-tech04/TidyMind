using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace TidyMind
{
    public enum UpdateChoice { Later, Skip, Update }

    // "Update available": the new version and its notes, then (after "Update now") the download with a green bar,
    // the check, and the hand-over to the installer (Updates.Apply). A failure says what happened, offers Retry, and
    // leaves TidyMind running as it was.
    public partial class UpdateDialog : Window
    {
        private enum Stage { Offer, Download, Verify, Install, Error }

        private readonly UpdateRelease release;
        private Stage stage = Stage.Offer;
        private DownloadProgress lastProgress;
        private Func<string> errorText = () => "";
        private CancellationTokenSource? download;
        // Downloaded and checked: kept for a Retry after the installer couldn't be started, and handed back to Updates
        // when a window wouldn't close, so it isn't downloaded again.
        private VerifiedInstaller? verified;

        public UpdateDialog(UpdateRelease release, VerifiedInstaller? ready = null)
        {
            InitializeComponent();
            this.release = release;
            verified = ready;
            NotesBox.Text = release.Notes;
            NotesSection.Visibility = release.Notes.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            ShowStage(Stage.Offer);
            Loc.Track(this, () => ShowStage(stage));
            Closed += (s, e) =>
            {
                // Neither started nor kept for later: the file goes at the next start (Updates.CleanUpDownloads).
                if (Updates.Started != verified && WaitingForWindows == null)
                    verified?.Dispose();
            };
        }

        public UpdateChoice Choice { get; private set; } = UpdateChoice.Later;

        // Set when a window wouldn't close: the dialog closed so the user can deal with it (Updates.Offer).
        public VerifiedInstaller? WaitingForWindows { get; private set; }

        // What the harness looks at.
        internal string Status => StatusText.Text;
        internal string Error => ErrorText.Text;
        internal bool IsBusy => stage == Stage.Download || stage == Stage.Verify || stage == Stage.Install;
        internal bool HasInstaller => verified != null;
        internal Version Version => release.Version;

        private void ShowStage(Stage next)
        {
            stage = next;
            VersionsText.Text = Loc.F("Update.Versions", Updates.CurrentVersion.ToString(3), release.Version.ToString(3));

            bool offer = next == Stage.Offer;
            bool error = next == Stage.Error;
            OfferPanel.Visibility = offer ? Visibility.Visible : Visibility.Collapsed;
            ProgressPanel.Visibility = offer || error ? Visibility.Collapsed : Visibility.Visible;
            ErrorPanel.Visibility = error ? Visibility.Visible : Visibility.Collapsed;
            SkipButton.Visibility = offer ? Visibility.Visible : Visibility.Collapsed;
            LaterButton.Visibility = offer ? Visibility.Visible : Visibility.Collapsed;
            UpdateButton.Visibility = offer ? Visibility.Visible : Visibility.Collapsed;
            CancelButton.Visibility = next == Stage.Download ? Visibility.Visible : Visibility.Collapsed;
            CloseButton.Visibility = error ? Visibility.Visible : Visibility.Collapsed;
            RetryButton.Visibility = error ? Visibility.Visible : Visibility.Collapsed;
            RetryButton.IsDefault = error;
            UpdateButton.IsDefault = offer;

            ErrorText.Text = errorText();
            StatusText.Text = next switch
            {
                Stage.Download => Loc.T("Update.Status.Download"),
                Stage.Verify => Loc.T("Update.Status.Verify"),
                Stage.Install => Loc.T("Update.Status.Install"),
                _ => ""
            };
            // Restarting is never current here: it happens after TidyMind has closed.
            TextBlock[] steps = { StepDownload, StepVerify, StepInstall, StepRestart };
            int current = next == Stage.Download ? 0 : next == Stage.Verify ? 1 : next == Stage.Install ? 2 : -1;
            for (int i = 0; i < steps.Length; i++)
            {
                steps[i].Foreground = (System.Windows.Media.Brush)FindResource(i < current ? "Success" : i == current ? "TextMain" : "TextSub");
                steps[i].FontWeight = i == current ? FontWeights.SemiBold : FontWeights.Normal;
            }
            ShowProgress(next == Stage.Download ? lastProgress : new DownloadProgress(1, 1));
        }

        private void ShowProgress(DownloadProgress progress)
        {
            if (stage == Stage.Download)
                lastProgress = progress;
            double fraction = progress.Total > 0 ? Math.Clamp((double)progress.Received / progress.Total, 0, 1) : 0;
            DownloadBar.Value = fraction * 100;
            ProgressText.Text = stage == Stage.Download
                ? Loc.F("Update.Progress", (int)Math.Floor(fraction * 100), progress.Received / 1048576.0, progress.Total / 1048576.0)
                : "";
        }

        private async void UpdateButton_Click(object sender, RoutedEventArgs e)
        {
            if (IsBusy)
                return;
            Choice = UpdateChoice.Update;
            if (verified == null)
            {
                lastProgress = new DownloadProgress(0, release.Installer.Size);
                ShowStage(Stage.Download);
                download = new CancellationTokenSource();
                Progress<DownloadProgress> progress = new Progress<DownloadProgress>(p =>
                {
                    if (stage == Stage.Download)
                        ShowProgress(p);
                });
                UpdateClient client = Updates.NewClient();
                CancellationToken token = download.Token;
                try
                {
                    verified = await Task.Run(() => client.DownloadAsync(release, progress,
                        () => Dispatcher.BeginInvoke(new Action(() => ShowStage(Stage.Verify))), token));
                    Updates.Note("download: " + release.Installer.Name + " verified (" + release.Installer.Size + " bytes)");
                }
                catch (UpdateFailure failure)
                {
                    Updates.Note("download: " + failure.Problem + ": " + failure.Message);
                    if (failure.Problem == UpdateProblem.Cancelled)
                    {
                        Choice = UpdateChoice.Later;
                        if (IsLoaded)
                            ShowStage(Stage.Offer);
                        return;
                    }
                    Fail(() => DownloadError(failure.Problem));
                    return;
                }
                catch (Exception unexpected)
                {
                    Updates.Note("download: unexpected " + unexpected);
                    Fail(() => Loc.T("Update.Error.Unexpected"));
                    return;
                }
                finally
                {
                    download.Dispose();
                    download = null;
                }
                if (!IsLoaded)
                {
                    verified.Dispose(); // closed meanwhile
                    verified = null;
                    return;
                }
            }

            ShowStage(Stage.Install);
            // Long enough to read before the windows close.
            await Task.Delay(800);
            Updates.ApplyResult result;
            try
            {
                result = Updates.Apply(verified, this);
            }
            catch (Exception unexpected)
            {
                Updates.Note("apply: unexpected " + unexpected);
                Fail(() => Loc.T("Update.Error.Unexpected"));
                return;
            }
            if (result == Updates.ApplyResult.WindowsOpen)
            {
                WaitingForWindows = verified;
                stage = Stage.Offer; // so it may close
                Close();
            }
            else if (result == Updates.ApplyResult.StartFailed)
            {
                Fail(() => Loc.T("Update.Error.StartFailed"));
            }
        }

        private void Fail(Func<string> text)
        {
            errorText = text;
            ShowStage(Stage.Error);
        }

        // Full sentences, one per kind of failure; TidyMind keeps running in every case.
        private static string DownloadError(UpdateProblem problem) => problem switch
        {
            UpdateProblem.Offline => Loc.T("Update.Error.Offline"),
            UpdateProblem.Timeout => Loc.T("Update.Error.Timeout"),
            UpdateProblem.ForeignHost => Loc.T("Update.Error.ForeignHost"),
            UpdateProblem.SizeMismatch => Loc.T("Update.Error.SizeMismatch"),
            UpdateProblem.HashMismatch => Loc.T("Update.Error.HashMismatch"),
            UpdateProblem.CantSave => Loc.F("Update.Error.CantSave", Updates.Endpoints.DownloadFolder),
            UpdateProblem.RateLimited => Loc.T("Update.Error.Server"),
            UpdateProblem.ServerError => Loc.T("Update.Error.ServerFailed"),
            _ => Loc.T("Update.Error.BadAnswer")
        };

        private void CancelButton_Click(object sender, RoutedEventArgs e) => download?.Cancel();

        private void LaterButton_Click(object sender, RoutedEventArgs e)
        {
            Choice = UpdateChoice.Later;
            Close();
        }

        private void SkipButton_Click(object sender, RoutedEventArgs e)
        {
            Choice = UpdateChoice.Skip;
            Close();
        }

        // Esc: Later, Cancel during the download, Close after an error. Nothing once the hand-over has begun.
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Key != Key.Escape || e.Handled)
                return;
            e.Handled = true;
            if (stage == Stage.Download)
                download?.Cancel();
            else if (!IsBusy)
                LaterButton_Click(this, e);
        }

        // Closing during the download stops it; during the check and the hand-over it waits (TidyMind closes anyway).
        protected override void OnClosing(CancelEventArgs e)
        {
            if (stage == Stage.Verify || stage == Stage.Install)
            {
                e.Cancel = true;
                return;
            }
            if (stage == Stage.Download)
            {
                download?.Cancel();
                Choice = UpdateChoice.Later;
            }
            base.OnClosing(e);
        }
    }
}
