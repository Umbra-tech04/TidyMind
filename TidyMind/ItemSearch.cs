using System;
using System.Collections.Generic;
using System.Linq;

namespace TidyMind
{
    // One thing in an item that matches its window's search box: where it is (the tab, and the section, record or
    // log, a part with the parts above it), the line itself, and the record to show for it.
    public sealed record ItemMatch(string Tab, string Where, string Text, Guid? Record);

    // The window's search over everything the item holds ("M10", "oil filter", "45 Nm"): every field as shown, each
    // record's title, fields, file and specs, every log row and the notes; accents and case ignored (TextSearch).
    public static class ItemSearch
    {
        public const string OverviewTab = "Overview";
        private const string Arrow = " › ";

        public static List<ItemMatch> Find(Entity item, string query)
        {
            string folded = TextSearch.FoldQuery(query.Trim());
            if (folded.Length == 0)
                return new List<ItemMatch>();
            return Lines(item).Where(m => new SearchableText(m.Text).IndexOf(folded, out _) >= 0).ToList();
        }

        private static IEnumerable<ItemMatch> Lines(Entity item)
        {
            if (!string.IsNullOrWhiteSpace(item.Notes))
                yield return new ItemMatch(OverviewTab, Loc.T("Entity.Tab.Overview") + Arrow + EntityTypes.Notes.Label, item.Notes, null);

            foreach (TabDefinition tab in EntityTypes.Tabs(item.EntityType))
            {
                foreach (SectionDefinition section in tab.Sections)
                    foreach (FieldDefinition field in section.Fields)
                    {
                        string shown = field.Show(field.Read(item), item);
                        if (shown.Length > 0)
                            yield return new ItemMatch(tab.Key, tab.Label + Arrow + section.Label, field.Label + ": " + shown, null);
                    }

                foreach (RecordDefinition records in tab.Records)
                    foreach ((EntityRecord record, IReadOnlyList<EntityRecord> above) in RecordTree.Walk(records.Read(item)))
                    {
                        string where = string.Join(Arrow, above.Select(records.Title).Prepend(tab.Label));
                        yield return new ItemMatch(tab.Key, where, records.Title(record), record.Id);
                        where += Arrow + records.Title(record);
                        foreach (string cell in records.Cells(record, item))
                            yield return new ItemMatch(tab.Key, where, cell, record.Id);
                    }

                foreach (LogDefinition log in tab.Logs)
                    foreach (Dictionary<string, string> row in EntityTypes.Rows(item, log))
                        yield return new ItemMatch(tab.Key, tab.Label + Arrow + log.Label, EntityTypes.FormatRow(log, row), null);
            }
        }
    }
}
