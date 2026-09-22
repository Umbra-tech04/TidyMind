using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32.TaskScheduler;

namespace TidyMind
{
    public static class ReminderManager
    {
        private const string RemindersFile = "reminders.json";
        private const string TaskNamePrefix = "TidyMind_Reminder_";

        private static NotifyIcon trayIcon;

        public static List<Reminder> LoadReminders()
        {
            if (!File.Exists(RemindersFile))
                return new List<Reminder>();

            string json = File.ReadAllText(RemindersFile);
            return JsonSerializer.Deserialize<List<Reminder>>(json);
        }

        public static void SaveReminders(List<Reminder> reminders)
        {
            string json = JsonSerializer.Serialize(reminders);
            File.WriteAllText(RemindersFile, json);
        }

        public static void AddReminder(Reminder reminder)
        {
            List<Reminder> reminders = LoadReminders();
            reminders.Add(reminder);
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
        public static void FireReminder(Guid id)
        {
            List<Reminder> reminders = LoadReminders();
            Reminder reminder = reminders.Find(r => r.Id == id);

            if (reminder == null)
            {
                RemoveReminderTask(id);
                return;
            }

            ShowToast(reminder.Title, reminder.Message);

            if (reminder.RepeatType == RepeatType.Once)
            {
                reminders.Remove(reminder);
                SaveReminders(reminders);
                RemoveReminderTask(id);
            }
            else
            {
                reminder.NextFireTime = GetNextFireTime(reminder.NextFireTime, reminder.RepeatType);
                SaveReminders(reminders);
                RegisterReminderTask(reminder);
            }

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
            string exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
                return;

            using (TaskService taskService = new TaskService())
            {
                TaskDefinition taskDefinition = taskService.NewTask();
                taskDefinition.RegistrationInfo.Description = "TidyMind reminder: " + reminder.Title;

                taskDefinition.Triggers.Clear();
                taskDefinition.Triggers.Add(new TimeTrigger(reminder.NextFireTime));

                taskDefinition.Actions.Clear();
                taskDefinition.Actions.Add(new ExecAction(
                    exePath,
                    "--remind \"" + reminder.Id + "\"",
                    Path.GetDirectoryName(exePath)));

                taskDefinition.Settings.DisallowStartIfOnBatteries = false;
                taskDefinition.Settings.StopIfGoingOnBatteries = false;
                taskDefinition.Settings.StartWhenAvailable = true;
                taskDefinition.Settings.ExecutionTimeLimit = TimeSpan.Zero;

                taskService.RootFolder.RegisterTaskDefinition(GetTaskName(reminder.Id), taskDefinition);
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
