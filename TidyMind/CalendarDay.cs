using System;

namespace TidyMind
{
    // A day the user customised in the calendar. Stored sparsely: untouched days have no entry.
    public class CalendarDay
    {
        public DateTime Date { get; set; }
        public string ColorHex { get; set; }
        public string Note { get; set; }
    }
}
