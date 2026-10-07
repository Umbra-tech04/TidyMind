using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32.TaskScheduler;

namespace TidyMind
{
    public static class ReminderManager
    {
        private static readonly string RemindersFile = AppPaths.Data("reminders.json");
        private const string TaskNamePrefix = "TidyMind_Reminder_";

        private static NotifyIcon trayIcon;

        public static List<Reminder> LoadReminders()
        {
            if (!File.Exists(RemindersFile))
                return new List<Reminder>();

            string json = File.ReadAllText(RemindersFile);
            return JsonSerializer.Deserialize<List<Reminder>>(json) ?? new List<Reminder>();
        }

        // For views that only display reminders: an unreadable file shows as none instead of crashing them.
        public static List<Reminder> TryLoadReminders()
        {
            try
            {
                return LoadReminders();
            }
            catch (Exception)
            {
                return new List<Reminder>();
            }
        }

        // Written via a temporary file, so a crash or full disk mid-save can't truncate every reminder at once.
        // Throws IOException if it couldn't be written (the file is then unchanged); callers that schedule a task
        // save first, so a failed save never leaves a task behind for a reminder that isn't stored.
        public static void SaveReminders(List<Reminder> reminders)
        {
            string json = JsonSerializer.Serialize(reminders);
            if (!AtomicFile.TryWriteAllText(RemindersFile, json))
                throw new IOException("reminders.json couldn't be written.");
        }

        public static void AddReminder(Reminder reminder)
        {
            List<Reminder> reminders = LoadReminders();
            reminders.Add(reminder);
            SaveReminders(reminders);

            RegisterReminderTask(reminder);
        }

        // Replaces the stored reminder with the same Id and reschedules its task (registering under the same
        // name overwrites the old trigger).
        public static void UpdateReminder(Reminder reminder)
        {
            List<Reminder> reminders = LoadReminders();
            int index = reminders.FindIndex(r => r.Id == reminder.Id);
            if (index < 0)
                reminders.Add(reminder);
            else
                reminders[index] = reminder;
            SaveReminders(reminders);

            RegisterReminderTask(reminder);
        }

        public static void DeleteReminder(Guid id)
        {
            List<Reminder> reminders = LoadReminders();
            reminders.RemoveAll(r => r.Id == id);
            SaveReminders(reminders);

            RemoveReminderTask(id);
        }

        // Called by the app when it is launched as "TidyMind.exe --remind {id}"
        // by the Windows Task Scheduler task for this reminder.
        // The task is one-shot, so this is the only place a repeating reminder's next run gets scheduled: nothing
        // here may fail before that happens. reminders.json can be briefly locked by the open app, so reads are
        // retried; if it still can't be read, the task is put back a few minutes later instead of being dropped.
        public static void FireReminder(Guid id)
        {
            List<Reminder> reminders;
            try
            {
                reminders = WithRetries(LoadReminders);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is JsonException)
            {
                RetryLater(id);
                return;
            }

            Reminder reminder = reminders.Find(r => r.Id == id);
            if (reminder == null)
            {
                TrySchedule(() => RemoveReminderTask(id));
                return;
            }

            // A trigger left over from before the reminder was moved to a later time: just put the task back on time.
            if (reminder.NextFireTime > DateTime.Now.AddMinutes(1))
            {
                TrySchedule(() => RegisterReminderTask(reminder));
                return;
            }

            ShowToast(reminder.Title, reminder.Message);

            bool once = reminder.RepeatType == RepeatType.Once;
            if (once)
                reminders.Remove(reminder);
            else
                reminder.NextFireTime = NextFutureFireTime(reminder.NextFireTime, reminder.RepeatType, DateTime.Now);

            // Scheduled even if the save fails: a repeating reminder then still fires next time (and is stepped on
            // from its old stored time); a finished one-off is left in the list rather than shown twice.
            try
            {
                SaveReminders(reminders);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
            }
            TrySchedule(once ? () => RemoveReminderTask(id) : () => RegisterReminderTask(reminder));

            // Keep the process (and the tray icon) alive long enough for the
            // balloon/toast to actually render before we exit.
            Thread.Sleep(6000);

            if (trayIcon != null)
            {
                trayIcon.Visible = false;
                trayIcon.Dispose();
                trayIcon = null;
            }
        }

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
            catch (Exception e) when (e is COMException || e is UnauthorizedAccessException)
            {
            }
        }

        // The first repeat after `now`: after the PC was off or asleep through several, the reminder fires once and
        // the missed ones are skipped. Stepped from the previous time, not from now, so the time of day is kept and
        // month ends step the way CalendarService.OccursOn expects (Jan 31 -> Feb 28 -> Mar 28).
        private static DateTime NextFutureFireTime(DateTime previous, RepeatType repeat, DateTime now)
        {
            if (repeat == RepeatType.Once)
                return previous;

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

        private static void ShowToast(string title, string message)
        {
            if (trayIcon == null)
            {
                trayIcon = new NotifyIcon();
                trayIcon.Icon = System.Drawing.SystemIcons.Information;
                trayIcon.Visible = true;
            }

            trayIcon.BalloonTipIcon = ToolTipIcon.Info;
            trayIcon.BalloonTipTitle = string.IsNullOrWhiteSpace(title) ? "Reminder" : "Reminder: " + title;
            trayIcon.BalloonTipText = message;
            trayIcon.ShowBalloonTip(5000);
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

        private static void RegisterTask(Guid id, DateTime fireTime, string description)
        {
            string exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
                return;

            using (TaskService taskService = new TaskService())
            {
                TaskDefinition taskDefinition = taskService.NewTask();
                taskDefinition.RegistrationInfo.Description = description;

                taskDefinition.Triggers.Clear();
                taskDefinition.Triggers.Add(new TimeTrigger(fireTime));

                taskDefinition.Actions.Clear();
                taskDefinition.Actions.Add(new ExecAction(
                    exePath,
                    "--remind \"" + id + "\"",
                    Path.GetDirectoryName(exePath)));

                taskDefinition.Settings.DisallowStartIfOnBatteries = false;
                taskDefinition.Settings.StopIfGoingOnBatteries = false;
                taskDefinition.Settings.StartWhenAvailable = true;
                taskDefinition.Settings.ExecutionTimeLimit = TimeSpan.Zero;

                taskService.RootFolder.RegisterTaskDefinition(GetTaskName(id), taskDefinition);
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
