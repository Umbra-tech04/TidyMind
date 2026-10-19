using System;
using System.Collections.Generic;
using System.Linq;

namespace TidyMind
{
    // One thing in a vehicle that matches its window's search box: where it is (the tab, and the section, record or
    // log, a part with the parts above it), the line itself, and the record to show for it.
    public sealed record VehicleMatch(string Tab, string Where, string Text, Guid? Record);

    // The window's search over everything the vehicle holds ("M10", "oil filter", "45 Nm"): every field as shown, each
    // record's title, fields, file and specs, every log row and the notes; accents and case ignored (TextSearch).
    public static class VehicleSearch
    {
        public const string OverviewTab = "Overview";
        private const string Arrow = " › ";

        public static List<VehicleMatch> Find(Entity vehicle, string query)
        {
            string folded = TextSearch.FoldQuery(query.Trim());
            if (folded.Length == 0)
                return new List<VehicleMatch>();
            return Lines(vehicle).Where(m => new SearchableText(m.Text).IndexOf(folded, out _) >= 0).ToList();
        }

        private static IEnumerable<VehicleMatch> Lines(Entity vehicle)
        {
            if (!string.IsNullOrWhiteSpace(vehicle.Notes))
                yield return new VehicleMatch(OverviewTab, Loc.T("Entity.Tab.Overview") + Arrow + VehicleSchema.Notes.Label, vehicle.Notes, null);

            foreach (TabDefinition tab in EntityTypes.Tabs(vehicle.EntityType))
            {
                foreach (SectionDefinition section in tab.Sections)
                    foreach (FieldDefinition field in section.Fields)
                    {
                        string shown = field.Show(field.Read(vehicle), vehicle);
                        if (shown.Length > 0)
                            yield return new VehicleMatch(tab.Key, tab.Label + Arrow + section.Label, field.Label + ": " + shown, null);
                    }

                foreach (RecordDefinition records in tab.Records)
                    foreach ((EntityRecord record, IReadOnlyList<EntityRecord> above) in RecordTree.Walk(records.Read(vehicle)))
                    {
                        string where = string.Join(Arrow, above.Select(records.Title).Prepend(tab.Label));
                        yield return new VehicleMatch(tab.Key, where, records.Title(record), record.Id);
                        where += Arrow + records.Title(record);
                        foreach (string cell in records.Cells(record, vehicle))
                            yield return new VehicleMatch(tab.Key, where, cell, record.Id);
                    }

                foreach (LogDefinition log in tab.Logs)
                    foreach (Dictionary<string, string> row in EntityTypes.Rows(vehicle, log))
                        yield return new VehicleMatch(tab.Key, tab.Label + Arrow + log.Label, EntityTypes.FormatRow(log, row), null);
            }
        }
    }
}
