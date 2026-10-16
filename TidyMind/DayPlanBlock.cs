using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TidyMind
{
    // dayplan.json: every planned day's time blocks, app-wide like calendar.json (a day is the same day whichever
    // memory is open). An object rather than a bare list, so other kinds of plan data can sit beside Blocks in the
    // same file.
    public class DayPlanFile
    {
        public List<DayPlanBlock> Blocks { get; set; } = new List<DayPlanBlock>();

        // Fields this version doesn't know (written by another TidyMind version): kept so saving doesn't drop them.
        [JsonExtensionData]
        public Dictionary<string, JsonElement>? ExtensionData { get; set; }
    }

    // One block of a day's plan, e.g. "Math homework 13:00-15:00". Times are wall-clock minutes from midnight
    // (0..1440), never a span from a moment: a block reads the same on the 23- and 25-hour days of a clock change.
    public class DayPlanBlock
    {
        public const int MinLength = 15;
        public const int DayEnd = 24 * 60;

        public Guid Id { get; set; }
        public DateTime Date { get; set; } // date only
        public int StartMinute { get; set; }
        public int EndMinute { get; set; }
        public string Title { get; set; } = "";
        public string? Notes { get; set; }
        public string? ColorHex { get; set; }
        public bool Done { get; set; }

        // The to-do this block is time for. Kept when that to-do is deleted: the block then shows as unlinked.
        public TodoLink? Todo { get; set; }

        // How long before the start its reminder fires, if it has one. The reminder itself is an ordinary one in
        // reminders.json with this block's Id (DayPlanReminders).
        public int? RemindMinutesBefore { get; set; }

        // Fields this version doesn't know (written by another TidyMind version): kept so saving doesn't drop them.
        [JsonExtensionData]
        public Dictionary<string, JsonElement>? ExtensionData { get; set; }

        // Methods, not properties, so they aren't written to dayplan.json.
        public DateTime StartTime() => Date.Date.AddMinutes(StartMinute);

        public string TimeRange() => Clock(StartMinute) + "–" + Clock(EndMinute);

        public bool Overlaps(DayPlanBlock other) =>
            other.Date.Date == Date.Date && other.StartMinute < EndMinute && StartMinute < other.EndMinute;

        // "13:00"; the end of the day is "24:00", not "00:00".
        public static string Clock(int minute) =>
            (minute / 60).ToString("00", CultureInfo.InvariantCulture) + ":" + (minute % 60).ToString("00", CultureInfo.InvariantCulture);

        // Null if the times make a block; otherwise what's wrong, to show as is.
        public static string? CheckTimes(int start, int end)
        {
            if (start < 0 || end > DayEnd)
                return Loc.T("Plan.Problem.WithinDay");
            if (end <= start)
                return Loc.T("Plan.Problem.EndAfterStart");
            if (end - start < MinLength)
                return Loc.Plural("Plan.Problem.MinLength", MinLength);
            return null;
        }
    }

    public enum TodoSource { Home, Memory }

    // Which to-do a block belongs to: one of Home's to-dos, or one in a project memory's to-do list.
    public class TodoLink
    {
        public TodoSource Source { get; set; }
        public Guid Id { get; set; }
        public Guid? MemoryId { get; set; } // Memory only

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? ExtensionData { get; set; }

        public static TodoLink ForHome(Guid id) => new TodoLink { Source = TodoSource.Home, Id = id };

        public static TodoLink ForMemory(Profile memory, Guid id) =>
            new TodoLink { Source = TodoSource.Memory, Id = id, MemoryId = memory.Id };

        public bool Matches(TodoLink? other) =>
            other != null && other.Source == Source && other.Id == Id && other.MemoryId == MemoryId;
    }
}
