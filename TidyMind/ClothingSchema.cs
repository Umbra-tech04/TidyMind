using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace TidyMind
{
    // What a garment holds (EntityTypes.Clothing): its details and its sizes, all in the Entity properties it has had
    // from the start (Brand, Color, Material, Season, PurchasePrice, Sizes), so nothing about how it is stored changes.
    // One tab: the item window shows it as a single page.
    public static class ClothingSchema
    {
        private static FieldDefinition Typed(string key, Func<Entity, string?> get, Action<Entity, string?> set, string[]? choices = null) =>
            new FieldDefinition { Key = key, Kind = choices == null ? ValueKind.Text : ValueKind.Choice, Choices = choices ?? Array.Empty<string>(),
                InForm = true, Get = get, Set = set };

        public static readonly SectionDefinition Details = new SectionDefinition
        {
            Key = "Clothing",
            Fields = new[]
            {
                Typed("Brand", e => e.Brand, (e, v) => e.Brand = v),
                Typed("Color", e => e.Color, (e, v) => e.Color = v),
                Typed("Material", e => e.Material, (e, v) => e.Material = v, Choices.Material),
                Typed("Season", e => e.Season, (e, v) => e.Season = v, Choices.Season),
                // As typed ("12 000 Ft"): never read as a number.
                Typed("PurchasePrice", e => e.PurchasePrice, (e, v) => e.PurchasePrice = v)
            }
        };

        // The garment's sizes (Entity.Sizes) as records: each with an Id that stays the same while the list does, and
        // the fields another version wrote kept through ExtensionData.
        public static readonly RecordDefinition Sizes = new RecordDefinition
        {
            Key = "Sizes",
            Fields = new[]
            {
                new FieldDefinition { Key = "Size", Kind = ValueKind.Text },
                new FieldDefinition { Key = "Condition", Kind = ValueKind.Choice, Choices = Choices.Condition },
                new FieldDefinition { Key = "Quantity", Kind = ValueKind.Number, Unit = "pcs" }
            },
            TitleKeys = new[] { "Size" },
            Title = size => string.IsNullOrWhiteSpace(size.Get("Size")) ? Loc.T("Entity.Records.Sizes.Untitled") : size.Get("Size")!.Trim(),
            Subtitle = (size, _) => Choices.Label(size.Get("Condition")),
            Figures = new[] { "Quantity" },
            Compact = true,
            StepKey = "Quantity",
            Start = (size, _) =>
            {
                size.Set("Condition", Choices.Condition[0]);
                size.Set("Quantity", "1");
            },
            Load = SizesOf,
            Store = StoreSizes
        };

        public static readonly TabDefinition[] Tabs = { new TabDefinition { Key = "Clothing", Sections = new[] { Details }, Records = new[] { Sizes } } };

        // The window's title block: the brand under the name, color, material, season and price as chips.
        public static readonly ItemHeadline Headline = new ItemHeadline(new[] { "Brand" }, new[] { "Color", "Material", "Season", "PurchasePrice" });

        private static List<EntityRecord> SizesOf(Entity garment) =>
            (garment.Sizes ?? new List<SizeQuantity>()).Where(s => s != null).Select((size, index) =>
            {
                EntityRecord record = new EntityRecord { Id = SizeId(garment, index), ExtensionData = size.ExtensionData };
                record.Set("Size", size.Size);
                record.Set("Condition", size.Condition);
                record.Set("Quantity", size.Quantity.ToString(CultureInfo.InvariantCulture));
                return record;
            }).ToList();

        private static void StoreSizes(Entity garment, List<EntityRecord> sizes) =>
            garment.Sizes = sizes.Select(record => new SizeQuantity
            {
                Size = record.Get("Size") ?? "",
                Condition = record.Get("Condition"),
                Quantity = Values.ToInt(record.Get("Quantity")) ?? 0,
                ExtensionData = record.ExtensionData
            }).ToList();

        private static Guid SizeId(Entity garment, int index) =>
            new Guid(MD5.HashData(garment.Id.ToByteArray().Concat(Encoding.UTF8.GetBytes("Size" + index)).ToArray()));
    }
}
