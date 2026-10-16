using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TidyMind
{
    public enum AgendaKind { Reminder, CalendarNote }

    // One line in Today / Next 7 days: a reminder occurrence or a calendar day's note.
    public class DashboardAgendaItem
    {
        public AgendaKind Kind { get; set; }
        public DateTime Date { get; set; }
        public DateTime? Time { get; set; } // reminders only (this occurrence); a note belongs to the whole day
        public string Text { get; set; }
        public string Detail { get; set; }
        public Reminder Reminder { get; set; } // null for notes
    }

    public class DashboardData
    {
        public List<DashboardAgendaItem> TodayItems { get; set; } = new List<DashboardAgendaItem>();
        public List<DashboardAgendaItem> UpcomingItems { get; set; } = new List<DashboardAgendaItem>();

        // Expiry dates (EntityTypes) across every collection: within ExpiryDays or already past, soonest first.
        public List<DashboardExpiry> Expiries { get; set; } = new List<DashboardExpiry>();
    }

    // One expiry date of one item ("Inspection" of "Skoda Octavia"), and where it is.
    public class DashboardExpiry
    {
        public string ItemName { get; set; } = "";
        public FieldDefinition Field { get; set; } = new FieldDefinition();
        public string Name => Field.ExpiryName; // in the current language, also for an entry cached before a switch
        public string MemoryName { get; set; } = "";
        public DateTime Date { get; set; }
        public SearchResult Target { get; set; } = new SearchResult();
    }

    // Reads every memory's files (like SearchIndexBuilder) fresh on each call and computes the Home dashboard.
    public static class DashboardDataAggregator
    {
        private const int UpcomingDays = 7;

        public static DashboardData Build(IList<Profile> profiles, DateTime now)
        {
            DashboardData data = new DashboardData();

            AddAgenda(data, now.Date);
            AddExpiries(data, profiles, now.Date);

            return data;
        }

        // Reminders (reminders.json) and calendar notes (calendar.json); quick todos have their own section.
        private static void AddAgenda(DashboardData data, DateTime today)
        {
            List<Reminder> reminders = ReminderManager.TryLoadReminders();
            List<CalendarDay> days = CalendarService.LoadDays();

            HashSet<Guid>? blockIds = DayPlanReminders.BlockIds();
            data.TodayItems = ItemsOn(today, reminders, days, blockIds);

            // Every occurrence: Home shows the week day by day, so a daily reminder belongs on each of its days.
            for (int day = 1; day <= UpcomingDays; day++)
                data.UpcomingItems.AddRange(ItemsOn(today.AddDays(day), reminders, days, blockIds));
        }

        // The day's note first (it covers the whole day), then its reminders by time.
        private static List<DashboardAgendaItem> ItemsOn(DateTime date, List<Reminder> reminders, List<CalendarDay> days,
            ISet<Guid>? blockIds)
        {
            List<DashboardAgendaItem> items = new List<DashboardAgendaItem>();

            // The note's title as the line itself, what to do under it (like a reminder's message and labels).
            CalendarDay day = CalendarService.GetDay(days, date);
            if (day != null && day.HasEntry())
            {
                items.Add(new DashboardAgendaItem
                {
                    Kind = AgendaKind.CalendarNote,
                    Date = date,
                    Text = day.Heading(),
                    Detail = day.Body()
                });
            }

            foreach (Reminder reminder in CalendarService.RemindersOn(reminders, date))
            {
                string title = string.IsNullOrWhiteSpace(reminder.Title) ? null : DayPlanReminders.ShownTitle(reminder, blockIds);
                string repeat = reminder.RepeatType == RepeatType.Once ? null : ReminderForm.RepeatLabel(reminder.RepeatType).ToLower(Loc.Culture);
                items.Add(new DashboardAgendaItem
                {
                    Kind = AgendaKind.Reminder,
                    Date = date,
                    Time = date + reminder.NextFireTime.TimeOfDay,
                    Text = reminder.Message,
                    Detail = string.Join("  ·  ", new[] { title, repeat }.Where(s => s != null)),
                    Reminder = reminder
                });
            }

            return items;
        }

        // ---- Expiry dates -------------------------------------------------

        public const int ExpiryDays = 60;

        // Each collection's expiry dates, kept while its file is unchanged: Home refreshes on every activation, and
        // this way only a collection that was saved since is read again.
        private static readonly Dictionary<Guid, (DateTime Written, List<DashboardExpiry> Dates)> expiryCache =
            new Dictionary<Guid, (DateTime, List<DashboardExpiry>)>();

        // Read only: it never schedules anything. A date further than ExpiryDays ahead, or none, isn't listed.
        private static void AddExpiries(DashboardData data, IList<Profile> profiles, DateTime today)
        {
            // Collections deleted since leave the cache.
            foreach (Guid gone in expiryCache.Keys.Where(id => !profiles.Any(p => p.Id == id)).ToList())
                expiryCache.Remove(gone);

            foreach (Profile profile in profiles.Where(p => p.Type == ProfileType.Collection))
            {
                string file = MemoryFiles.Items(profile);
                DateTime written = File.Exists(file) ? File.GetLastWriteTimeUtc(file) : DateTime.MinValue;
                if (!expiryCache.TryGetValue(profile.Id, out var cached) || cached.Written != written)
                {
                    // A missing or unreadable file contributes nothing (the collection view says why).
                    cached = (written, EntityStore.LoadOrEmpty(profile)
                        .SelectMany(entity => EntityTypes.Expiries(entity).Select(expiry => new DashboardExpiry
                        {
                            ItemName = entity.Name ?? "",
                            Field = expiry.Field,
                            Date = expiry.Date,
                            Target = new SearchResult { Kind = SearchResultKind.Entity, Title = entity.Name, Profile = profile, ItemId = entity.Id }
                        }))
                        .ToList());
                    expiryCache[profile.Id] = cached;
                }

                foreach (DashboardExpiry expiry in cached.Dates.Where(x => (x.Date - today).TotalDays <= ExpiryDays))
                {
                    // The memory as it is now (renamed, or the list read again since this was cached).
                    expiry.MemoryName = profile.Name;
                    expiry.Target.Profile = profile;
                    data.Expiries.Add(expiry);
                }
            }

            data.Expiries = data.Expiries.OrderBy(x => x.Date).ThenBy(x => x.ItemName, Loc.Comparer).ToList();
        }
    }
}
