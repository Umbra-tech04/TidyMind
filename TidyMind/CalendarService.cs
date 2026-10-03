using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace TidyMind
{
    public static class CalendarService
    {
        private const string CalendarFile = "calendar.json";

        // For display: an unreadable file just shows as an empty calendar.
        public static List<CalendarDay> LoadDays()
        {
            return TryRead(out List<CalendarDay> days) ? days : new List<CalendarDay>();
        }

        public static CalendarDay GetDay(List<CalendarDay> days, DateTime date)
        {
            return days.FirstOrDefault(d => d.Date.Date == date.Date);
        }

        // Sparse: a day left with neither a colour nor a note is removed instead of stored empty.
        // False if nothing was saved: a file that can't be read is left alone rather than replaced by this one day.
        public static bool SaveDay(DateTime date, string colorHex, string note)
        {
            if (!TryRead(out List<CalendarDay> days))
                return false;
            days.RemoveAll(d => d.Date.Date == date.Date);

            colorHex = string.IsNullOrWhiteSpace(colorHex) ? null : colorHex;
            note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
            if (colorHex != null || note != null)
                days.Add(new CalendarDay { Date = date.Date, ColorHex = colorHex, Note = note });

            return AtomicFile.TryWriteAllText(CalendarFile, JsonSerializer.Serialize(days.OrderBy(d => d.Date).ToList()));
        }

        private static bool TryRead(out List<CalendarDay> days)
        {
            days = new List<CalendarDay>();
            if (!File.Exists(CalendarFile))
                return true;

            try
            {
                days = JsonSerializer.Deserialize<List<CalendarDay>>(File.ReadAllText(CalendarFile)) ?? new List<CalendarDay>();
                return true;
            }
            catch (Exception e) when (e is JsonException || e is IOException || e is UnauthorizedAccessException)
            {
                return false;
            }
        }

        // Reminders firing on the given day, including future repeats of recurring ones, earliest first.
        public static List<Reminder> RemindersOn(IEnumerable<Reminder> reminders, DateTime date)
        {
            return reminders
                .Where(r => OccursOn(r, date.Date))
                .OrderBy(r => r.NextFireTime.TimeOfDay)
                .ToList();
        }

        // Mirrors ReminderManager's repeat rule (each fire time = previous + one period), starting at NextFireTime.
        private static bool OccursOn(Reminder reminder, DateTime date)
        {
            DateTime first = reminder.NextFireTime;
            if (date < first.Date)
                return false;

            switch (reminder.RepeatType)
            {
                case RepeatType.Daily:
                    return true;
                case RepeatType.Weekly:
                    return (date - first.Date).Days % 7 == 0;
                case RepeatType.Monthly:
                case RepeatType.Yearly:
                    // Stepped like AddMonths/AddYears chains, so e.g. Jan 31 -> Feb 28 -> Mar 28 matches the scheduler.
                    DateTime next = first;
                    while (next.Date < date)
                        next = reminder.RepeatType == RepeatType.Monthly ? next.AddMonths(1) : next.AddYears(1);
                    return next.Date == date;
                default:
                    return first.Date == date;
            }
        }
    }
}
