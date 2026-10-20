using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TidyMind
{
    // A vehicle's documents (Entity.Records["Documents"], VehicleSchema.Documents): insurance, registration, inspection,
    // road tax, vignette or anything else, each with an optional file, a "valid until" date and how many days before it
    // to be reminded (VehicleReminders). They are the vehicle's deadlines: the card, Home and the window list them.
    //
    // Before documents a vehicle had two dates of its own (Entity.InsuranceExpires, InspectionExpires). Nothing is
    // migrated: such a vehicle shows them as an Insurance and an Inspection document, and only storing the list (an
    // edit in the window) writes it. Storing also sets the two fields to the newest date of that type, so an older
    // TidyMind still shows and keeps them; a date changed there is picked up by the newest document of that type.
    public static class VehicleDocuments
    {
        public const string Key = "Documents";
        public const string TypeKey = "Type";
        public const string NameKey = "Name";
        public const string ValidUntilKey = "ValidUntil";
        public const string RemindKey = "RemindDays";

        public const string Insurance = "Insurance";
        public const string Inspection = "Inspection";
        public static readonly string[] Types = { Insurance, "Registration", Inspection, "Road tax", "Vignette", "Other" };

        private static readonly (string Type, Func<Entity, DateTime?> Get, Action<Entity, DateTime?> Set)[] OldDates =
        {
            (Insurance, e => e.InsuranceExpires, (e, date) => e.InsuranceExpires = date),
            (Inspection, e => e.InspectionExpires, (e, date) => e.InspectionExpires = date)
        };

        public static DateTime? ValidUntil(EntityRecord document) => Values.ParseDate(document.Get(ValidUntilKey));

        // The vehicle's documents, in a list of its own. A stored document whose date is taken from an old field is a
        // copy, so the item itself is never changed by reading it.
        public static List<EntityRecord> Of(Entity vehicle)
        {
            List<EntityRecord> documents = vehicle.Records != null && vehicle.Records.TryGetValue(Key, out List<EntityRecord>? stored) && stored != null
                ? stored.Where(d => d != null).ToList() : new List<EntityRecord>();

            foreach ((string type, Func<Entity, DateTime?> get, _) in OldDates)
            {
                if (get(vehicle) is not DateTime date)
                    continue; // none, or cleared by an older version: the documents keep theirs
                List<EntityRecord> ofType = documents.Where(d => d.Get(TypeKey) == type).ToList();
                DateTime? newest = ofType.Max(ValidUntil);
                if (newest == date)
                    continue;

                EntityRecord? holder = newest == null ? null : ofType.First(d => ValidUntil(d) == newest);
                if (holder == null)
                {
                    EntityRecord shown = new EntityRecord { Id = OldDateId(vehicle, type) };
                    shown.Set(TypeKey, type);
                    shown.Set(ValidUntilKey, Values.FromDate(date));
                    documents.Add(shown);
                    continue;
                }
                EntityRecord changed = JsonSerializer.Deserialize<EntityRecord>(JsonSerializer.Serialize(holder))!;
                changed.Set(ValidUntilKey, Values.FromDate(date));
                documents[documents.IndexOf(holder)] = changed;
            }
            return documents;
        }

        // Stores the list (none: no Documents entry) and the old fields in step with it.
        public static void Store(Entity vehicle, List<EntityRecord> documents)
        {
            if (documents.Count > 0)
                (vehicle.Records ??= new Dictionary<string, List<EntityRecord>>())[Key] = documents;
            else if (vehicle.Records != null && vehicle.Records.Remove(Key) && vehicle.Records.Count == 0)
                vehicle.Records = null;

            foreach ((string type, _, Action<Entity, DateTime?> set) in OldDates)
                set(vehicle, documents.Where(d => d.Get(TypeKey) == type).Max(ValidUntil));
        }

        // What it is called in a short line ("Inspection in 12 days"): its own name, else its type.
        public static string ShortName(EntityRecord document)
        {
            string? name = document.Get(NameKey);
            return string.IsNullOrWhiteSpace(name) ? TypeLabel(document) : name.Trim();
        }

        // Its heading: the type, and its own name after it.
        public static string Title(EntityRecord document)
        {
            string? name = document.Get(NameKey);
            return string.IsNullOrWhiteSpace(name) ? TypeLabel(document) : TypeLabel(document) + " · " + name.Trim();
        }

        private static string TypeLabel(EntityRecord document) => Choices.Label(document.Get(TypeKey) ?? "Other");

        // The same Id every time for an old date shown as a document, so the window and Home can point at it until it
        // is stored.
        private static Guid OldDateId(Entity vehicle, string type) =>
            new Guid(MD5.HashData(vehicle.Id.ToByteArray().Concat(Encoding.UTF8.GetBytes(type)).ToArray()));
    }
}
