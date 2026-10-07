using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TidyMind
{
    // A day the user customised in the calendar. Stored sparsely: untouched days have no entry.
    // Its entry is a plain note: a title and what to do. No time, no repeat, no notification; reminders are separate.
    public class CalendarDay
    {
        public DateTime Date { get; set; }
        public string ColorHex { get; set; }
        public string Title { get; set; }
        public string Note { get; set; }

        // Fields this version doesn't know (written by another TidyMind version): kept so saving doesn't drop them.
        [JsonExtensionData]
        public Dictionary<string, JsonElement> ExtensionData { get; set; }

        // Methods, not properties, so they aren't written to calendar.json.
        public bool HasEntry() => !string.IsNullOrWhiteSpace(Title) || !string.IsNullOrWhiteSpace(Note);

        // Notes from before Title existed are one block of text: their first line stands in as the title.
        public string Heading()
        {
            if (!string.IsNullOrWhiteSpace(Title))
                return Title.Trim();

            string note = (Note ?? "").Trim();
            int lineEnd = note.IndexOf('\n');
            return (lineEnd < 0 ? note : note.Substring(0, lineEnd)).Trim();
        }

        // The text under the heading exactly as written (blank lines and indentation kept), "" if there's none.
        // The day dialog edits this text, so it must round-trip unchanged: only the outer whitespace is trimmed.
        public string Body()
        {
            string note = (Note ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(Title))
                return note;

            int lineEnd = note.IndexOf('\n');
            return lineEnd < 0 ? "" : note.Substring(lineEnd + 1).Trim();
        }
    }
}
