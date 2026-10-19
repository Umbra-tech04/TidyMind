using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace TidyMind
{
    // A vehicle document's reminder (its "remind me" days, VehicleDocuments) is an ordinary one-off reminder
    // (ReminderManager: the same file, the same Task Scheduler task, listed in Reminders like any other) with the
    // document's Id, at 09:00 that many days before the document's date. It follows the document: set when the date or
    // the days change, gone when they are cleared, the document or the vehicle is deleted, or the date has passed. Once
    // it has fired it is gone like every one-off, and saving the vehicle again doesn't bring it back.
    //
    // Its stored title is the vehicle's name (the user's own) and its message empty: what it says ("Insurance runs out
    // on …") is made in the current language whenever it is shown (ShownMessage). A message the user types in
    // Reminders is kept and shown instead.
    public static class VehicleReminders
    {
        public const int Hour = 9;

        // When the document's reminder fires; null for none (no date or no days, or the date has passed). A day already
        // behind (the days were raised, or the date is close): the next 09:00, if that is still on or before the date.
        public static DateTime? FireTime(EntityRecord document, DateTime now)
        {
            if (VehicleDocuments.ValidUntil(document) is not DateTime date || VehicleDocuments.RemindDays(document) is not int days
                || date < now.Date)
                return null;
            DateTime at = date.AddDays(-days).AddHours(Hour);
            if (at > now)
                return at;
            DateTime next = now.Date.AddHours(Hour) > now ? now.Date.AddHours(Hour) : now.Date.AddDays(1).AddHours(Hour);
            return next.Date <= date ? next : null;
        }

        // After the vehicle was saved: `before` as it was stored (null: just added), `after` as it is now (null:
        // deleted). Documents whose date or days changed get their reminder set or removed, documents that are gone
        // lose theirs, and a new vehicle name goes into the title of the rest. Without such a change reminders.json
        // isn't even read. Returns what the user should be told (it couldn't be done), or null.
        public static string? Follow(Entity? before, Entity? after)
        {
            Dictionary<Guid, EntityRecord> was = Documents(before), now = Documents(after);
            List<EntityRecord> changed = now.Values.Where(d => !was.TryGetValue(d.Id, out EntityRecord? old) || Timing(old) != Timing(d)).ToList();
            List<Guid> gone = was.Keys.Where(id => !now.ContainsKey(id)).ToList();
            bool renamed = before != null && after != null && before.Name != after.Name;
            if (changed.Count == 0 && gone.Count == 0 && !renamed)
                return null;

            try
            {
                Dictionary<Guid, Reminder> existing = ReminderManager.LoadReminders().GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First());
                foreach (EntityRecord document in changed)
                {
                    existing.TryGetValue(document.Id, out Reminder? reminder);
                    if (FireTime(document, DateTime.Now) is DateTime at)
                        ReminderManager.UpdateReminder(new Reminder
                        {
                            Id = document.Id,
                            Title = after!.Name,
                            Message = reminder?.Message ?? "",
                            NextFireTime = at,
                            RepeatType = RepeatType.Once
                        });
                    else if (reminder != null)
                        ReminderManager.DeleteReminder(document.Id);
                }
                foreach (Guid id in gone.Where(existing.ContainsKey))
                    ReminderManager.DeleteReminder(id);
                if (renamed)
                    foreach (Reminder reminder in existing.Values.Where(r => now.ContainsKey(r.Id) && changed.All(d => d.Id != r.Id)))
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

        private static (DateTime?, int?) Timing(EntityRecord document) => (VehicleDocuments.ValidUntil(document), VehicleDocuments.RemindDays(document));

        private static Dictionary<Guid, EntityRecord> Documents(Entity? vehicle) =>
            vehicle?.EntityType != EntityTypes.Vehicle ? new Dictionary<Guid, EntityRecord>()
                : VehicleDocuments.Of(vehicle).GroupBy(d => d.Id).ToDictionary(g => g.Key, g => g.First());

        // ---- What a reminder says -----------------------------------------------------------

        // Every vehicle document by Id, for ShownMessage (read only; a collection that can't be read has none). Null if
        // the list of memories can't be read.
        public static Dictionary<Guid, EntityRecord>? DocumentIndex()
        {
            if (!ProfileManager.TryLoadProfiles(out List<Profile> profiles, out _))
                return null;
            return profiles.Where(p => p.Type == ProfileType.Collection)
                .SelectMany(EntityStore.LoadOrEmpty)
                .Where(e => e.EntityType == EntityTypes.Vehicle)
                .SelectMany(VehicleDocuments.Of)
                .GroupBy(d => d.Id).ToDictionary(g => g.Key, g => g.First());
        }

        // A reminder's message as shown: its own, or for a document's reminder without one, which document runs out
        // when, in the current language.
        public static string ShownMessage(Reminder reminder, IReadOnlyDictionary<Guid, EntityRecord>? documents)
        {
            if (!string.IsNullOrWhiteSpace(reminder.Message) || documents == null
                || !documents.TryGetValue(reminder.Id, out EntityRecord? document) || VehicleDocuments.ValidUntil(document) is not DateTime date)
                return reminder.Message ?? "";
            return Loc.F("Entity.Reminder.Message", VehicleDocuments.Title(document), date.ToString(Loc.T("Format.LongDate"), Loc.Culture));
        }
    }
}
