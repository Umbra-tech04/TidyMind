using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TidyMind
{
    public class Entity
    {
        // How views and search point at this item. Items from before Ids existed get one the first time their file
        // is read (EntityStore.Load).
        public Guid Id { get; set; }

        public string EntityType { get; set; }
        public string Name { get; set; }

        // A file name inside EntityImages/ (ImageStore.Items). Items from before that hold the full path of the
        // user's own picture instead, until the collection view copies it in (EntityImages).
        public string ImagePath { get; set; }

        // The item's further pictures, in order after ImagePath (the cover): same kind of file names. Left out of the
        // file while there are none, so an item with at most one picture is stored exactly as before; a version
        // that doesn't know this keeps it as an unknown field (ExtensionData). Read and set through EntityImages.
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string>? MoreImages { get; set; }

        public string Notes { get; set; }
        public Guid TabId { get; set; }
        public int Order { get; set; }

        // Set when the item is added (DateTime.MinValue for items from before this existed).
        public DateTime CreatedDate { get; set; }

        // Clothing
        public string Brand { get; set; }
        public string Color { get; set; }
        public string Material { get; set; }
        public string Season { get; set; }
        public string PurchasePrice { get; set; }
        public List<SizeQuantity> Sizes { get; set; } = new List<SizeQuantity>();

        // Vehicle (EntityTypes.Vehicle); its name (a nickname, or the make and model) is Name, its other fields are in
        // Details and Records. Left out of the file while empty, so other items are stored exactly as before. The two
        // dates are the newest insurance and inspection document's (VehicleDocuments keeps them in step), so a version
        // from before documents still shows them.
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? VehicleKind { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Plate { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? Year { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? OdometerKm { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public DateTime? InsuranceExpires { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public DateTime? InspectionExpires { get; set; }

        // The type's other single-value fields (EntityTypes, FieldDefinition) by field key, as stored text (Values),
        // e.g. "Make" or "Displacement". Left out of the file while empty; a version that doesn't know it keeps it as an
        // unknown field.
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, string>? Details { get; set; }

        // The type's lists of records (EntityTypes, RecordDefinition) by key, e.g. "Documents". Left out while empty.
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, List<EntityRecord>>? Records { get; set; }

        // Log fields (EntityTypes, LogDefinition) by log key, e.g. "Fuel": each row maps column keys to stored text
        // (Values: dates as yyyy-MM-dd, numbers with a '.' decimal point). Left out until a log has rows.
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, List<Dictionary<string, string>>>? Logs { get; set; }

        // Fields this version doesn't know (written by another TidyMind version): kept so saving doesn't drop them.
        [JsonExtensionData]
        public Dictionary<string, JsonElement> ExtensionData { get; set; }
    }
}
