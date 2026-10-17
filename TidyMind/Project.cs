using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TidyMind
{
    public class Project
    {
        // How views, search and the dashboard point at this project. Projects from before Ids existed get one the
        // first time their file is read (ProjectStore.Load).
        public Guid Id { get; set; }

        public string Name { get; set; }
        public string Description { get; set; }
        public List<TaskItem> Tasks { get; set; }

        // The single note from before pages existed. No longer written, so older versions (and a rollback) still
        // find it as it was; it only stands in while NotePages is null.
        public string Notes { get; set; }

        // Null until the notes are first edited in this version. Left out of the file while null, so saving an
        // untouched project writes it exactly as it was.
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<NotePage>? NotePages { get; set; }

        // What Project Details shows: the pages, or the old note as page 1 (built fresh, not stored). Never empty.
        public List<NotePage> PagesOrLegacyNote()
        {
            List<NotePage> pages = NotePages?.Where(p => p != null).ToList() ?? new List<NotePage>();
            if (NotePages == null)
                pages.Add(new NotePage { Body = Notes });
            else if (pages.Count == 0)
                pages.Add(new NotePage());
            return pages;
        }

        // Files attached in Project Details (empty for projects from before attachments existed).
        public List<Attachment> Attachments { get; set; } = new List<Attachment>();
        public Guid TabId { get; set; }
        public int Order { get; set; }

        // Set whenever the project or its tasks change (DateTime.MinValue for projects from before this existed).
        public DateTime LastModified { get; set; }

        // The card's own background (Customize Card). Only the field matching the type is set; the other is null.
        public BackgroundFill CardBackgroundType { get; set; }
        public string CardBackgroundColor { get; set; }     // "#RRGGBB", when the type is Color
        public string CardBackgroundImagePath { get; set; } // a file name inside CardImages/, when the type is Image

        public BackgroundSetting GetCardBackground() => new BackgroundSetting(CardBackgroundType, CardBackgroundColor, CardBackgroundImagePath);

        public void SetCardBackground(BackgroundSetting background)
        {
            CardBackgroundType = background.Type;
            CardBackgroundColor = background.Color;
            CardBackgroundImagePath = background.Image;
        }

        // 0-100 from tasks done; never 100 until every task is done (e.g. 199/200 would otherwise round up).
        public int CompletionPercent()
        {
            int total = Tasks?.Count ?? 0;
            if (total == 0) return 0;

            int done = Tasks.Count(t => t.IsDone);
            return done == total
                ? 100
                : Math.Min(99, (int)Math.Round(done * 100.0 / total, MidpointRounding.AwayFromZero));
        }

        // Fields this version doesn't know (e.g. the removed Status, or ones from a newer TidyMind): kept so saving
        // doesn't drop them.
        [JsonExtensionData]
        public Dictionary<string, JsonElement> ExtensionData { get; set; }
    }
}
