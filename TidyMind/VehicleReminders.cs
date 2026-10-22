using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace TidyMind
{
    // The reminder of a date a vehicle's record comes due on (a document's "valid until", a service's next one:
    // EntityTypes.Expiries) with its "remind me" days is an ordinary one-off reminder (ReminderManager: the same file,
    // the same Task Scheduler task, listed in Reminders like any other) with the record's Id, at 09:00 that many days
    // before the date. It follows the record: set when the date or the days change, gone when they are cleared, the
    // record or the vehicle is deleted, the date has passed, or (a service's) a newer entry of its kind is written down.
    // Once it has fired it is gone like every one-off, and saving the vehicle again doesn't bring it back. A custom
    // collection's Documents blocks (CustomType) come due the same way, so their reminders work alike.
    //
    // Its stored title is the vehicle's name (the user's own) and its message empty: what it says ("Insurance runs out
    // on …") is made in the current language whenever it is shown (ShownMessage). A message the user types in
    // Reminders is kept and shown instead.
    public static class VehicleReminders
    {
        public const int Hour = 9;

        // When the reminder fires; null for none (no date or no days, or the date has passed). A day already behind (the
        // days were raised, or the date is close): the next 09:00, if that is still on or before the date.
        public static DateTime? FireTime(DateTime? date, int? days, DateTime now)
        {
            if (date == null || days == null || date < now.Date)
                return null;
            DateTime at = date.Value.AddDays(-days.Value).AddHours(Hour);
            if (at > now)
                return at;
            DateTime next = now.Date.AddHours(Hour) > now ? now.Date.AddHours(Hour) : now.Date.AddDays(1).AddHours(Hour);
            return next.Date <= date ? next : null;
        }

        // After the vehicle was saved: `before` as it was stored (null: just added), `after` as it is now (null:
        // deleted). Dates whose day or "remind me" days changed get their reminder set or removed, dates that are gone
        // lose theirs, and a new vehicle name goes into the title of the rest. Without such a change reminders.json isn't
        // even read. Returns what the user should be told (it couldn't be done), or null.
        public static string? Follow(Entity? before, Entity? after)
        {
            Dictionary<Guid, Deadline> was = Dated(before), now = Dated(after);
            List<Deadline> changed = now.Values.Where(d => !was.TryGetValue(d.Record.Id, out Deadline? old) || Timing(old) != Timing(d)).ToList();
            List<Guid> gone = was.Keys.Where(id => !now.ContainsKey(id)).ToList();
            bool renamed = before != null && after != null && before.Name != after.Name;
            if (changed.Count == 0 && gone.Count == 0 && !renamed)
                return null;

            try
            {
                Dictionary<Guid, Reminder> existing = ReminderManager.LoadReminders().GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First());
                foreach (Deadline deadline in changed)
                {
                    existing.TryGetValue(deadline.Record.Id, out Reminder? reminder);
                    if (FireTime(deadline.Date, deadline.RemindDays, DateTime.Now) is DateTime at)
                        ReminderManager.UpdateReminder(new Reminder
                        {
                            Id = deadline.Record.Id,
                            Title = after!.Name,
                            Message = reminder?.Message ?? "",
                            NextFireTime = at,
                            RepeatType = RepeatType.Once
                        });
                    else if (reminder != null)
                        ReminderManager.DeleteReminder(deadline.Record.Id);
                }
                foreach (Guid id in gone.Where(existing.ContainsKey))
                    ReminderManager.DeleteReminder(id);
                if (renamed)
                    foreach (Reminder reminder in existing.Values.Where(r => now.ContainsKey(r.Id) && changed.All(d => d.Record.Id != r.Id)))
                    {
                        reminder.Title = after!.Name;
                        ReminderManager.UpdateReminder(reminder);
                    }
                return null;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is JsonException || e is COMException)
            {
                return Loc.T("Entity.Reminder.Failed");
            }
        }

        // The reminders of records that went without their item being saved through Follow (a custom collection's block
        // removed with its data): by the records' Ids. Returns what the user should be told, or null.
        public static string? Remove(IEnumerable<Guid> recordIds)
        {
            HashSet<Guid> ids = recordIds.ToHashSet();
            if (ids.Count == 0)
                return null;
            try
            {
                foreach (Reminder reminder in ReminderManager.LoadReminders().Where(r => ids.Contains(r.Id)).ToList())
                    ReminderManager.DeleteReminder(reminder.Id);
                return null;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is JsonException || e is COMException)
            {
                return Loc.T("Entity.Reminder.Failed");
            }
        }

        private static (DateTime, int?) Timing(Deadline deadline) => (deadline.Date, deadline.RemindDays);

        private static Dictionary<Guid, Deadline> Dated(Entity? vehicle) =>
            vehicle?.EntityType != EntityTypes.Vehicle && !CustomTypes.IsCustom(vehicle?.EntityType) ? new Dictionary<Guid, Deadline>()
                : EntityTypes.Expiries(vehicle).GroupBy(d => d.Record.Id).ToDictionary(g => g.Key, g => g.First());

        // ---- What a reminder says -----------------------------------------------------------

        // Every vehicle's dates that come due, by their record's Id, for ShownMessage (read only; a collection that can't
        // be read has none). Null if the list of memories can't be read.
        public static Dictionary<Guid, Deadline>? DeadlineIndex()
        {
            if (!ProfileManager.TryLoadProfiles(out List<Profile> profiles, out _))
                return null;
            return profiles.Where(p => p.Type == ProfileType.Collection)
                .SelectMany(EntityStore.LoadOrEmpty)
                .Where(e => e.EntityType == EntityTypes.Vehicle || CustomTypes.IsCustom(e.EntityType))
                .SelectMany(EntityTypes.Expiries)
                .GroupBy(d => d.Record.Id).ToDictionary(g => g.Key, g => g.First());
        }

        // A reminder's message as shown: its own, or for a date's reminder without one, what comes due when, in the
        // current language.
        public static string ShownMessage(Reminder reminder, IReadOnlyDictionary<Guid, Deadline>? deadlines)
        {
            if (!string.IsNullOrWhiteSpace(reminder.Message) || deadlines == null || !deadlines.TryGetValue(reminder.Id, out Deadline? deadline))
                return reminder.Message ?? "";
            return deadline.Kind.ReminderText(deadline.Record, deadline.Date);
        }
    }
}
