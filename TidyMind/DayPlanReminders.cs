using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace TidyMind
{
    // A block's reminder is an ordinary one-off reminder (ReminderManager: the same file, the same Task Scheduler task,
    // listed in Reminders like any other) with the block's Id, firing RemindMinutesBefore the block starts. It exists
    // only while it can still fire: a moved block takes it along, a time that has passed removes it, a deleted block
    // deletes it, and once it has fired it is gone like every one-off reminder.
    public static class DayPlanReminders
    {
        // Stored as the reminder's Title in every language, as it always was. On screen a block's reminder is told
        // apart by its Id (the block's) and called ShownTitle, so no stored text depends on the language.
        public const string ReminderTitle = "Day plan";

        // The choices the block editor offers, in minutes before the start.
        public static readonly int[] Leads = { 0, 5, 10, 15, 30, 60 };

        public static string LeadText(int minutes) =>
            minutes == 0 ? Loc.T("Plan.Lead.AtStart") : minutes == 60 ? Loc.T("Plan.Lead.HourBefore") : Loc.Plural("Plan.Lead.MinutesBefore", minutes);

        // The Ids of every day plan block: a reminder with one of them is a block's. Null if the plan can't be read.
        public static HashSet<Guid>? BlockIds() =>
            DayPlanStore.TryLoad(out DayPlanFile plan) ? plan.Blocks.Select(b => b.Id).ToHashSet() : null;

        // What a reminder's title says on screen: a block's reminder in the current language, any other its own title
        // (a memory, project or item name, which is never translated). Without the plan, the stored title tells.
        public static string ShownTitle(Reminder reminder, ISet<Guid>? blockIds) =>
            (blockIds?.Contains(reminder.Id) ?? reminder.Title == ReminderTitle) ? Loc.T("Plan.Name") : reminder.Title;

        public static DateTime FireTime(DayPlanBlock block) => block.StartTime().AddMinutes(-(block.RemindMinutesBefore ?? 0));

        // Schedules the block's reminder, or moves it to the block's current time and title.
        public static void Set(DayPlanBlock block)
        {
            ReminderManager.UpdateReminder(new Reminder
            {
                Id = block.Id,
                Title = ReminderTitle,
                Message = block.Title + ", " + block.TimeRange(),
                NextFireTime = FireTime(block),
                RepeatType = RepeatType.Once
            });
        }

        // Only a block saved with a reminder can have one (Set is the only way in), so for the others reminders.json
        // isn't even read: a problem with it never gets in the way of their changes.
        public static void Remove(DayPlanBlock block)
        {
            if (block.RemindMinutesBefore != null && Exists(block.Id))
                ReminderManager.DeleteReminder(block.Id);
        }

        // After the block was moved or changed: its reminder (if it has one) follows it, or goes if that time has
        // passed. Returns what the user should be told, or null.
        public static string? Follow(DayPlanBlock block)
        {
            if (block.RemindMinutesBefore == null || !Exists(block.Id))
                return null;
            if (FireTime(block) > DateTime.Now)
            {
                Set(block);
                return null;
            }
            ReminderManager.DeleteReminder(block.Id);
            return Loc.T("Plan.Reminder.Removed");
        }

        private static bool Exists(Guid blockId) => ReminderManager.LoadReminders().Any(r => r.Id == blockId);

        // Runs a reminder change. What the user should know comes back as a sentence: the change's own note, or why
        // it couldn't be made (the same reasons AddReminderWindow gives, `failed` then true).
        public static string? Try(Func<string?> change, out bool failed)
        {
            failed = false;
            try
            {
                return change();
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is JsonException
                                      || e is COMException)
            {
                failed = true;
                return Loc.T("Plan.Reminder.Failed");
            }
        }
    }
}
