using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace TidyMind
{
    // What a vehicle record holds (EntityTypes.Vehicle): its window's tabs with their sections, record lists and logs.
    // The fields from before Details (kind, plate, year, odometer) keep their own Entity properties. Choice values are
    // stored as the English text written here.
    public static class VehicleSchema
    {
        public const string OdometerUnitKey = "OdometerUnit";
        public const string OdometerLog = "Odometer";

        private static FieldDefinition Text(string key, bool inForm = false) => new FieldDefinition { Key = key, Kind = ValueKind.Text, InForm = inForm };
        private static FieldDefinition Number(string key, string? unit = null) => new FieldDefinition { Key = key, Kind = ValueKind.Number, Unit = unit };
        private static FieldDefinition Date(string key) => new FieldDefinition { Key = key, Kind = ValueKind.Date };
        private static FieldDefinition Choice(string key, params string[] choices) => new FieldDefinition { Key = key, Kind = ValueKind.Choice, Choices = choices };

        // ---- Specs ----------------------------------------------------------------

        public static readonly SectionDefinition Identity = new SectionDefinition
        {
            Key = "Identity",
            Fields = new[]
            {
                new FieldDefinition { Key = "Kind", Kind = ValueKind.Choice, Choices = new[] { "Car", "Motorcycle", "Bicycle", "Other" }, InForm = true,
                    Get = e => e.VehicleKind, Set = (e, v) => e.VehicleKind = v },
                Text("Make", inForm: true),
                Text("Model", inForm: true),
                Text("Edition"),
                Choice("BodyType", "Sedan", "Hatchback", "Estate", "Coupe", "Convertible", "SUV", "MPV", "Pickup", "Van", "Other"),
                new FieldDefinition { Key = "Year", Kind = ValueKind.Number, InForm = true,
                    Get = e => e.Year?.ToString(CultureInfo.InvariantCulture), Set = (e, v) => e.Year = Values.ToInt(v) },
                Date("FirstRegistration"),
                new FieldDefinition { Key = "Color", Kind = ValueKind.Text, Get = e => e.Color, Set = (e, v) => e.Color = v },
                new FieldDefinition { Key = "Plate", Kind = ValueKind.Text, InForm = true, Get = e => e.Plate, Set = (e, v) => e.Plate = v },
                Text("Vin"),
                Text("RegistrationNumber")
            }
        };

        public static readonly SectionDefinition Engine = new SectionDefinition
        {
            Key = "Engine",
            Fields = new[]
            {
                Text("EngineCode"),
                Number("Displacement", "cm3"),
                new FieldDefinition { Key = "Power", Kind = ValueKind.Number, Unit = "kW", Alternate = new UnitAlternate("hp", 1.35962) },
                Number("Torque", "Nm"),
                Choice("Fuel", "Petrol", "Diesel", "Hybrid", "Plug-in hybrid", "Electric", "LPG", "CNG", "Other"),
                Choice("Gearbox", "Manual", "Automatic"),
                Number("Gears"),
                Choice("Drive", "Front-wheel", "Rear-wheel", "All-wheel"),
                Text("EmissionClass"),
                Number("Consumption", "l100km"),
                Number("Co2", "gkm")
            }
        };

        public static readonly SectionDefinition Body = new SectionDefinition
        {
            Key = "Body",
            Fields = new[] { Number("Doors"), Number("Seats"), Number("KerbWeight", "kg"), Number("GrossWeight", "kg"), Number("BootCapacity", "l") }
        };

        public static readonly SectionDefinition Ownership = new SectionDefinition
        {
            Key = "Ownership",
            Fields = new[]
            {
                Date("PurchaseDate"),
                Number("PurchasePrice", Units.Money),
                Number("PurchaseOdometer", Units.Distance),
                Number("PreviousOwners"),
                new FieldDefinition { Key = "ServiceBook", Kind = ValueKind.Flag },
                new FieldDefinition { Key = "AccidentHistory", Kind = ValueKind.LongText },
                new FieldDefinition
                {
                    Key = "Equipment", Kind = ValueKind.CheckList,
                    Choices = new[] { "Air conditioning", "Navigation", "Leather seats", "Heated seats", "Parking sensors", "Reversing camera",
                                      "Cruise control", "Tow bar", "Bluetooth", "Alloy wheels" }
                }
            }
        };

        // The current reading is the field from before Details; "Update odometer" also adds it to the Odometer log.
        public static readonly FieldDefinition Odometer = new FieldDefinition
        {
            Key = "Odometer", Kind = ValueKind.Number, Unit = Units.Distance,
            Get = e => e.OdometerKm?.ToString(CultureInfo.InvariantCulture), Set = (e, v) => e.OdometerKm = Values.ToInt(v)
        };

        public static readonly SectionDefinition OdometerSection = new SectionDefinition
        {
            Key = "Odometer",
            Fields = new[]
            {
                Odometer,
                new FieldDefinition { Key = OdometerUnitKey, Kind = ValueKind.Choice, Choices = new[] { Units.Km, Units.Mi },
                    ChoiceLabel = unit => Units.Label(unit, null) }
            }
        };

        // On the Overview, not on a tab: Entity.Notes, which every item type has.
        public static readonly SectionDefinition Notes = new SectionDefinition
        {
            Key = "Notes",
            Fields = new[] { new FieldDefinition { Key = "Notes", Kind = ValueKind.LongText, Get = e => e.Notes, Set = (e, v) => e.Notes = v } }
        };

        // ---- Documents ---------------------------------------------------------------

        public static readonly int[] RemindChoices = { 1, 7, 14, 30, 60 };

        public static readonly RecordDefinition Documents = new RecordDefinition
        {
            Key = VehicleDocuments.Key,
            Fields = new[]
            {
                Choice(VehicleDocuments.TypeKey, VehicleDocuments.Types),
                Text(VehicleDocuments.NameKey),
                Text("Number"),
                new FieldDefinition { Key = VehicleDocuments.ValidUntilKey, Kind = ValueKind.Date, IsExpiry = true },
                new FieldDefinition
                {
                    Key = VehicleDocuments.RemindKey, Kind = ValueKind.Choice,
                    Choices = RemindChoices.Select(d => d.ToString(CultureInfo.InvariantCulture)).ToArray(),
                    ChoiceLabel = days => Values.ToInt(days) is int n ? Loc.Plural("Entity.Remind.DaysBefore", n) : Loc.T("Entity.Remind.None")
                },
                new FieldDefinition { Key = "File", Kind = ValueKind.File },
                new FieldDefinition { Key = "Notes", Kind = ValueKind.LongText }
            },
            TitleKeys = new[] { VehicleDocuments.TypeKey, VehicleDocuments.NameKey },
            Title = VehicleDocuments.Title,
            Start = record =>
            {
                record.Set(VehicleDocuments.TypeKey, "Insurance");
                record.Set(VehicleDocuments.RemindKey, "30");
            },
            Load = VehicleDocuments.Of,
            Store = VehicleDocuments.Store
        };

        // ---- Logs ----------------------------------------------------------------------

        public static readonly LogDefinition[] Logs =
        {
            new LogDefinition
            {
                Key = "Service",
                Columns = new[]
                {
                    new LogColumn { Key = "Date", Kind = ValueKind.Date },
                    new LogColumn { Key = "Work", Kind = ValueKind.Text },
                    new LogColumn { Key = "Km", Kind = ValueKind.Number },
                    new LogColumn { Key = "Cost", Kind = ValueKind.Number },
                    new LogColumn { Key = "Where", Kind = ValueKind.Text }
                }
            },
            new LogDefinition
            {
                Key = "Fuel",
                Columns = new[]
                {
                    new LogColumn { Key = "Date", Kind = ValueKind.Date },
                    new LogColumn { Key = "Liters", Kind = ValueKind.Number },
                    new LogColumn { Key = "Price", Kind = ValueKind.Number },
                    new LogColumn { Key = "Km", Kind = ValueKind.Number }
                },
                Summary = FuelConsumption
            },
            new LogDefinition
            {
                Key = OdometerLog,
                Columns = new[]
                {
                    new LogColumn { Key = "Date", Kind = ValueKind.Date },
                    new LogColumn { Key = "Reading", Kind = ValueKind.Number }
                }
            }
        };

        public static LogDefinition Log(string key) => Logs.First(l => l.Key == key);

        // ---- The window's tabs, after its Overview -------------------------------------------------

        public static readonly TabDefinition[] Tabs =
        {
            new TabDefinition { Key = "Specs", Sections = new[] { Identity, Engine, Body, Ownership, OdometerSection } },
            new TabDefinition { Key = "Documents", Records = new[] { Documents } },
            new TabDefinition { Key = "Service", Logs = Logs }
        };

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
            return Loc.F("Entity.Fuel.Consumption", per100);
        }
    }
}
