using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace TidyMind
{
    public enum ValueKind { Text, Number, Choice, Date }

    // One field of a type described here: how the item form, the detail card, search and export show it. Values go
    // in and out as stored text (Values).
    public class FieldDefinition
    {
        public string Label { get; init; } = "";
        public ValueKind Kind { get; init; }
        public string[] Choices { get; init; } = Array.Empty<string>();

        // A date that runs out (insurance, inspection): highlighted when close, listed on Home. Name is how it's
        // called in a short line ("Inspection in 12 days").
        public bool IsExpiry { get; init; }
        public string ExpiryName { get; init; } = "";

        public Func<Entity, string?> Get { get; init; } = _ => null;
        public Action<Entity, string?> Set { get; init; } = (_, _) => { };
    }

    public class LogColumn
    {
        public string Key { get; init; } = "";
        public string Label { get; init; } = "";
        public ValueKind Kind { get; init; }
    }

    // A log field: a list of rows with the type's columns (LogEditor in the form). Summary: an optional line computed
    // from the rows (never stored), or null when there's nothing to say.
    public class LogDefinition
    {
        public string Key { get; init; } = "";
        public string Label { get; init; } = "";
        public LogColumn[] Columns { get; init; } = Array.Empty<LogColumn>();
        public Func<IReadOnlyList<Dictionary<string, string>>, string?>? Summary { get; init; }
    }

    // The item types described by definitions (Vehicle). Clothing keeps its own fields in EntityForm, CollectionView
    // and ExportService and has no definition here; Fields and Logs are empty for it.
    public static class EntityTypes
    {
        public const string Clothing = "Clothing";
        public const string Vehicle = "Vehicle";

        private static readonly FieldDefinition[] VehicleFields =
        {
            new FieldDefinition { Label = "Kind", Kind = ValueKind.Choice, Choices = new[] { "Car", "Motorcycle", "Bicycle", "Other" },
                Get = e => e.VehicleKind, Set = (e, v) => e.VehicleKind = v },
            new FieldDefinition { Label = "Plate", Kind = ValueKind.Text, Get = e => e.Plate, Set = (e, v) => e.Plate = v },
            new FieldDefinition { Label = "Year", Kind = ValueKind.Number,
                Get = e => e.Year?.ToString(CultureInfo.InvariantCulture), Set = (e, v) => e.Year = Values.ToInt(v) },
            new FieldDefinition { Label = "Odometer (km)", Kind = ValueKind.Number,
                Get = e => e.OdometerKm?.ToString(CultureInfo.InvariantCulture), Set = (e, v) => e.OdometerKm = Values.ToInt(v) },
            new FieldDefinition { Label = "Insurance expires", Kind = ValueKind.Date, IsExpiry = true, ExpiryName = "Insurance",
                Get = e => Values.FromDate(e.InsuranceExpires), Set = (e, v) => e.InsuranceExpires = Values.ParseDate(v) },
            new FieldDefinition { Label = "Inspection expires", Kind = ValueKind.Date, IsExpiry = true, ExpiryName = "Inspection",
                Get = e => Values.FromDate(e.InspectionExpires), Set = (e, v) => e.InspectionExpires = Values.ParseDate(v) }
        };

        private static readonly LogDefinition[] VehicleLogs =
        {
            new LogDefinition
            {
                Key = "Service", Label = "Service log",
                Columns = new[]
                {
                    new LogColumn { Key = "Date", Label = "Date", Kind = ValueKind.Date },
                    new LogColumn { Key = "Work", Label = "What was done", Kind = ValueKind.Text },
                    new LogColumn { Key = "Km", Label = "Km", Kind = ValueKind.Number },
                    new LogColumn { Key = "Cost", Label = "Cost", Kind = ValueKind.Number },
                    new LogColumn { Key = "Where", Label = "Where", Kind = ValueKind.Text }
                }
            },
            new LogDefinition
            {
                Key = "Fuel", Label = "Fuel log",
                Columns = new[]
                {
                    new LogColumn { Key = "Date", Label = "Date", Kind = ValueKind.Date },
                    new LogColumn { Key = "Liters", Label = "Liters", Kind = ValueKind.Number },
                    new LogColumn { Key = "Price", Label = "Price", Kind = ValueKind.Number },
                    new LogColumn { Key = "Km", Label = "Km", Kind = ValueKind.Number }
                },
                Summary = FuelConsumption
            }
        };

        // What the item's Name is called in the form ("Make and model" for a vehicle, the card title).
        public static string NameLabel(string? type) => type == Vehicle ? "Make and model" : "Name";

        public static IReadOnlyList<FieldDefinition> Fields(string? type) =>
            type == Vehicle ? VehicleFields : Array.Empty<FieldDefinition>();

        public static IReadOnlyList<LogDefinition> Logs(string? type) =>
            type == Vehicle ? VehicleLogs : Array.Empty<LogDefinition>();

        // The item's expiry dates that are set, with their short names.
        public static IEnumerable<(string Name, DateTime Date)> Expiries(Entity entity)
        {
            foreach (FieldDefinition field in Fields(entity.EntityType).Where(f => f.IsExpiry))
            {
                DateTime? date = Values.ParseDate(field.Get(entity));
                if (date != null)
                    yield return (field.ExpiryName, date.Value);
            }
        }

        // The soonest one (an expired date first): what a card shows.
        public static (string Name, DateTime Date)? NearestExpiry(Entity entity)
        {
            List<(string Name, DateTime Date)> all = Expiries(entity).OrderBy(x => x.Date).ToList();
            return all.Count == 0 ? null : all[0];
        }

        public static List<Dictionary<string, string>> Rows(Entity entity, LogDefinition log) =>
            entity.Logs != null && entity.Logs.TryGetValue(log.Key, out var rows) && rows != null ? rows : new List<Dictionary<string, string>>();

        // The type's fields, notes and logs that have something in them, as label and text (detail card, export).
        public static List<(string Label, string Value)> Describe(Entity entity)
        {
            List<(string, string)> lines = new List<(string, string)>();
            foreach (FieldDefinition field in Fields(entity.EntityType))
            {
                string? value = field.Get(entity);
                if (!string.IsNullOrWhiteSpace(value))
                    lines.Add((field.Label, value));
            }
            if (!string.IsNullOrWhiteSpace(entity.Notes))
                lines.Add(("Notes", entity.Notes));
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
            if (Fields(entity.EntityType).Count == 0)
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

        // From the two newest fill-ups: the newer one's liters over the km driven since the older one. Nothing if
        // either lacks the numbers, or the km don't go up.
        private static string? FuelConsumption(IReadOnlyList<Dictionary<string, string>> rows)
        {
            // Newest by date; two on the same day by km.
            List<Dictionary<string, string>> newest = rows
                .OrderByDescending(r => Values.ParseDate(r.GetValueOrDefault("Date")) ?? DateTime.MinValue)
                .ThenByDescending(r => Values.Number(r.GetValueOrDefault("Km")) ?? 0)
                .Take(2).ToList();
            if (newest.Count < 2)
                return null;

            double? liters = Values.Number(newest[0].GetValueOrDefault("Liters"));
            double? kmNow = Values.Number(newest[0].GetValueOrDefault("Km"));
            double? kmBefore = Values.Number(newest[1].GetValueOrDefault("Km"));
            if (liters == null || kmNow == null || kmBefore == null || liters <= 0 || kmNow <= kmBefore)
                return null;

            double per100 = liters.Value / (kmNow.Value - kmBefore.Value) * 100;
            return "Consumption: " + per100.ToString("0.0", CultureInfo.InvariantCulture) + " l/100 km (last two fill-ups)";
        }
    }

    // How field and log values are stored: text, so one row type fits every column. Dates as yyyy-MM-dd, numbers
    // with a '.' decimal point and no thousands separator.
    public static class Values
    {
        private static readonly string[] DateFormats =
            { "yyyy-MM-dd", "yyyy.MM.dd", "yyyy.MM.dd.", "yyyy. MM. dd.", "yyyy/MM/dd" };
        // Anything else (e.g. "5/1/2027") is read in the computer's own date order, as the date pickers show it.

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
            return DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out DateTime parsed) ? parsed.Date : null;
        }

        public static string? FromDate(DateTime? date) => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
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
            if (days > 1) return "in " + days + " days";
            if (days == 1) return "tomorrow";
            if (days == 0) return "expires today";
            if (days == -1) return "expired yesterday";
            return "expired " + -days + " days ago";
        }

        public static Brush Brush(ExpiryLevel level) => (Brush)Application.Current.FindResource(
            level == ExpiryLevel.Expired ? "Danger" : level == ExpiryLevel.Soon ? "BtnHover" : "TextSub");

        public static FontWeight Weight(ExpiryLevel level) => level == ExpiryLevel.Later ? FontWeights.Normal : FontWeights.SemiBold;
    }
}
