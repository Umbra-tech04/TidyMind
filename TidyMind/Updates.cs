using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace TidyMind
{
    // In-app updates. At every start, and about every 6 hours while it runs (unless turned off in Settings), TidyMind
    // asks GitHub for the latest release in the background. A newer one shows the sidebar's "Update available" button
    // (Available) until the user updates or skips it, and pops up in UpdateDialog once the main window is free: in
    // front, no modal dialog, no tour, not being typed in (it waits for that as long as it takes). "Update now"
    // downloads and checks the installer (UpdateClient), closes every window the normal way (each saves), starts the
    // installer silently and closes TidyMind; the installer waits for it to be gone, installs over it in place (same
    // folder, so reminder tasks keep working) and starts TidyMind again (installer\TidyMind.iss, /RELAUNCH=1). Whatever
    // happens goes to updates.log (English). Offline, or anything else failing, the automatic check stays silent.
    public static class Updates
    {
        private const long MaxLogBytes = 256 * 1024;
        // While TidyMind runs, the automatic check is repeated this long after the last one. Looked at every few minutes
        // against the clock, so a PC that slept through it checks soon after it wakes.
        private static readonly TimeSpan RecheckInterval = TimeSpan.FromHours(6);
        private static readonly TimeSpan RecheckTick = TimeSpan.FromMinutes(5);
        // Keys typed into a text box this recently: the user is writing, so the dialog waits.
        private static readonly TimeSpan TypingPause = TimeSpan.FromSeconds(3);
        // How long the start-up waits for an installer that just restarted TidyMind to finish its log.
        private static readonly TimeSpan InstallerLogWait = TimeSpan.FromSeconds(15);
        private static readonly object logLock = new object();

        public static Version CurrentVersion { get; } =
            UpdateRelease.Normalize(typeof(Updates).Assembly.GetName().Version ?? new Version(0, 0, 0));

        // Always UpdateEndpoints.Production in the app. Only the test harness sets another (InternalsVisibleTo); there
        // is no setting, argument or environment variable that changes where updates come from.
        internal static UpdateEndpoints Endpoints { get; set; } = UpdateEndpoints.Production;

        // Offered once per run: "Later" (or closing the dialog) means not again until the next start.
        private static bool offeredThisRun;
        // Found, and waiting for the main window to be idle (one waits; a re-check that finds a newer one replaces it).
        private static UpdateRelease? waiting;
        // When the last automatic check was started (UTC), answered or not; null before the first one.
        private static DateTime? lastAutomaticCheck;
        private static bool checking;
        private static DispatcherTimer? recheckTimer;
        private static long lastKeyTick;
        // The start-up check, sent from App.OnStartup before the data is read and the window built (BeginStartupCheck).
        private static Task<UpdateCheckResult>? earlyCheck;
        // The installer that was started: kept (and so locked) until this process has ended.
        internal static VerifiedInstaller? Started { get; private set; }

        public static string LogPath => AppPaths.Data("updates.log");
        internal static string PendingFile => Path.Combine(Endpoints.DownloadFolder, "pending.json");
        internal static string InstallerLog => Path.Combine(Endpoints.DownloadFolder, "install.log");

        public static UpdateClient NewClient() => new UpdateClient(Endpoints, CurrentVersion);

        // A newer version known in this run and not skipped: the main window shows an "Update available" button for it
        // until TidyMind is updated, or the user skips that version. Null when there's none.
        public static UpdateRelease? Available { get; private set; }
        public static event Action? AvailableChanged;

        // App.OnStartup, as soon as this is the copy that runs: the request goes out in the background now, while the
        // data is read and the window built, so a slow PC doesn't add its start-up time to the answer. Start takes it up.
        public static void BeginStartupCheck()
        {
            if (!ShouldCheckAutomatically(AppSettings.Load()))
                return;
            UpdateClient client = NewClient();
            earlyCheck = Task.Run(() => client.CheckAsync());
        }

        // App.OnStartup, once the main window is up. Nothing here waits on the network or the disk on the UI thread.
        public static void Start(MainWindow main)
        {
            // Only keys count: moving and clicking the mouse never hold the dialog back; typing into a text box does.
            InputManager.Current.PreProcessInput += (s, e) =>
            {
                if (e.StagingItem.Input is KeyboardEventArgs || e.StagingItem.Input is TextCompositionEventArgs)
                    lastKeyTick = Environment.TickCount64;
            };

            Task.Run(() =>
            {
                string? failed = FinishLastUpdate();
                CleanUpDownloads();
                return failed;
            }).ContinueWith(task =>
            {
                if (task.IsFaulted)
                    Note("after update: " + task.Exception?.GetBaseException().Message);
                else if (task.Result != null)
                    WhenIdle(main, () => Msg.Show(main, task.Result, Loc.T("Update.Title"), MessageBoxButton.OK, MessageBoxImage.Warning));
            }, TaskScheduler.FromCurrentSynchronizationContext());

            // Every start, however recently the last check (automatic or "Check now") ran.
            Task<UpdateCheckResult>? sent = earlyCheck;
            earlyCheck = null;
            if (sent != null)
                _ = CheckAutomaticallyAsync(main, sent);
            else if (ShouldCheckAutomatically(AppSettings.Load()))
                _ = CheckAutomaticallyAsync(main);

            if (recheckTimer == null)
            {
                recheckTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = RecheckTick };
                recheckTimer.Tick += (s, e) => RecheckIfDue(main, DateTime.UtcNow);
                recheckTimer.Start();
            }
        }

        internal static bool ShouldCheckAutomatically(AppSettings settings) => settings.CheckForUpdates != false;

        // Six hours after the last automatic check, or none yet (the switch was off at start). A last check "in the
        // future" (the clock was set back) doesn't block it.
        internal static bool IsRecheckDue(DateTime? lastUtc, DateTime nowUtc) =>
            lastUtc == null || lastUtc > nowUtc || nowUtc - lastUtc >= RecheckInterval;

        // Also after "Later": a newer release still updates the button, it just doesn't pop up again in this run.
        internal static void RecheckIfDue(MainWindow main, DateTime nowUtc)
        {
            if (!checking && IsRecheckDue(lastAutomaticCheck, nowUtc) && ShouldCheckAutomatically(AppSettings.Load()))
                _ = CheckAutomaticallyAsync(main);
        }

        // sent: the start-up request already on its way (BeginStartupCheck); otherwise one goes out now.
        internal static async Task CheckAutomaticallyAsync(MainWindow main, Task<UpdateCheckResult>? sent = null)
        {
            if (checking)
                return;
            checking = true;
            lastAutomaticCheck = DateTime.UtcNow;
            try
            {
                UpdateClient client = NewClient();
                UpdateCheckResult result = await (sent ?? Task.Run(() => client.CheckAsync()));
                Record(result, "automatic");
                if (result.Newer == null)
                    return;
                if (IsSkipped(result.Newer.Version))
                {
                    Note("version " + result.Newer.Version.ToString(3) + " was skipped by the user; not offered");
                    return;
                }
                MakeAvailable(result.Newer);
                OfferWhenIdle(main, result.Newer);
            }
            finally
            {
                checking = false;
            }
        }

        // Settings' "Check now": always asks, and a skipped version is offered again (the user asked).
        public static async Task<UpdateCheckResult> CheckNowAsync()
        {
            UpdateClient client = NewClient();
            UpdateCheckResult result = await Task.Run(() => client.CheckAsync());
            Record(result, "manual");
            if (result.Newer != null)
                MakeAvailable(result.Newer);
            return result;
        }

        // The newest one found wins; an older answer (a re-check racing a manual one) never replaces it.
        private static void MakeAvailable(UpdateRelease release)
        {
            if (Available != null && release.Version <= Available.Version)
                return;
            Available = release;
            AvailableChanged?.Invoke();
        }

        // On the UI thread, so the settings file is only ever written from there.
        private static void Record(UpdateCheckResult result, string kind)
        {
            Note("check (" + kind + "): " + (result.Problem == UpdateProblem.None ? "" : result.Problem + ": ") + result.Reason);
            if (result.Answered)
                AppSettings.Update(s => s.LastUpdateCheck = DateTime.UtcNow);
        }

        internal static bool IsSkipped(Version version) =>
            UpdateRelease.TryParseVersion(AppSettings.Load().SkippedUpdate, out Version skipped) && skipped == version;

        // Kept until the main window is free (IsIdle), however long that takes, so it shows as soon as the user is
        // back in the window or done with a dialog.
        internal static void OfferWhenIdle(MainWindow main, UpdateRelease release)
        {
            if (offeredThisRun)
                return;
            bool alreadyWaiting = waiting != null;
            if (waiting == null || release.Version > waiting.Version)
                waiting = release;
            if (alreadyWaiting)
                return;
            WhenIdle(main, () =>
            {
                UpdateRelease? found = waiting;
                waiting = null;
                if (found != null && !offeredThisRun)
                    Offer(main, found);
            });
        }

        // The dialog over `owner`. "Update now" runs to the end inside it (TidyMind closes); "Skip this version" is
        // remembered until a newer one comes; "Later" (or closing it) only lasts until the next start. `ready`: an
        // installer already downloaded and checked, offered again after a window wouldn't close.
        public static void Offer(Window owner, UpdateRelease release, VerifiedInstaller? ready = null)
        {
            offeredThisRun = true;
            UpdateDialog dialog = new UpdateDialog(release, ready) { Owner = owner };
            dialog.ShowDialog();
            if (dialog.WaitingForWindows != null)
            {
                // The dialog has closed so the user can deal with that window (it says why it stayed open). Once it's
                // closed and TidyMind is idle, the update comes back, already downloaded.
                VerifiedInstaller kept = dialog.WaitingForWindows;
                MainWindow main = (MainWindow)Application.Current.MainWindow;
                Msg.Show(main, Loc.T("Update.Error.WindowsOpen"), Loc.T("Update.Title"), MessageBoxButton.OK, MessageBoxImage.Information);
                WhenIdle(main, () => Offer(main, release, kept), noOtherWindows: true);
                return;
            }
            if (dialog.Choice == UpdateChoice.Skip)
            {
                AppSettings.Update(s => s.SkippedUpdate = release.Version.ToString(3));
                Note("version " + release.Version.ToString(3) + " skipped");
                if (Available != null && Available.Version <= release.Version)
                {
                    Available = null; // the button goes until a newer one comes
                    AvailableChanged?.Invoke();
                }
            }
            else if (dialog.Choice == UpdateChoice.Later)
            {
                Note("version " + release.Version.ToString(3) + ": later");
            }
        }

        internal static void ResetForTests()
        {
            offeredThisRun = false;
            waiting = null;
            if (Available != null)
            {
                Available = null;
                AvailableChanged?.Invoke();
            }
        }

        // Runs `action` once the main window is free (IsIdle). Checked every second until then. noOtherWindows: also
        // wait until every other TidyMind window is closed (an update coming back after one wouldn't close).
        internal static void WhenIdle(MainWindow main, Action action, bool noOtherWindows = false)
        {
            DispatcherTimer timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += (s, e) =>
            {
                if (!main.IsLoaded && !main.IsVisible)
                {
                    timer.Stop(); // closed
                    return;
                }
                if (!IsIdle(main) || (noOtherWindows && Application.Current.Windows.OfType<Window>().Any(w => w != main && w.IsVisible)))
                    return;
                timer.Stop();
                action();
            };
            timer.Start();
        }

        // The main window is in front (so the dialog never takes the focus from another app or window), no modal
        // dialog or message box is open, the tour isn't running, no click, drag or open menu is in progress, and nothing
        // is being typed into a text box. Clicking and moving around otherwise never hold it back.
        internal static bool IsIdle(MainWindow main) =>
            main.IsVisible && main.IsActive && main.WindowState != WindowState.Minimized
            && !ComponentDispatcher.IsThreadModal
            && GuidedTour.Current == null
            && Mouse.Captured == null && Mouse.LeftButton == MouseButtonState.Released
            && !IsTyping();

        private static bool IsTyping() =>
            (Keyboard.FocusedElement is TextBoxBase || Keyboard.FocusedElement is PasswordBox)
            && Environment.TickCount64 - lastKeyTick < TypingPause.TotalMilliseconds;

        // ---- Applying -------------------------------------------------------

        // The switches for Inno Setup: no pages or questions, no restart, can't be cancelled half-way, its log here,
        // and TidyMind started again afterwards (also when the install failed: then the old version, still in place).
        internal static IReadOnlyList<string> InstallerArguments(string logPath) => new[]
        {
            "/SILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/NOCANCEL", "/RELAUNCH=1", "/LOG=" + logPath
        };

        internal enum ApplyResult { Started, WindowsOpen, StartFailed }

        // Closes every window but the main one and `dialog` the way the user would (each saves, or stays open and says
        // why), starts the checked installer, and shuts TidyMind down normally (Application.Shutdown: the main window
        // closes, the single-instance lock is released, which the installer waits for). Anything but Started leaves
        // TidyMind running as it was.
        internal static ApplyResult Apply(VerifiedInstaller installer, Window dialog, Action? shutdown = null)
        {
            Window main = Application.Current.MainWindow;
            foreach (Window window in OtherWindows(main, dialog).OrderByDescending(OwnerDepth).ToList())
                window.Close();
            List<Window> left = OtherWindows(main, dialog).ToList();
            if (left.Count > 0)
            {
                Note("apply: " + string.Join(", ", left.Select(w => w.GetType().Name)) + " didn't close; update not started");
                return ApplyResult.WindowsOpen;
            }

            WritePending(installer.Version);
            try
            {
                ProcessStartInfo start = new ProcessStartInfo(installer.Path)
                {
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(installer.Path) ?? ""
                };
                foreach (string argument in InstallerArguments(InstallerLog))
                    start.ArgumentList.Add(argument);
                using (Process.Start(start)) { }
            }
            catch (Exception e) when (e is Win32Exception || e is IOException || e is InvalidOperationException)
            {
                DeleteQuietly(PendingFile);
                Note("apply: the installer couldn't be started: " + e.Message);
                return ApplyResult.StartFailed;
            }

            Started = installer;
            Note("apply: installer for " + installer.Version.ToString(3) + " started (" + string.Join(" ", InstallerArguments(InstallerLog))
                + "); TidyMind " + CurrentVersion.ToString(3) + " closes");
            (shutdown ?? (() => Application.Current.Shutdown()))();
            return ApplyResult.Started;
        }

        private static IEnumerable<Window> OtherWindows(Window main, Window dialog) =>
            Application.Current.Windows.OfType<Window>().Where(w => w != main && w != dialog && (w.IsVisible || w.IsLoaded));

        private static int OwnerDepth(Window window)
        {
            int depth = 0;
            for (Window? w = window.Owner; w != null; w = w.Owner)
                depth++;
            return depth;
        }

        // ---- After an update --------------------------------------------------

        private sealed class Pending
        {
            public string Version { get; set; } = "";
            public string From { get; set; } = "";
            public DateTime Started { get; set; }
        }

        private static void WritePending(Version version)
        {
            Pending pending = new Pending { Version = version.ToString(3), From = CurrentVersion.ToString(3), Started = DateTime.UtcNow };
            DeleteQuietly(InstallerLog);
            if (!AtomicFile.TryWriteAllText(PendingFile, JsonSerializer.Serialize(pending)))
                Note("apply: " + PendingFile + " couldn't be written; the result won't be reported");
        }

        // The update started last time: installed (this is the new version), or not (the old one, which the installer
        // left in place or restored). Logged either way; the message for the user if it failed, else null.
        internal static string? FinishLastUpdate()
        {
            Pending? pending;
            try
            {
                if (!File.Exists(PendingFile))
                    return null;
                pending = JsonSerializer.Deserialize<Pending>(File.ReadAllText(PendingFile));
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is JsonException)
            {
                Note("after update: " + PendingFile + " couldn't be read (" + e.Message + ")");
                DeleteQuietly(PendingFile);
                return null;
            }

            if (pending == null || !UpdateRelease.TryParseVersion(pending.Version, out Version target))
            {
                DeleteQuietly(PendingFile);
                return null;
            }
            if (CurrentVersion >= target)
            {
                Note("after update: " + pending.From + " -> " + CurrentVersion.ToString(3) + " installed");
                DeleteQuietly(PendingFile);
                DeleteQuietly(InstallerLog);
                return null;
            }

            // Started again while the installer is still finishing (it starts TidyMind just before it ends): its log
            // says "Log closed." once it's done. One still running long after it began has failed some other way.
            string log = ReadInstallerLog(waitForEnd: DateTime.UtcNow - pending.Started < TimeSpan.FromMinutes(10));
            if (log.Length > 0 && !log.Contains("Log closed.") && DateTime.UtcNow - pending.Started < TimeSpan.FromMinutes(10))
            {
                Note("after update: the installer for " + pending.Version + " is still running; checked again next start");
                return null;
            }

            string tail = string.Join(Environment.NewLine + "    ",
                log.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(20));
            Note("after update: the update to " + pending.Version + " didn't complete; still running " + CurrentVersion.ToString(3)
                + (tail.Length > 0 ? ". End of the installer's log (" + InstallerLog + "):" + Environment.NewLine + "    " + tail
                    : ". The installer left no log (" + InstallerLog + ")."));
            DeleteQuietly(PendingFile);
            DeleteQuietly(InstallerLog);
            return Loc.F("Update.Failed.AfterRestart", pending.Version, CurrentVersion.ToString(3), LogPath);
        }

        private static string ReadInstallerLog(bool waitForEnd)
        {
            Stopwatch waited = Stopwatch.StartNew();
            while (true)
            {
                string text = "";
                try
                {
                    if (File.Exists(InstallerLog))
                        using (FileStream stream = new FileStream(InstallerLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        using (StreamReader reader = new StreamReader(stream))
                            text = reader.ReadToEnd();
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                }
                if (!waitForEnd || text.Length == 0 || text.Contains("Log closed.") || waited.Elapsed > InstallerLogWait)
                    return text;
                Thread.Sleep(500);
            }
        }

        // Installers (and half-downloaded ones) from earlier runs. One still in use (an installer running right now)
        // stays; the next start takes it.
        internal static void CleanUpDownloads()
        {
            try
            {
                if (!Directory.Exists(Endpoints.DownloadFolder))
                    return;
                foreach (string file in Directory.EnumerateFiles(Endpoints.DownloadFolder, "TidyMind-Setup-*"))
                    DeleteQuietly(file);
                if (!File.Exists(PendingFile))
                    DeleteQuietly(InstallerLog);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Note("clean-up of " + Endpoints.DownloadFolder + ": " + e.Message);
            }
        }

        private static void DeleteQuietly(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
            }
        }

        // ---- updates.log --------------------------------------------------------

        // One line per check, download and install step: versions, outcomes and reasons, nothing about the user's data.
        // Best effort, rolled over at 256 KB (one old copy kept).
        internal static void Note(string text)
        {
            try
            {
                lock (logLock)
                {
                    Directory.CreateDirectory(AppPaths.DataFolder);
                    FileInfo log = new FileInfo(LogPath);
                    if (log.Exists && log.Length > MaxLogBytes)
                        File.Move(LogPath, AppPaths.Data("updates.old.log"), overwrite: true);
                    File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + CurrentVersion.ToString(3)
                        + "  " + text + Environment.NewLine);
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
            }
        }
    }
}
