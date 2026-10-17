using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using Microsoft.Win32.TaskScheduler;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace TidyMind
{
    public static class ReminderManager
    {
        private static readonly string RemindersFile = AppPaths.Data("reminders.json");
        private const string TaskNamePrefix = "TidyMind_Reminder_";
        private const string TestTaskPrefix = "TidyMind_NotificationTest_";

        public static List<Reminder> LoadReminders()
        {
            if (!File.Exists(RemindersFile))
                return new List<Reminder>();

            string json = File.ReadAllText(RemindersFile);
            return JsonSerializer.Deserialize<List<Reminder>>(json) ?? new List<Reminder>();
        }

        // For views that only display reminders: an unreadable file shows as none instead of crashing them.
        // Showing reminders is also when their tasks get checked (throttled, in the background).
        public static List<Reminder> TryLoadReminders()
        {
            try
            {
                List<Reminder> reminders = LoadReminders();
                RepairTasksInBackground();
                return reminders;
            }
            catch (Exception)
            {
                return new List<Reminder>();
            }
        }

        // Written via a temporary file, so a crash or full disk mid-save can't truncate every reminder at once.
        // Throws IOException if it couldn't be written (the file is then unchanged). Only called under Locked.
        private static void SaveReminders(List<Reminder> reminders)
        {
            string json = JsonSerializer.Serialize(reminders);
            if (!AtomicFile.TryWriteAllText(RemindersFile, json))
                throw new IOException("reminders.json couldn't be written.");
        }

        public static void AddReminder(Reminder reminder) => SaveAndSchedule(reminder);

        public static void UpdateReminder(Reminder reminder) => SaveAndSchedule(reminder);

        // Adds the reminder, or replaces the stored one with the same Id (so retrying an add never duplicates it).
        // The task is scheduled first and the file saved second; if the save fails, the task is put back the way
        // the file still has it. So a reminder is never stored without its task, and a failure leaves both as they
        // were. Registering under the same name overwrites the old trigger.
        private static void SaveAndSchedule(Reminder reminder)
        {
            Locked(() =>
            {
                List<Reminder> reminders = LoadReminders();
                int index = reminders.FindIndex(r => r.Id == reminder.Id);
                Reminder previous = index >= 0 ? reminders[index] : null;
                if (previous == null)
                {
                    reminders.Add(reminder);
                }
                else
                {
                    // The form builds a new Reminder: fields this version doesn't know come over from the stored one.
                    reminder.ExtensionData ??= previous.ExtensionData;
                    reminders[index] = reminder;
                }

                RegisterReminderTask(reminder);
                try
                {
                    SaveReminders(reminders);
                }
                catch (IOException)
                {
                    TrySchedule(() =>
                    {
                        if (previous != null)
                            RegisterReminderTask(previous);
                        else
                            RemoveReminderTask(reminder.Id);
                    });
                    Note("save failed for " + reminder.Id + "; its task was put back");
                    throw;
                }
                Note((previous == null ? "added " : "updated ") + Describe(reminder) + "; file now has " + reminders.Count);
            });
        }

        public static void DeleteReminder(Guid id)
        {
            Locked(() =>
            {
                List<Reminder> reminders = LoadReminders();
                reminders.RemoveAll(r => r.Id == id);
                SaveReminders(reminders);
                Note("deleted " + id + "; file now has " + reminders.Count);

                RemoveReminderTask(id);
            });
        }

        // ---- What happened (reminders.log) ---------------------------------

        // One line per decision about a reminder, from the app and from every "--remind" process: ids, repeat types
        // and times only, never the message. So a reminder that did something unexpected can be traced afterwards.
        // Best effort, rolled over at 256 KB (one old copy kept).
        private const long MaxNoteBytes = 256 * 1024;

        private static void Note(string text)
        {
            try
            {
                string path = AppPaths.Data("reminders.log");
                FileInfo log = new FileInfo(path);
                if (log.Exists && log.Length > MaxNoteBytes)
                    File.Move(path, AppPaths.Data("reminders.old.log"), overwrite: true);
                File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  pid " + Environment.ProcessId
                    + "  " + text + Environment.NewLine);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
            }
        }

        private static string Describe(Reminder reminder) =>
            reminder.Id + " (" + reminder.RepeatType + ", due " + reminder.NextFireTime.ToString("yyyy-MM-dd HH:mm") + ")";

        // ---- Cross-process lock -------------------------------------------

        // The open app and every "--remind" process (two reminders due the same minute start two) change
        // reminders.json. Each change is load, modify, save (and the task change that goes with it) under this one
        // lock, with the file read again inside it, so no writer can save over another's change.
        private const string LockName = @"Local\TidyMind_Reminders";
        private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);

        // Throws IOException if the lock isn't free within the timeout (callers already report that as "can't be
        // read or written"; the reminder process retries later).
        private static void Locked(System.Action change)
        {
            using (Mutex gate = new Mutex(false, LockName))
            {
                bool owned;
                try
                {
                    owned = gate.WaitOne(LockTimeout);
                }
                catch (AbandonedMutexException)
                {
                    owned = true; // its last holder died mid-change; the file itself is intact (AtomicFile)
                }
                if (!owned)
                    throw new IOException("reminders.json is busy: another TidyMind process is changing it.");

                try
                {
                    change();
                }
                finally
                {
                    gate.ReleaseMutex();
                }
            }
        }

        // ---- Keeping the tasks right ---------------------------------------

        private static readonly TimeSpan RepairInterval = TimeSpan.FromMinutes(10);
        private static readonly object repairGate = new object();
        private static DateTime lastRepair = DateTime.MinValue;
        private static bool repairRunning;

        // Checks every reminder's task off the UI thread: at app start (force), and at most every few minutes when
        // reminders are shown. Best effort: a failure is logged and tried again next time.
        public static void RepairTasksInBackground(bool force = false)
        {
            lock (repairGate)
            {
                if (repairRunning || (!force && DateTime.UtcNow - lastRepair < RepairInterval))
                    return;
                repairRunning = true;
                lastRepair = DateTime.UtcNow;
            }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    RepairTasks();
                }
                catch (Exception e)
                {
                    // Background upkeep must never take the app down.
                    UnhandledErrors.LogQuietly("reminder task repair", e);
                }
                finally
                {
                    lock (repairGate)
                        repairRunning = false;
                }
            });
        }

        // A task can be missing (scheduling failed, or the app was reinstalled), point at an exe that isn't there
        // any more (the TidyMind folder moved, another build), or be left over for a deleted reminder. Each
        // reminder gets a task that starts this exe at its time (one already due fires within a minute); tasks
        // without a reminder go.
        private static void RepairTasks()
        {
            string exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
                return;

            Locked(() =>
            {
                List<Reminder> reminders = LoadReminders();
                Dictionary<Guid, Reminder> byId = reminders.GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First());
                HashSet<Guid> healthy = new HashSet<Guid>();
                List<Guid> orphaned = new List<Guid>();

                using (TaskService taskService = new TaskService())
                {
                    foreach (Microsoft.Win32.TaskScheduler.Task task in taskService.RootFolder.Tasks)
                    {
                        using (task)
                        {
                            if (!task.Name.StartsWith(TaskNamePrefix, StringComparison.OrdinalIgnoreCase)
                                || !Guid.TryParse(task.Name.Substring(TaskNamePrefix.Length), out Guid id))
                                continue;

                            if (!byId.TryGetValue(id, out Reminder reminder))
                                orphaned.Add(id);
                            else if (IsHealthy(task, reminder, exePath))
                                healthy.Add(id);
                        }
                    }
                }

                foreach (Guid id in orphaned)
                {
                    Note("repair: task for " + id + " has no reminder; removed");
                    TrySchedule(() => RemoveReminderTask(id));
                }

                DateTime soon = DateTime.Now.AddMinutes(1);
                foreach (Reminder reminder in byId.Values.Where(r => !healthy.Contains(r.Id)))
                {
                    DateTime when = reminder.NextFireTime > soon ? reminder.NextFireTime : soon;
                    Note("repair: " + Describe(reminder) + " had no task, a wrong one, or one that failed to start TidyMind; scheduled for "
                        + when.ToString("yyyy-MM-dd HH:mm"));
                    TrySchedule(() => RegisterTask(reminder.Id, when, "TidyMind reminder: " + reminder.Title));
                }
            });
        }

        // Starts this exe for this reminder at its stored time. One whose time has passed counts as right when the
        // task otherwise is: it has run or will (StartWhenAvailable), and moving the reminder on is its job. Unless it
        // ran at that time and failed to start TidyMind (say, while an update was replacing the program files): the
        // reminder never moved on, so it's due again.
        private static bool IsHealthy(Microsoft.Win32.TaskScheduler.Task task, Reminder reminder, string exePath)
        {
            TaskDefinition definition = task.Definition;
            ExecAction action = definition.Actions.OfType<ExecAction>().FirstOrDefault();
            if (action == null || !SamePath(action.Path, exePath)
                || action.Arguments == null || !action.Arguments.Contains(reminder.Id.ToString(), StringComparison.OrdinalIgnoreCase))
                return false;

            if (reminder.NextFireTime <= DateTime.Now)
                return !(task.LastRunTime >= reminder.NextFireTime.AddMinutes(-1) && task.LastTaskResult != 0 && task.State != TaskState.Running);

            TimeTrigger trigger = definition.Triggers.OfType<TimeTrigger>().FirstOrDefault();
            return trigger != null && Math.Abs((trigger.StartBoundary - reminder.NextFireTime).TotalSeconds) < 1;
        }

        private static bool SamePath(string a, string b)
        {
            try
            {
                return string.Equals(Path.GetFullPath(a.Trim('"')), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception e) when (e is ArgumentException || e is NotSupportedException || e is PathTooLongException)
            {
                return false;
            }
        }

        // Called by the app when it is launched as "TidyMind.exe --remind {id}"
        // by the Windows Task Scheduler task for this reminder.
        // The task is one-shot, so this is the only place a repeating reminder's next run gets scheduled: nothing
        // here may fail before that happens. reminders.json can be briefly locked by the open app, so reads are
        // retried; if it still can't be read, the task is put back a few minutes later instead of being dropped.
        public static void FireReminder(Guid id)
        {
            Reminder due = null;
            try
            {
                Locked(() => due = MoveOn(id));
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is JsonException)
            {
                Note("fire " + id + ": " + e.Message + "; trying again in 5 minutes");
                RetryLater(id);
                return;
            }

            if (due == null)
                return;

            ShowNotification(due);
        }

        // Under the lock: the reminder this task is for, with the file and its task already moved on (a one-off
        // removed, a repeating one stepped to its next time); null if there's nothing to show.
        private static Reminder MoveOn(Guid id)
        {
            List<Reminder> reminders = WithRetries(LoadReminders);
            Reminder reminder = reminders.Find(r => r.Id == id);
            if (reminder == null)
            {
                Note("fire " + id + ": not in reminders.json (" + reminders.Count + " there); task removed, nothing shown");
                TrySchedule(() => RemoveReminderTask(id));
                return null;
            }

            // A trigger left over from before the reminder was moved to a later time: just put the task back on time.
            if (reminder.NextFireTime > DateTime.Now.AddMinutes(1))
            {
                Note("fire " + Describe(reminder) + ": not due yet; task put back, nothing shown");
                TrySchedule(() => RegisterReminderTask(reminder));
                return null;
            }

            // A repeat type this version doesn't know (from a newer TidyMind, or an edited file) has no next time
            // it could compute: it fires as a one-off instead of looping.
            bool once = !Repeats(reminder.RepeatType);
            if (once && reminder.RepeatType != RepeatType.Once)
                UnhandledErrors.LogQuietly("reminder", "Unknown repeat type " + (int)reminder.RepeatType + " on reminder "
                    + reminder.Id + "; it was treated as a one-off.");

            string before = Describe(reminder);
            if (once)
                reminders.Remove(reminder);
            else
                reminder.NextFireTime = NextFutureFireTime(reminder.NextFireTime, reminder.RepeatType, DateTime.Now);

            // Scheduled even if the save fails: a repeating reminder then still fires next time (and is stepped on
            // from its old stored time); a finished one-off is left in the list rather than shown twice.
            try
            {
                SaveReminders(reminders);
                Note("fire " + before + ": " + (once ? "one-off done, removed" : "moved on to "
                    + reminder.NextFireTime.ToString("yyyy-MM-dd HH:mm")) + "; file now has " + reminders.Count);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Note("fire " + before + ": shown, but the save failed (" + e.Message + ")");
            }
            TrySchedule(once ? () => RemoveReminderTask(id) : () => RegisterReminderTask(reminder));
            return reminder;
        }

        private static bool Repeats(RepeatType repeat) =>
            repeat == RepeatType.Daily || repeat == RepeatType.Weekly || repeat == RepeatType.Monthly || repeat == RepeatType.Yearly;

        // The reminder couldn't be handled now (its file unreadable, or the data folder not ready): run its task
        // again in a few minutes rather than lose it.
        public static void RetryLater(Guid id)
        {
            TrySchedule(() => RegisterTask(id, DateTime.Now.Add(RetryDelay), "TidyMind reminder (retry)"));
        }

        private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(5);
        private const int ReadAttempts = 5;
        private const int ReadRetryDelayMs = 200;

        private static T WithRetries<T>(Func<T> read)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    return read();
                }
                catch (Exception e) when ((e is IOException || e is UnauthorizedAccessException) && attempt < ReadAttempts)
                {
                    Thread.Sleep(ReadRetryDelayMs);
                }
            }
        }

        // Task Scheduler refusing a change must not end the reminder process before the rest has run.
        private static void TrySchedule(System.Action change)
        {
            try
            {
                change();
            }
            catch (Exception e) when (e is COMException || e is UnauthorizedAccessException || e is IOException)
            {
            }
        }

        // The first repeat after `now`: after the PC was off or asleep through several, the reminder fires once and
        // the missed ones are skipped. Stepped from the previous time, not from now, so the time of day is kept and
        // month ends step the way CalendarService.OccursOn expects (Jan 31 -> Feb 28 -> Mar 28).
        private static DateTime NextFutureFireTime(DateTime previous, RepeatType repeat, DateTime now)
        {
            if (!Repeats(repeat))
                return previous; // no step to take: looping on it would never end

            DateTime next = previous;
            do
                next = GetNextFireTime(next, repeat);
            while (next <= now);
            return next;
        }

        private static DateTime GetNextFireTime(DateTime current, RepeatType repeat)
        {
            switch (repeat)
            {
                case RepeatType.Daily: return current.AddDays(1);
                case RepeatType.Weekly: return current.AddDays(7);
                case RepeatType.Monthly: return current.AddMonths(1);
                case RepeatType.Yearly: return current.AddYears(1);
                default: return current;
            }
        }

        // ---- The notification ---------------------------------------------

        // Windows reports a notification it couldn't show within moments of Show; a shown one reports nothing, so
        // the reminder process always waits this long before it exits.
        private static readonly TimeSpan NotificationFailureWait = TimeSpan.FromSeconds(3);

        // "Element not found": what Windows answers for a sender it doesn't know (yet).
        private const int ElementNotFound = unchecked((int)0x80070490);

        // The reminder's Windows notification. If Windows didn't take it, the reminder still shows: in the open
        // TidyMind window, or else in a window of this process (ReminderPopups). Notifications the user turned off for
        // TidyMind in Windows stay off.
        private static void ShowNotification(Reminder due)
        {
            // In the saved language (App reads it before a reminder fires); reminders.log stays in English.
            string title = string.IsNullOrWhiteSpace(due.Title) ? Loc.T("Reminder.Toast.Title")
                : Loc.F("Reminder.Toast.TitleFor", DayPlanReminders.ShownTitle(due, DayPlanReminders.BlockIds()));
            string message = due.Message ?? "";

            NotificationResult result = SendToast(title, message);
            if (result.Outcome != NotificationOutcome.Failed)
            {
                Note("fire " + due.Id + ": " + result);
                return;
            }
            bool handed = ReminderPopups.HandToApp(title, message);
            Note("fire " + due.Id + ": " + result + "; " + (handed ? "shown in the open TidyMind window instead" : "shown in a TidyMind window instead"));
            if (!handed)
                ReminderPopups.ShowHere(title, message);
        }

        // One Windows notification from App.UserModelId: a banner, then kept in the Action Center. Windows takes it only
        // for an ID a Start Menu shortcut carries (the installer's). Never throws: the outcome says what happened.
        public static NotificationResult SendToast(string title, string message)
        {
            string where = "session " + Process.GetCurrentProcess().SessionId + (Environment.UserInteractive ? "" : ", not interactive");
            try
            {
                ToastNotifier notifier = ToastNotificationManager.CreateToastNotifier(App.UserModelId);

                // Read for the log, never as a gate: Windows 10 throws "Element not found" here for an app that has
                // never shown a notification (Windows lists it under Settings > Notifications only after its first
                // one), so checking first kept that first one, and every later one, from being sent.
                NotificationSetting? setting = null;
                string settingText;
                try
                {
                    setting = notifier.Setting;
                    settingText = setting.ToString();
                }
                catch (Exception e)
                {
                    settingText = "not readable (" + Code(e) + ")";
                }

                string problem = SendNotification(notifier, title, message);
                if (problem != null)
                    return new NotificationResult(NotificationOutcome.Failed, problem + "; setting " + settingText + "; " + where);
                if (setting != null && setting != NotificationSetting.Enabled)
                    return new NotificationResult(NotificationOutcome.TurnedOff, "turned off in Windows (" + settingText + "); " + where);
                return new NotificationResult(NotificationOutcome.Shown, "notification sent; setting " + settingText + "; " + where);
            }
            catch (Exception e)
            {
                // Whatever Windows threw (a COMException mostly). "Element not found" comes without a message and has
                // more than one cause, so it's described, not diagnosed.
                string what = e.HResult == ElementNotFound
                    ? "Element not found: Windows doesn't know the sender " + App.UserModelId + " (no Start Menu shortcut with that ID yet, or the notification platform isn't available here)"
                    : e.Message;
                return new NotificationResult(NotificationOutcome.Failed, "the Windows notification failed (" + Code(e) + " " + what + "); " + where);
            }
        }

        private static string Code(Exception e) => e.GetType().Name + " 0x" + e.HResult.ToString("X8");

        // Null once shown; otherwise what Windows reported.
        private static string SendNotification(ToastNotifier notifier, string title, string message)
        {
            // Filled in as text nodes, never as markup: a title can hold any character.
            XmlDocument content = new XmlDocument();
            content.LoadXml("<toast><visual><binding template=\"ToastGeneric\"><text/><text/></binding></visual></toast>");
            XmlNodeList lines = content.GetElementsByTagName("text");
            lines[0].AppendChild(content.CreateTextNode(title));
            lines[1].AppendChild(content.CreateTextNode(message));

            ToastNotification toast = new ToastNotification(content);
            TaskCompletionSource<string> failed = new TaskCompletionSource<string>();
            toast.Failed += (sender, args) =>
                failed.TrySetResult("0x" + args.ErrorCode.HResult.ToString("X8") + ": " + args.ErrorCode.Message);
            notifier.Show(toast);
            return failed.Task.Wait(NotificationFailureWait) ? failed.Task.Result : null;
        }

        // ---- Testing the notification path ---------------------------------

        // Where "TidyMind.exe --test-notification <token>" writes its outcome for the app to read (same user, same
        // temp folder); nothing goes into the data folder.
        public static string TestResultFile(Guid token) =>
            Path.Combine(Path.GetTempPath(), "tidymind-notification-test-" + token.ToString("N") + ".txt");

        // The real path of a reminder: a one-off task with the same settings, user and session as a reminder's,
        // starting this exe. Throws if Task Scheduler refuses it.
        public static void ScheduleTest(Guid token, DateTime at)
        {
            RegisterTask(TestTaskPrefix + token, at, "TidyMind notification test", "--test-notification \"" + token + "\"");
            Note("test " + token + ": task scheduled for " + at.ToString("HH:mm:ss"));
        }

        public static void RemoveTest(Guid token)
        {
            TrySchedule(() =>
            {
                using (TaskService taskService = new TaskService())
                    taskService.RootFolder.DeleteTask(TestTaskPrefix + token, false);
            });
            try { File.Delete(TestResultFile(token)); } catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
        }

        // The task's side: one test notification, its outcome to reminders.log and to the result file.
        public static void RunTest(Guid token)
        {
            NotificationResult result = SendToast(Loc.T("Notify.Test.Title"), Loc.T("Notify.Test.Scheduled"));
            Note("test " + token + " (scheduled task): " + result);
            AtomicFile.TryWriteAllText(TestResultFile(token), result.Outcome + "\n" + result.Detail);
        }

        // The app's side, for Settings: the same notification straight from this process.
        public static NotificationResult TestNow()
        {
            NotificationResult result = SendToast(Loc.T("Notify.Test.Title"), Loc.T("Notify.Test.Direct"));
            Note("test (from the app): " + result);
            return result;
        }

        // The outcome the task wrote, or null while there's none yet.
        public static NotificationResult? ReadTestResult(Guid token)
        {
            try
            {
                string[] lines = File.ReadAllLines(TestResultFile(token));
                return lines.Length >= 2 && Enum.TryParse(lines[0], out NotificationOutcome outcome)
                    ? new NotificationResult(outcome, lines[1]) : null;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static string GetTaskName(Guid id)
        {
            return TaskNamePrefix + id;
        }

        // Every reminder (Once or repeating) is scheduled as a single one-shot
        // TimeTrigger at NextFireTime. When the app fires it (FireReminder above),
        // repeating reminders compute their next occurrence and re-register the
        // task with the new time. This sidesteps Windows Task Scheduler's built-in
        // repetition limits (its "repeat every" pattern tops out at 31 days, which
        // can't express Monthly/Yearly), and works identically for every repeat type.
        private static void RegisterReminderTask(Reminder reminder)
        {
            RegisterTask(reminder.Id, reminder.NextFireTime, "TidyMind reminder: " + reminder.Title);
        }

        private static void RegisterTask(Guid id, DateTime fireTime, string description) =>
            RegisterTask(GetTaskName(id), fireTime, description, "--remind \"" + id + "\"");

        // Throws if it can't be scheduled, so callers never take an unscheduled reminder for a scheduled one.
        private static void RegisterTask(string name, DateTime fireTime, string description, string arguments)
        {
            string exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
                throw new IOException("TidyMind's own location couldn't be found, so the reminder can't be scheduled.");

            using (TaskService taskService = new TaskService())
            {
                TaskDefinition taskDefinition = taskService.NewTask();
                taskDefinition.RegistrationInfo.Description = description;
                // The signed-in user's own session, only while signed in: a notification (or window) can only show
                // there. (Task Scheduler's default for a task without a password, written out so it can't drift.)
                taskDefinition.Principal.UserId = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
                taskDefinition.Principal.LogonType = TaskLogonType.InteractiveToken;
                taskDefinition.Principal.RunLevel = TaskRunLevel.LUA;

                taskDefinition.Triggers.Clear();
                taskDefinition.Triggers.Add(new TimeTrigger(fireTime));

                taskDefinition.Actions.Clear();
                taskDefinition.Actions.Add(new ExecAction(exePath, arguments, Path.GetDirectoryName(exePath)));

                taskDefinition.Settings.DisallowStartIfOnBatteries = false;
                taskDefinition.Settings.StopIfGoingOnBatteries = false;
                taskDefinition.Settings.StartWhenAvailable = true;
                // Firing takes seconds; the limit only stops a process that got stuck (it can't loop on a reminder,
                // see Repeats, but nothing else should run forever either).
                taskDefinition.Settings.ExecutionTimeLimit = TimeSpan.FromMinutes(10);

                taskService.RootFolder.RegisterTaskDefinition(name, taskDefinition);
            }
        }

        // The uninstaller (TidyMind.exe --remove-reminder-tasks): deletes every TidyMind_Reminder_* task, so none is left
        // pointing at a removed exe. Nothing else changes: reminders.json stays, and the next start re-creates the tasks
        // (RepairTasks). Throws if the tasks can't be read or deleted.
        public static void RemoveAllReminderTasks()
        {
            using (TaskService taskService = new TaskService())
            {
                List<string> names = new List<string>();
                foreach (Microsoft.Win32.TaskScheduler.Task task in taskService.RootFolder.Tasks)
                {
                    using (task)
                    {
                        if (task.Name.StartsWith(TaskNamePrefix, StringComparison.OrdinalIgnoreCase)
                            || task.Name.StartsWith(TestTaskPrefix, StringComparison.OrdinalIgnoreCase))
                            names.Add(task.Name);
                    }
                }
                foreach (string name in names)
                    taskService.RootFolder.DeleteTask(name, false);
            }
        }

        private static void RemoveReminderTask(Guid id)
        {
            using (TaskService taskService = new TaskService())
            {
                taskService.RootFolder.DeleteTask(GetTaskName(id), false);
            }
        }
    }
}
