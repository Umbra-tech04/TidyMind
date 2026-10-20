using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace TidyMind
{
    // Text; Number (with a unit); Choice (one of fixed values); Date; Flag (yes, or nothing); CheckList (any of fixed
    // values plus the user's own, one per line); LongText (several lines); Links (any of another record list's records, by
    // Id, one per line: a service's parts). A record's own lists: File (EntityRecord.File), Specs (name and value pairs)
    // and Pictures.
    public enum ValueKind { Text, Number, Choice, Date, Flag, CheckList, LongText, File, Specs, Pictures, Links }

    // One field of a type described here: how the item form, the vehicle window, search and export show it. Values go
    // in and out as stored text (Values). Key is the field's stable name; what it's called on screen comes from it.
    public class FieldDefinition
    {
        public string Key { get; init; } = "";
        public string Label => Loc.T("Entity.Field." + Key);
        public ValueKind Kind { get; init; }

        // Choice and CheckList: stored as written here, in English, whatever the language; on screen through
        // ChoiceLabel (by default the "Choice.<value>" text).
        public string[] Choices { get; init; } = Array.Empty<string>();
        public Func<string, string> ChoiceLabel { get; init; } = TidyMind.Choices.Label;

        // A date that runs out (a document's "valid until"): shown with how close it is (Expiry).
        public bool IsExpiry { get; init; }

        // A number's unit (Units): a fixed one, Units.Distance (the vehicle's own, km or mi) or Units.Money. Alternate:
        // a second unit it is also shown and can be typed in (hp beside kW).
        public string? Unit { get; init; }
        public UnitAlternate? Alternate { get; init; }

        // Asked for in the item form too (adding a vehicle), not only in its window.
        public bool InForm { get; init; }

        // Links: the key of the record list it points into (a vehicle's "Parts").
        public string? LinkTo { get; init; }

        // A field Entity has a property of its own for (from before Details): how to read and write it. Null for the
        // others, which are stored in Entity.Details (or a record's Values) under Key.
        public Func<Entity, string?>? Get { get; init; }
        public Action<Entity, string?>? Set { get; init; }

        public string? Read(Entity entity) => Get != null ? Get(entity) : entity.Details?.GetValueOrDefault(Key);

        // Nothing ("" or null) removes the value; Details leaves the file once it holds nothing.
        public void Write(Entity entity, string? value)
        {
            value = string.IsNullOrEmpty(value) ? null : value;
            if (Set != null)
                Set(entity, value);
            else
                WriteDetail(entity, Key, value);
        }

        // Entity.Details[key] set, or removed for nothing (and Details with its last value).
        public static void WriteDetail(Entity entity, string key, string? value)
        {
            if (!string.IsNullOrEmpty(value))
                (entity.Details ??= new Dictionary<string, string>())[key] = value;
            else if (entity.Details != null && entity.Details.Remove(key) && entity.Details.Count == 0)
                entity.Details = null;
        }

        // A stored value as the user reads it, in the current language: a choice by its label, a date in the language's
        // short form, a number with its unit (and the alternate one), a flag as "Yes". Empty for nothing.
        public string Show(string? stored, Entity owner)
        {
            if (string.IsNullOrWhiteSpace(stored))
                return "";
            switch (Kind)
            {
                case ValueKind.Choice:
                    return ChoiceLabel(stored);
                case ValueKind.CheckList:
                    return string.Join(", ", Items(stored).Select(ChoiceLabel));
                case ValueKind.Links:
                    Dictionary<string, string> labels = LinkOptions(owner).GroupBy(o => o.Value).ToDictionary(g => g.Key, g => g.First().Label);
                    return string.Join(", ", Items(stored).Where(labels.ContainsKey).Select(id => labels[id])); // one deleted since: left out
                case ValueKind.Date:
                    return Values.ParseDate(stored) is DateTime date ? date.ToString(Loc.T("Format.ShortDate"), Loc.Culture) : stored;
                case ValueKind.Flag:
                    return Loc.T("Entity.Flag.Yes");
                case ValueKind.Number:
                    if (Values.Number(stored) is not double number)
                        return stored;
                    string shown = Units.Format(number, Unit, owner);
                    return Alternate == null ? shown : shown + " (" + Units.Format(Math.Round(number * Alternate.Factor), Alternate.Unit, owner) + ")";
                default:
                    return stored;
            }
        }

        // Links: the records it can point at, as stored value (the Id) and label (a nested one with the ones above it,
        // "Brakes › Front brakes").
        public List<(string Value, string Label)> LinkOptions(Entity owner)
        {
            RecordDefinition? target = EntityTypes.Records(owner.EntityType).FirstOrDefault(r => r.Key == LinkTo);
            return target == null ? new List<(string, string)>()
                : RecordTree.Walk(target.Read(owner)).Select(x => (x.Record.Id.ToString(), string.Join(" › ", x.Above.Append(x.Record).Select(target.Title)))).ToList();
        }

        // A CheckList's or Links' values: one per line.
        public static IEnumerable<string> Items(string? stored) =>
            (stored ?? "").Split('\n').Select(v => v.Trim()).Where(v => v.Length > 0);
    }

    // A second unit for a number field: worth Factor times the stored one.
    public sealed record UnitAlternate(string Unit, double Factor);

    // The units of number fields, shown as "Entity.Unit.<unit>" in the current language. Distance is a vehicle's own
    // (km or mi, its OdometerUnit field), Money the currency setting.
    public static class Units
    {
        public const string Distance = "Distance";
        public const string Money = "Money";
        public const string Km = "km";
        public const string Mi = "mi";

        public static string DistanceOf(Entity? owner) => owner?.Details?.GetValueOrDefault(VehicleSchema.OdometerUnitKey) == Mi ? Mi : Km;

        public static string Label(string unit, Entity? owner) =>
            unit == Money ? Currency.Symbol : Loc.T("Entity.Unit." + (unit == Distance ? DistanceOf(owner) : unit));

        // "152 300 km", "6,5 l/100 km", "12 500 Ft": a quantity written the current language's way, grouped. A plain
        // number (a year, a count) is not grouped: 2017, not 2,017.
        public static string Format(double value, string? unit, Entity? owner)
        {
            string number = value.ToString(unit == null ? "0.###" : "#,0.###", Loc.Culture);
            string label = unit == null ? "" : Label(unit, owner);
            return label.Length == 0 ? number : number + " " + label;
        }
    }

    // The one currency amounts are shown in (Settings): what the user typed there, else the Windows region's symbol.
    // Only a label: amounts are stored as plain numbers, so changing it converts nothing.
    public static class Currency
    {
        private static string? symbol;

        public static string Symbol => symbol ??= Pick(AppSettings.Load().Currency);

        public static string RegionSymbol
        {
            get
            {
                try
                {
                    return new RegionInfo(Loc.RegionalCulture.Name).CurrencySymbol;
                }
                catch (ArgumentException)
                {
                    return ""; // a culture without a region
                }
            }
        }

        // Settings saved a new one.
        public static void Changed(string? typed) => symbol = Pick(typed);

        private static string Pick(string? typed) => string.IsNullOrWhiteSpace(typed) ? RegionSymbol : typed.Trim();
    }

    // A group of a type's fields under a heading: a card in the vehicle window; search and export go through them in
    // this order.
    public class SectionDefinition
    {
        public string Key { get; init; } = "";
        public string Label => Loc.T("Entity.Section." + Key);
        public FieldDefinition[] Fields { get; init; } = Array.Empty<FieldDefinition>();
    }

    // A list of records an item holds (a vehicle's documents): the fields of each, what a new one starts with, and how
    // one is called in a line (Title, made from the fields TitleKeys names, which a line then doesn't repeat). Nested:
    // records hold records (RecordTree). CurrentKey: the Entity.Details field naming the one record in use now (the
    // mounted wheel set). The window lists a flat one newest first by NewestFirstKey (a date field), with a filter by
    // FilterKey (a choice field) and the sums of TotalKey (a money field) per choice and per year.
    public class RecordDefinition
    {
        public string Key { get; init; } = "";
        public string Label => Loc.T("Entity.Records." + Key);
        public string AddLabel => Loc.T("Entity.Records." + Key + ".Add");
        public FieldDefinition[] Fields { get; init; } = Array.Empty<FieldDefinition>();
        public string[] TitleKeys { get; init; } = Array.Empty<string>();
        public Func<EntityRecord, string> Title { get; init; } = _ => "";
        public Action<EntityRecord, Entity>? Start { get; init; }
        public bool Nested { get; init; }
        public string? CurrentKey { get; init; }
        public string? NewestFirstKey { get; init; }
        public string? FilterKey { get; init; }
        public string? TotalKey { get; init; }

        // Records that come due (a document's valid-until date, a service's next one; EntityTypes.Expiries): the date field
        // (DueKey) and its "remind me" days (RemindKey, VehicleReminders), the odometer reading it comes due at
        // (DueDistanceKey, EntityTypes.DistanceDues), which records' due dates still count (DueFrom: a service's only until a
        // newer one of its kind is written down), what one is called in a short line ("Inspection in 12 days") and what its
        // reminder says.
        public string? DueKey { get; init; }
        public string? RemindKey { get; init; }
        public string? DueDistanceKey { get; init; }
        public Func<IReadOnlyList<EntityRecord>, IEnumerable<EntityRecord>> DueFrom { get; init; } = records => records;
        public Func<EntityRecord, string> DeadlineName { get; init; } = _ => "";
        public Func<EntityRecord, DateTime, string> ReminderText { get; init; } = (_, _) => "";

        // After one of its records was saved, on the item being saved (a service fitting its parts: their installed date).
        public Action<Entity, EntityRecord>? Saved { get; init; }

        // How the item's list is read and stored, when it isn't simply Entity.Records[Key] (documents keep the old
        // insurance and inspection dates in step).
        public Func<Entity, List<EntityRecord>>? Load { get; init; }
        public Action<Entity, List<EntityRecord>>? Store { get; init; }

        // The item's records: a list of its own to change, then give to Write.
        public List<EntityRecord> Read(Entity entity) =>
            Load != null ? Load(entity)
            : entity.Records != null && entity.Records.TryGetValue(Key, out List<EntityRecord>? stored) && stored != null
                ? stored.Where(r => r != null).ToList() : new List<EntityRecord>();

        // An empty list leaves the item without it (and without Records once that's empty).
        public void Write(Entity entity, List<EntityRecord> records)
        {
            if (Store != null)
            {
                Store(entity, records);
                return;
            }
            if (records.Count > 0)
                (entity.Records ??= new Dictionary<string, List<EntityRecord>>())[Key] = records;
            else if (entity.Records != null && entity.Records.Remove(Key) && entity.Records.Count == 0)
                entity.Records = null;
        }

        // A new record of the owner's (not added to it yet).
        public EntityRecord New(Entity owner)
        {
            EntityRecord record = new EntityRecord { Id = Guid.NewGuid() };
            Start?.Invoke(record, owner);
            return record;
        }

        // "Insurance · Allianz | Valid until: Nov 30, 2026 | Reminder: 30 days before": the title, then its cells. A
        // nested record has the ones above it before its title ("Brakes › Front brakes › Brake disc").
        public string Line(EntityRecord record, Entity owner, IEnumerable<EntityRecord>? above = null)
        {
            string title = string.Join(" › ", (above ?? Enumerable.Empty<EntityRecord>()).Select(Title).Append(Title(record)));
            return string.Join(" | ", Cells(record, owner).Prepend(title).Where(c => c.Length > 0));
        }

        // The fields that are filled, other than the title's ("Valid until: Nov 30, 2026"): a file by its name, specs
        // one by one ("Size: M10x1.25x40"), pictures by how many there are.
        public IEnumerable<string> Cells(EntityRecord record, Entity owner)
        {
            foreach (FieldDefinition field in Fields.Where(f => !TitleKeys.Contains(f.Key)))
            {
                switch (field.Kind)
                {
                    case ValueKind.File:
                        if (!string.IsNullOrEmpty(record.File?.DisplayName))
                            yield return field.Label + ": " + record.File.DisplayName;
                        break;
                    case ValueKind.Specs:
                        foreach (SpecLine spec in (record.Specs ?? new List<SpecLine>()).Where(s => s != null))
                            yield return spec.Name + ": " + spec.Value;
                        break;
                    case ValueKind.Pictures:
                        if (record.Pictures?.Count > 0)
                            yield return Loc.Plural("Entity.PictureCount", record.Pictures.Count);
                        break;
                    default:
                        string shown = field.Show(record.Get(field.Key), owner);
                        if (shown.Length > 0)
                            yield return field.Label + ": " + shown;
                        break;
                }
            }
        }

        // The record in use now (CurrentKey), and making one so.
        public bool IsCurrent(Entity owner, EntityRecord record) =>
            CurrentKey != null && owner.Details?.GetValueOrDefault(CurrentKey) == record.Id.ToString();

        public void SetCurrent(Entity owner, Guid? id) => FieldDefinition.WriteDetail(owner, CurrentKey!, id?.ToString());
    }

    // One tab of the vehicle window after its Overview: its sections, record lists and logs, in that order.
    public class TabDefinition
    {
        public string Key { get; init; } = "";
        public string Label => Loc.T("Entity.Tab." + Key);
        public SectionDefinition[] Sections { get; init; } = Array.Empty<SectionDefinition>();
        public RecordDefinition[] Records { get; init; } = Array.Empty<RecordDefinition>();
        public LogDefinition[] Logs { get; init; } = Array.Empty<LogDefinition>();
    }

    public class LogColumn
    {
        public string Key { get; init; } = "";
        public string Label => Loc.T("Entity.Column." + Key);
        public ValueKind Kind { get; init; }
    }

    // A log field: a list of rows with the type's columns (LogEditor). Summary: an optional line computed from the rows
    // (never stored), or null when there's nothing to say.
    public class LogDefinition
    {
        public string Key { get; init; } = "";
        public string Label => Loc.T("Entity.Log." + Key);
        public LogColumn[] Columns { get; init; } = Array.Empty<LogColumn>();
        public Func<IReadOnlyList<Dictionary<string, string>>, string?>? Summary { get; init; }
    }

    // A date one of an item's records comes due on ("Inspection" of a vehicle), and what it is called in a short line.
    public sealed record Deadline(RecordDefinition Kind, EntityRecord Record, DateTime Date)
    {
        public string Name => Kind.DeadlineName(Record); // in the current language, also when kept from before a switch
        public int? RemindDays => Kind.RemindKey == null ? null : Values.ToInt(Record.Get(Kind.RemindKey));
    }

    // An odometer reading one of a vehicle's records comes due at, close (within EntityTypes.DistanceSoon) or passed:
    // Remaining is how far there is still to go, below 0 once passed. Shown like a date that is close or past.
    public sealed record DistanceDue(RecordDefinition Kind, EntityRecord Record, double Remaining)
    {
        public string Name => Kind.DeadlineName(Record);
        public ExpiryLevel Level => Remaining < 0 ? ExpiryLevel.Expired : ExpiryLevel.Soon;

        // "in 800 km", "200 km overdue".
        public string Describe(Entity owner) => Remaining < 0
            ? Loc.F("Expiry.PastDistance", Units.Format(-Remaining, Units.Distance, owner))
            : Loc.F("Expiry.InDistance", Units.Format(Remaining, Units.Distance, owner));
    }

    // The item types described by definitions (Vehicle, VehicleSchema). Clothing keeps its own fields in EntityForm,
    // CollectionView and ExportService and has no definition here: every list is empty for it.
    public static class EntityTypes
    {
        // Stored in each item (Entity.EntityType) as these English names, whatever the language; on screen they are
        // "Entity.Type.<name>".
        public const string Clothing = "Clothing";
        public const string Vehicle = "Vehicle";

        public static IReadOnlyList<TabDefinition> Tabs(string? type) =>
            type == Vehicle ? VehicleSchema.Tabs : Array.Empty<TabDefinition>();

        public static IEnumerable<SectionDefinition> Sections(string? type) => Tabs(type).SelectMany(t => t.Sections);

        public static IReadOnlyList<FieldDefinition> Fields(string? type) => Sections(type).SelectMany(s => s.Fields).ToList();

        public static IEnumerable<RecordDefinition> Records(string? type) => Tabs(type).SelectMany(t => t.Records);

        public static IReadOnlyList<LogDefinition> Logs(string? type) =>
            type == Vehicle ? VehicleSchema.Logs : Array.Empty<LogDefinition>();

        // The item's dates that come due (RecordDefinition.DueKey), soonest first (a passed one first of all).
        public static List<Deadline> Expiries(Entity entity) =>
            Records(entity.EntityType).Where(r => r.DueKey != null)
                .SelectMany(r => r.DueFrom(r.Read(entity)).Select(record => (Kind: r, Record: record, Date: Values.ParseDate(record.Get(r.DueKey!)))))
                .Where(x => x.Date != null).Select(x => new Deadline(x.Kind, x.Record, x.Date!.Value)).OrderBy(d => d.Date).ToList();

        // How far ahead an odometer reading that comes due is shown (in the vehicle's own unit).
        public const double DistanceSoon = 1000;

        // The odometer readings that come due (RecordDefinition.DueDistanceKey) within DistanceSoon of the current one, or
        // passed, the nearest first. Only the manually updated current reading counts: none without one.
        public static List<DistanceDue> DistanceDues(Entity entity)
        {
            if (entity.OdometerKm is not int now)
                return new List<DistanceDue>();
            return Records(entity.EntityType).Where(r => r.DueDistanceKey != null)
                .SelectMany(r => r.DueFrom(r.Read(entity)).Select(record => (Kind: r, Record: record, At: Values.Number(record.Get(r.DueDistanceKey!)))))
                .Where(x => x.At != null && x.At - now <= DistanceSoon)
                .Select(x => new DistanceDue(x.Kind, x.Record, x.At!.Value - now)).OrderBy(d => d.Remaining).ToList();
        }

        // The soonest one: what a card shows.
        public static Deadline? NearestExpiry(Entity entity) => Expiries(entity).FirstOrDefault();

        public static List<Dictionary<string, string>> Rows(Entity entity, LogDefinition log) =>
            entity.Logs != null && entity.Logs.TryGetValue(log.Key, out var rows) && rows != null ? rows : new List<Dictionary<string, string>>();

        // The type's fields, notes, records and logs that have something in them, as label and text in the current
        // language (export, search).
        public static List<(string Label, string Value)> Describe(Entity entity)
        {
            List<(string, string)> lines = new List<(string, string)>();
            foreach (FieldDefinition field in Fields(entity.EntityType))
            {
                string shown = field.Show(field.Read(entity), entity);
                if (shown.Length > 0)
                    lines.Add((field.Label, shown));
            }
            if (!string.IsNullOrWhiteSpace(entity.Notes))
                lines.Add((Loc.T("Entity.Field.Notes"), entity.Notes));
            foreach (RecordDefinition records in Records(entity.EntityType))
            {
                List<EntityRecord> list = records.Read(entity);
                if (list.Count > 0)
                    lines.Add((records.Label, string.Join("\n", RecordTree.Walk(list).Select(x => records.Line(x.Record, entity, x.Above)))));
            }
            foreach (LogDefinition log in Logs(entity.EntityType))
            {
                List<Dictionary<string, string>> rows = Rows(entity, log);
                if (rows.Count == 0) continue;
                IEnumerable<string> text = Sorted(log, rows).Select(row => FormatRow(log, row));
                string? summary = log.Summary?.Invoke(rows);
                lines.Add((log.Label, string.Join("\n", summary == null ? text : text.Append(summary))));
            }
            return lines;
        }

        // Everything searchable in one text, name first; null for types without a definition (searched by name).
        public static string? SearchText(Entity entity)
        {
            if (Tabs(entity.EntityType).Count == 0)
                return null;
            return string.Join("\n", new[] { entity.Name ?? "" }.Concat(Describe(entity).Select(line => line.Value)));
        }

        // "2026-10-13 | What was done: oil change | Km: 120000": the first column bare, the others named.
        public static string FormatRow(LogDefinition log, Dictionary<string, string> row)
        {
            IEnumerable<string> cells = log.Columns
                .Where(c => row.TryGetValue(c.Key, out string? v) && !string.IsNullOrWhiteSpace(v))
                .Select(c => c == log.Columns[0] ? row[c.Key] : c.Label + ": " + row[c.Key]);
            return string.Join(" | ", cells);
        }

        // Newest first: by the first date column, rows without a date last.
        public static IEnumerable<Dictionary<string, string>> Sorted(LogDefinition log, IEnumerable<Dictionary<string, string>> rows)
        {
            LogColumn? date = log.Columns.FirstOrDefault(c => c.Kind == ValueKind.Date);
            return date == null ? rows : rows.OrderByDescending(r => Values.ParseDate(r.GetValueOrDefault(date.Key)) ?? DateTime.MinValue);
        }
    }

    // How field and log values are stored: text, so one row type fits every column. Dates as yyyy-MM-dd, numbers
    // with a '.' decimal point and no thousands separator.
    public static class Values
    {
        private static readonly string[] DateFormats =
            { "yyyy-MM-dd", "yyyy.MM.dd", "yyyy.MM.dd.", "yyyy. MM. dd.", "yyyy/MM/dd" };
        // Anything else (e.g. "5/1/2027") is read in the computer's own date order (Windows' regional format, not
        // the app's language).

        // Typed text in its stored form: "" for nothing, null if it can't be read as that kind.
        public static string? Normalize(ValueKind kind, string text)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0)
                return "";

            switch (kind)
            {
                case ValueKind.Number:
                    double? number = ParseNumber(text);
                    return number?.ToString("0.###", CultureInfo.InvariantCulture);
                case ValueKind.Date:
                    DateTime? date = ParseDate(text);
                    return date == null ? null : FromDate(date);
                default:
                    return text;
            }
        }

        // Typed text (Normalize). Spaces are ignored ("120 500"). One separator is the decimal point ("6.5", "6,5"),
        // unless it has exactly three digits after it: "1,250" is 1250 in English and 1.25 in Hungarian, so that is
        // refused rather than guessed. A separator that repeats ("1.250.000") groups thousands; with both kinds
        // ("1,250.5" or "1.250,5") the last one is the decimal point. No exponents, nothing that isn't a finite number.
        private static double? ParseNumber(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;
            string plain = new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray());

            int commas = plain.Count(c => c == ','), dots = plain.Count(c => c == '.');
            if (commas > 0 && dots > 0)
            {
                char point = plain.LastIndexOf(',') > plain.LastIndexOf('.') ? ',' : '.';
                char grouping = point == ',' ? '.' : ',';
                if (plain.Count(c => c == point) > 1)
                    return null;
                plain = plain.Replace(grouping.ToString(), "").Replace(point, '.');
            }
            else if (commas + dots > 1)
            {
                char grouping = commas > 0 ? ',' : '.';
                string[] groups = plain.Split(grouping);
                if (groups.Skip(1).Any(g => g.Length != 3))
                    return null;
                plain = string.Concat(groups);
            }
            else if (commas + dots == 1)
            {
                int at = plain.IndexOfAny(new[] { ',', '.' });
                if (at > 0 && plain.Length - at - 1 == 3)
                    return null;
                plain = plain.Replace(',', '.');
            }

            return double.TryParse(plain, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                       CultureInfo.InvariantCulture, out double value) && double.IsFinite(value) ? value : null;
        }

        // A stored number (as Normalize wrote it: '.' decimal point, nothing else), read exactly.
        public static double? Number(string? stored) =>
            double.TryParse(stored, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture,
                out double value) && double.IsFinite(value) ? value : null;

        public static int? ToInt(string? text)
        {
            double? number = Number(text);
            return number == null || number < int.MinValue || number > int.MaxValue ? null : (int)Math.Round(number.Value);
        }

        public static DateTime? ParseDate(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;
            text = text.Trim();
            if (DateTime.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime exact))
                return exact.Date;
            return DateTime.TryParse(text, Loc.RegionalCulture, DateTimeStyles.None, out DateTime parsed) ? parsed.Date : null;
        }

        public static string? FromDate(DateTime? date) => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    // The fixed choices of the item form (Material, Season, a size's Condition, a vehicle's Kind). An item stores the
    // choice as its English text, so stored data never depends on the language; on screen it's shown as
    // "Choice.<letters and digits of it>" in the current language. A value that isn't one of them (typed into the
    // file, or from another version) is shown as it is.
    public static class Choices
    {
        public static readonly string[] Material = { "Cotton", "Polyester", "Wool", "Linen", "Silk", "Denim", "Leather", "Synthetic", "Mixed" };
        public static readonly string[] Season = { "All-season", "Summer", "Winter", "Spring/Autumn" };
        public static readonly string[] Condition = { "New", "Good", "Worn", "To donate", "To throw away" };

        public static string Label(string? value)
        {
            if (string.IsNullOrEmpty(value))
                return value ?? "";
            string key = "Choice." + new string(value.Where(char.IsLetterOrDigit).ToArray());
            return Loc.Find(key) ?? value;
        }
    }

    public enum ExpiryLevel { Later, Soon, Expired }

    // How close an expiry date is, in words and as a level: soon within 30 days, then expired. Shown with the level's
    // color and weight, never by color alone.
    public static class Expiry
    {
        public const int SoonDays = 30;

        public static ExpiryLevel Level(DateTime date, DateTime today)
        {
            int days = (date.Date - today.Date).Days;
            return days < 0 ? ExpiryLevel.Expired : days <= SoonDays ? ExpiryLevel.Soon : ExpiryLevel.Later;
        }

        // "in 12 days", "tomorrow", "expires today", "expired yesterday", "expired 3 days ago".
        public static string Describe(DateTime date, DateTime today)
        {
            int days = (date.Date - today.Date).Days;
            if (days > 1) return Loc.Plural("Expiry.InDays", days);
            if (days == 1) return Loc.T("Expiry.Tomorrow");
            if (days == 0) return Loc.T("Expiry.Today");
            if (days == -1) return Loc.T("Expiry.Yesterday");
            return Loc.Plural("Expiry.DaysAgo", -days);
        }

        public static Brush Brush(ExpiryLevel level) => (Brush)Application.Current.FindResource(
            level == ExpiryLevel.Expired ? "Danger" : level == ExpiryLevel.Soon ? "BtnHover" : "TextSub");

        public static FontWeight Weight(ExpiryLevel level) => level == ExpiryLevel.Later ? FontWeights.Normal : FontWeights.SemiBold;
    }
}
