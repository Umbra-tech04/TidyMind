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

        // Fields this version doesn't know (written by another TidyMind version): kept so saving doesn't drop them.
        [JsonExtensionData]
        public Dictionary<string, JsonElement> ExtensionData { get; set; }
    }
}
