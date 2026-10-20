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
                RemindField(),
                new FieldDefinition { Key = "File", Kind = ValueKind.File },
                new FieldDefinition { Key = "Notes", Kind = ValueKind.LongText }
            },
            TitleKeys = new[] { VehicleDocuments.TypeKey, VehicleDocuments.NameKey },
            Title = VehicleDocuments.Title,
            Subtitle = (document, _) => document.Get("Number") ?? "",
            Figures = new[] { VehicleDocuments.ValidUntilKey },
            Icon = _ => "", // Document
            Start = (record, _) =>
            {
                record.Set(VehicleDocuments.TypeKey, "Insurance");
                record.Set(VehicleDocuments.RemindKey, "30");
            },
            Load = VehicleDocuments.Of,
            Store = VehicleDocuments.Store,
            DueKey = VehicleDocuments.ValidUntilKey,
            RemindKey = VehicleDocuments.RemindKey,
            DeadlineName = VehicleDocuments.ShortName,
            ReminderText = (document, date) => Loc.F("Entity.Reminder.Message", VehicleDocuments.Title(document), date.ToString(Loc.T("Format.LongDate"), Loc.Culture))
        };

        // How many days before a date to be reminded of it (VehicleReminders); nothing: no reminder.
        private static FieldDefinition RemindField() => new FieldDefinition
        {
            Key = VehicleDocuments.RemindKey, Kind = ValueKind.Choice,
            Choices = RemindChoices.Select(d => d.ToString(CultureInfo.InvariantCulture)).ToArray(),
            ChoiceLabel = days => Values.ToInt(days) is int n ? Loc.Plural("Entity.Remind.DaysBefore", n) : Loc.T("Entity.Remind.None")
        };

        // ---- Wheels and tyres -----------------------------------------------------------------

        public const string MountedWheelSetKey = "MountedWheelSet";

        public static readonly RecordDefinition WheelSets = new RecordDefinition
        {
            Key = "WheelSets",
            Fields = new[]
            {
                Text("Name"),
                Choice("Season", "Summer", "Winter", "All-season"),
                Text("TyreBrand"),
                Text("TyreModel"),
                Text("TyreSize"),
                Text("Dot"),
                Number("TreadDepth", "mm"),
                Text("Pressure"),
                Choice("RimType", "Alloy", "Steel"),
                Text("RimSize"),
                Text("BoltPattern"),
                Number("Offset", "mm"),
                Number("CentreBore", "mm"),
                Text("BoltType"),
                Number("BoltTorque", "Nm"),
                Text("Storage"),
                Number("SetDistance", Units.Distance),
                new FieldDefinition { Key = "Notes", Kind = ValueKind.LongText }
            },
            TitleKeys = new[] { "Name" },
            Title = set => FirstOf(set.Get("Name"), Choices.Label(set.Get("Season") ?? ""), Loc.T("Entity.Records.WheelSets.Untitled")),
            Subtitle = (set, _) => string.Join(" · ", new[] { set.Get("TyreSize"), set.Get("RimSize") }.Where(t => !string.IsNullOrWhiteSpace(t))),
            Figures = new[] { "SetDistance" },
            CurrentKey = MountedWheelSetKey
        };

        // ---- Fluids, brakes, electrical ---------------------------------------------------------

        public static readonly SectionDefinition Fluids = new SectionDefinition
        {
            Key = "Fluids",
            Fields = new[]
            {
                Text("EngineOil"), Text("OilViscosity"), Number("OilCapacity", "l"),
                Text("Coolant"), Number("CoolantCapacity", "l"), Text("BrakeFluid"),
                Text("GearboxOil"), Number("GearboxOilCapacity", "l"),
                Text("OilFilter"), Text("AirFilter"), Text("CabinFilter"), Text("FuelFilter"),
                Text("WipersFront"), Text("WiperRear")
            }
        };

        public static readonly SectionDefinition Brakes = new SectionDefinition
        {
            Key = "Brakes",
            Fields = new[] { Text("FrontDiscs"), Text("FrontPads"), Text("RearDiscs"), Text("RearPads") }
        };

        public static readonly SectionDefinition Electrical = new SectionDefinition
        {
            Key = "Electrical",
            Fields = new[]
            {
                Text("Battery"), Number("BatteryCapacity", "Ah"), Number("BatteryCurrent", "A"),
                Text("BulbLowBeam"), Text("BulbHighBeam"), Text("BulbFog"), Text("BulbIndicator"), Text("BulbBrake"),
                Text("BulbReverse"), Text("BulbPlate"), Text("BulbInterior"),
                new FieldDefinition { Key = "FuseNotes", Kind = ValueKind.LongText }
            }
        };

        // ---- Parts --------------------------------------------------------------------------------

        // What "Start with the usual systems" creates, as top-level parts named in the current language (the user's own
        // from then on).
        public static readonly string[] PartsTemplate = { "Engine", "Transmission", "Brakes", "Suspension", "Wheels", "Electrical", "Body", "Interior", "Fluids" };

        public static readonly RecordDefinition Parts = new RecordDefinition
        {
            Key = "Parts",
            Nested = true,
            Fields = new[]
            {
                Text("Name"), Text("Brand"), Text("OemNumber"), Text("PartNumber"), Number("Quantity"),
                Date("PurchaseDate"), Number("Price", Units.Money), Text("Shop"), Text("Link"),
                Date("InstalledDate"), Number("InstalledOdometer", Units.Distance),
                new FieldDefinition { Key = "Specs", Kind = ValueKind.Specs },
                new FieldDefinition { Key = "Photos", Kind = ValueKind.Pictures },
                new FieldDefinition { Key = "Notes", Kind = ValueKind.LongText }
            },
            TitleKeys = new[] { "Name" },
            Title = part => FirstOf(part.Get("Name"), Loc.T("Entity.Records.Parts.Untitled")),
            Subtitle = (part, _) => string.Join(" · ", new[] { part.Get("Brand"), part.Get("PartNumber") ?? part.Get("OemNumber") }.Where(t => !string.IsNullOrWhiteSpace(t))),
            Figures = new[] { "InstalledDate" }
        };

        private static string FirstOf(params string?[] texts) => texts.First(t => !string.IsNullOrWhiteSpace(t))!.Trim();

        // ---- Service log ------------------------------------------------------------------------------

        public static readonly RecordDefinition ServiceLog = new RecordDefinition
        {
            Key = VehicleService.Key,
            Fields = new[]
            {
                Choice(VehicleService.TypeKey, VehicleService.Types),
                Date(VehicleService.DateKey),
                Number(VehicleService.OdometerKey, Units.Distance),
                new FieldDefinition { Key = "Work", Kind = ValueKind.LongText },
                Text("Where"),
                Number("Cost", Units.Money),
                new FieldDefinition { Key = VehicleService.PartsKey, Kind = ValueKind.Links, LinkTo = "Parts" },
                Date("NextDueDate"),
                Number("NextDueOdometer", Units.Distance),
                RemindField(),
                new FieldDefinition { Key = "File", Kind = ValueKind.File }
            },
            TitleKeys = new[] { VehicleService.TypeKey, VehicleService.DateKey },
            Title = VehicleService.Title,
            Subtitle = (entry, _) => string.Join(" · ", new[] { entry.Get("Work")?.Split('\n')[0].Trim(), entry.Get("Where") }.Where(t => !string.IsNullOrWhiteSpace(t))),
            Figures = new[] { VehicleService.OdometerKey, "Cost" },
            Icon = VehicleService.Icon,
            // A new entry: a service, today, at the current reading, reminded two weeks before its next due date.
            Start = (entry, vehicle) =>
            {
                entry.Set(VehicleService.TypeKey, VehicleService.Types[0]);
                entry.Set(VehicleService.DateKey, Values.FromDate(DateTime.Today));
                entry.Set(VehicleService.OdometerKey, Odometer.Read(vehicle));
                entry.Set(VehicleDocuments.RemindKey, "14");
            },
            Load = VehicleService.Of,
            Store = VehicleService.Store,
            NewestFirstKey = VehicleService.DateKey,
            FilterKey = VehicleService.TypeKey,
            TotalKey = "Cost",
            DueKey = "NextDueDate",
            RemindKey = VehicleDocuments.RemindKey,
            DueDistanceKey = "NextDueOdometer",
            DueFrom = VehicleService.Latest,
            DeadlineName = VehicleService.TypeLabel,
            ReminderText = (entry, date) => Loc.F("Entity.Reminder.Due", VehicleService.TypeLabel(entry), date.ToString(Loc.T("Format.LongDate"), Loc.Culture)),
            Saved = VehicleService.FitParts
        };

        // ---- Logs ----------------------------------------------------------------------

        public static readonly LogDefinition[] Logs =
        {
            new LogDefinition
            {
                Key = "Fuel",
                Columns = new[]
                {
                    new LogColumn { Key = "Date", Kind = ValueKind.Date },
                    new LogColumn { Key = "Liters", Kind = ValueKind.Number, Unit = "l" },
                    new LogColumn { Key = "Price", Kind = ValueKind.Number, Unit = Units.Money },
                    new LogColumn { Key = "Km", Kind = ValueKind.Number, Unit = Units.Distance }
                },
                Summary = FuelConsumption
            },
            new LogDefinition
            {
                Key = OdometerLog,
                Columns = new[]
                {
                    new LogColumn { Key = "Date", Kind = ValueKind.Date },
                    new LogColumn { Key = "Reading", Kind = ValueKind.Number, Unit = Units.Distance }
                }
            }
        };

        public static LogDefinition Log(string key) => Logs.First(l => l.Key == key);

        // The window's title block: "Škoda Octavia III RS", the year, fuel, power, gearbox, drive and odometer as chips,
        // the kind above the name, the plate as a badge.
        public static readonly ItemHeadline Headline = new ItemHeadline(
            new[] { "Make", "Model", "Edition" }, new[] { "Year", "Fuel", "Power", "Gearbox", "Drive", "Odometer" }, Eyebrow: "Kind", Badge: "Plate");

        // ---- The window's tabs, after its Overview -------------------------------------------------

        public static readonly TabDefinition[] Tabs =
        {
            new TabDefinition { Key = "Specs", Sections = new[] { Identity, Engine, Body, Ownership, OdometerSection } },
            new TabDefinition { Key = "Wheels", Records = new[] { WheelSets } },
            new TabDefinition { Key = "Parts", Sections = new[] { Fluids, Brakes, Electrical }, Records = new[] { Parts } },
            new TabDefinition { Key = "Documents", Records = new[] { Documents } },
            new TabDefinition { Key = "Service", Records = new[] { ServiceLog }, Logs = Logs }
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
