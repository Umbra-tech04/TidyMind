using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace TidyMind
{
    // A vehicle's service log (Entity.Records["ServiceLog"], VehicleSchema.ServiceLog): services, repairs, modifications,
    // taxes and fees and notes, each with its date, odometer, cost, the parts it fitted and when the next one is due.
    //
    // Before it, a vehicle had a plain Service log (Entity.Logs["Service"]: Date, Work, Km, Cost, Where). Nothing is
    // migrated: such rows show as entries of the type Service, and only storing the list (an edit in the window) writes
    // it. Storing also writes every entry back as such a row, so an older TidyMind still shows and keeps them; a row an
    // older version added or changed shows as an entry of its own (an edited one beside the entry it came from, as
    // nothing can tell them apart), so nothing typed there is lost.
    public static class VehicleService
    {
        public const string Key = "ServiceLog";
        public const string OldLog = "Service";
        public const string TypeKey = "Type";
        public const string DateKey = "Date";
        public const string OdometerKey = "Odometer";
        public const string PartsKey = "LinkedParts";

        public static readonly string[] Types = { "Service", "Repair", "Modification", "Fee", "Note" };

        // An entry's field and the old log's column it goes to.
        private static readonly (string Field, string Column)[] Columns =
            { (DateKey, "Date"), ("Work", "Work"), (OdometerKey, "Km"), ("Cost", "Cost"), ("Where", "Where") };

        public static DateTime? DateOf(EntityRecord entry) => Values.ParseDate(entry.Get(DateKey));

        // The vehicle's entries, in a list of its own: the stored ones, then the old log's rows no entry stands for.
        public static List<EntityRecord> Of(Entity vehicle)
        {
            List<EntityRecord> entries = vehicle.Records != null && vehicle.Records.TryGetValue(Key, out List<EntityRecord>? stored) && stored != null
                ? stored.Where(e => e != null).ToList() : new List<EntityRecord>();
            List<string> written = entries.Select(e => Signature(Row(e))).ToList();
            List<Dictionary<string, string>> rows = vehicle.Logs != null && vehicle.Logs.TryGetValue(OldLog, out var old) && old != null
                ? old.Where(r => r != null).ToList() : new List<Dictionary<string, string>>();
            for (int i = 0; i < rows.Count; i++)
            {
                string signature = Signature(rows[i]);
                if (written.Remove(signature))
                    continue;
                EntityRecord shown = new EntityRecord { Id = OldRowId(vehicle, i, signature) };
                shown.Set(TypeKey, Types[0]);
                foreach ((string field, string column) in Columns)
                    shown.Set(field, rows[i].GetValueOrDefault(column));
                entries.Add(shown);
            }
            return entries;
        }

        // Stores the list (none: no ServiceLog entry) and the old log in step with it.
        public static void Store(Entity vehicle, List<EntityRecord> entries)
        {
            if (entries.Count > 0)
                (vehicle.Records ??= new Dictionary<string, List<EntityRecord>>())[Key] = entries;
            else if (vehicle.Records != null && vehicle.Records.Remove(Key) && vehicle.Records.Count == 0)
                vehicle.Records = null;

            List<Dictionary<string, string>> rows = entries.Select(Row).Where(r => r.Count > 0).ToList();
            if (rows.Count > 0)
                (vehicle.Logs ??= new Dictionary<string, List<Dictionary<string, string>>>())[OldLog] = rows;
            else if (vehicle.Logs != null && vehicle.Logs.Remove(OldLog) && vehicle.Logs.Count == 0)
                vehicle.Logs = null;
        }

        // The entries whose next due date or reading still counts: the newest of each type (by date; one without a date
        // only when its type has no dated one). A newer service makes the next due of the one before it history.
        public static IEnumerable<EntityRecord> Latest(IReadOnlyList<EntityRecord> entries) =>
            entries.GroupBy(e => e.Get(TypeKey) ?? "").Select(type => type.OrderByDescending(e => DateOf(e) ?? DateTime.MinValue).First());

        // A saved entry fits its parts: each linked part gets the entry's date and odometer as when it was installed,
        // unless it was installed later than that already.
        public static void FitParts(Entity vehicle, EntityRecord entry)
        {
            DateTime? date = DateOf(entry);
            List<string> linked = FieldDefinition.Items(entry.Get(PartsKey)).ToList();
            if (date == null || linked.Count == 0)
                return;
            List<EntityRecord> parts = VehicleSchema.Parts.Read(vehicle);
            bool changed = false;
            foreach (EntityRecord part in RecordTree.Walk(parts).Select(x => x.Record).Where(p => linked.Contains(p.Id.ToString())))
            {
                if (Values.ParseDate(part.Get("InstalledDate")) > date)
                    continue;
                part.Set("InstalledDate", Values.FromDate(date));
                part.Set("InstalledOdometer", entry.Get(OdometerKey));
                changed = true;
            }
            if (changed)
                VehicleSchema.Parts.Write(vehicle, parts);
        }

        // "Oct 1, 2026 · Service": its date and type.
        public static string Title(EntityRecord entry) =>
            string.Join(" · ", new[] { DateOf(entry)?.ToString(Loc.T("Format.ShortDate"), Loc.Culture), TypeLabel(entry) }.Where(t => !string.IsNullOrEmpty(t)));

        public static string TypeLabel(EntityRecord entry) => Choices.Label(entry.Get(TypeKey) ?? Types[0]);

        private static Dictionary<string, string> Row(EntityRecord entry) =>
            Columns.Where(c => !string.IsNullOrEmpty(entry.Get(c.Field))).ToDictionary(c => c.Column, c => entry.Get(c.Field)!);

        private static string Signature(Dictionary<string, string> row) =>
            string.Join("\u001f", Columns.Select(c => row.GetValueOrDefault(c.Column) ?? ""));

        // The same Id every time for an old row shown as an entry, as long as the rows stay as they are.
        private static Guid OldRowId(Entity vehicle, int index, string signature) =>
            new Guid(MD5.HashData(vehicle.Id.ToByteArray().Concat(Encoding.UTF8.GetBytes(OldLog + index + signature)).ToArray()));
    }
}
