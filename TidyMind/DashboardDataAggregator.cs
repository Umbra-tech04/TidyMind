using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

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

    public class DashboardProject
    {
        public string Name { get; set; }
        public string MemoryName { get; set; }
        public int Percent { get; set; }
        public DateTime LastModified { get; set; } // default for projects from before timestamps existed
        public int TasksLeft { get; set; }
        public ProjectCardModel Card { get; set; } // what the project card draws (the same card as the project grid)
        public SearchResult Target { get; set; }
    }

    public class DashboardData
    {
        public List<DashboardAgendaItem> TodayItems { get; set; } = new List<DashboardAgendaItem>();
        public List<DashboardAgendaItem> UpcomingItems { get; set; } = new List<DashboardAgendaItem>();

        public int ProjectCount { get; set; }
        public int ActiveProjectCount { get; set; }
        public int AverageCompletion { get; set; }
        public List<DashboardProject> AlmostDone { get; set; } = new List<DashboardProject>(); // 70-99%, closest first
        public List<DashboardProject> Stalled { get; set; } = new List<DashboardProject>();    // under 30%, untouched 14+ days, longest first
    }

    // Reads every memory's files (like SearchIndexBuilder) fresh on each call and computes the Home dashboard.
    public static class DashboardDataAggregator
    {
        private const int UpcomingDays = 7;
        private const int AlmostDonePercent = 70;
        private const int StalledBelowPercent = 30;
        private const int StalledAfterDays = 14;

        public static DashboardData Build(IList<Profile> profiles, DateTime now)
        {
            DashboardData data = new DashboardData();

            AddAgenda(data, now.Date);
            AddProjects(data, profiles, now.Date);

            return data;
        }

        // Reminders (reminders.json) and calendar notes (calendar.json); quick todos have their own section.
        private static void AddAgenda(DashboardData data, DateTime today)
        {
            List<Reminder> reminders = ReminderManager.TryLoadReminders();
            List<CalendarDay> days = CalendarService.LoadDays();

            data.TodayItems = ItemsOn(today, reminders, days);

            // Every occurrence: Home shows the week day by day, so a daily reminder belongs on each of its days.
            for (int day = 1; day <= UpcomingDays; day++)
                data.UpcomingItems.AddRange(ItemsOn(today.AddDays(day), reminders, days));
        }

        // The day's note first (it covers the whole day), then its reminders by time.
        private static List<DashboardAgendaItem> ItemsOn(DateTime date, List<Reminder> reminders, List<CalendarDay> days)
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
                string title = string.IsNullOrWhiteSpace(reminder.Title) ? null : reminder.Title;
                string repeat = reminder.RepeatType == RepeatType.Once ? null : reminder.RepeatType.ToString().ToLowerInvariant();
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

        private static void AddProjects(DashboardData data, IList<Profile> profiles, DateTime today)
        {
            List<DashboardProject> all = new List<DashboardProject>();

            foreach (Profile profile in profiles.Where(p => p.Type == ProfileType.Project))
            {
                // Same order ProjectView sorts into, so ItemIndex opens the same project there.
                List<Project> projects = Load<Project>(profile.Name + ".json").OrderBy(p => p.Order).ToList();
                for (int i = 0; i < projects.Count; i++)
                {
                    int total = projects[i].Tasks?.Count ?? 0;
                    all.Add(new DashboardProject
                    {
                        Name = projects[i].Name,
                        MemoryName = profile.Name,
                        Percent = projects[i].CompletionPercent(),
                        LastModified = projects[i].LastModified,
                        TasksLeft = total - (projects[i].Tasks?.Count(t => t.IsDone) ?? 0),
                        Card = ProjectCardModel.From(projects[i]),
                        Target = new SearchResult { Kind = SearchResultKind.Project, Title = projects[i].Name, Profile = profile, ItemIndex = i }
                    });
                }
            }

            data.ProjectCount = all.Count;

            // "In progress" means the same thing as in the groups below: not every task done.
            List<DashboardProject> active = all.Where(p => p.Percent < 100).ToList();
            data.ActiveProjectCount = active.Count;
            data.AverageCompletion = active.Count == 0 ? 0 : (int)Math.Round(active.Average(p => p.Percent), MidpointRounding.AwayFromZero);

            data.AlmostDone = active.Where(p => p.Percent >= AlmostDonePercent)
                .OrderByDescending(p => p.Percent)
                .ThenByDescending(p => p.LastModified)
                .ToList();

            // Projects from before timestamps existed have no known age, so they can't be called stalled — guessing
            // would flag every old project the first time this opens. They join in once they're edited and left alone.
            data.Stalled = active.Where(p => p.Percent < StalledBelowPercent
                                             && p.LastModified != default && (today - p.LastModified.Date).Days >= StalledAfterDays)
                .OrderBy(p => p.LastModified)
                .ToList();
        }

        // A missing or unreadable file contributes nothing; the dashboard must never crash the app.
        private static List<T> Load<T>(string fileName)
        {
            if (!File.Exists(fileName))
                return new List<T>();

            try
            {
                return JsonSerializer.Deserialize<List<T>>(File.ReadAllText(fileName)) ?? new List<T>();
            }
            catch (Exception)
            {
                return new List<T>();
            }
        }
    }
}
