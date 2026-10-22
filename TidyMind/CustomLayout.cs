using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TidyMind
{
    // The blocks a custom collection's items are made of, stored as these English names whatever the language. A kind
    // this version doesn't know (from a newer one) is kept in the file and not shown.
    public static class BlockKinds
    {
        public const string Fields = "Fields";         // labelled values (LayoutField), in Entity.Details by field Id
        public const string Gallery = "Gallery";       // the item's own pictures (cover and MoreImages); one per layout
        public const string Notes = "Notes";           // free text, in Entity.Details by block Id
        public const string Checklist = "Checklist";   // ticked lines, Entity.Records by block Id
        public const string Documents = "Documents";   // documents that run out (a reminder each), Entity.Records
        public const string Log = "Log";               // dated entries with an amount, Entity.Records
        public const string Quantities = "Quantities"; // name, amount, unit, Entity.Records

        public static readonly string[] All = { Fields, Gallery, Notes, Checklist, Documents, Log, Quantities };

        public static bool Known(string? kind) => kind != null && All.Contains(kind);
    }

    // The kinds of a Fields block's field, stored as these English names.
    public static class FieldKinds
    {
        public const string Text = "Text", Number = "Number", Date = "Date", Flag = "Flag", Choice = "Choice";

        public static readonly string[] All = { Text, Number, Date, Flag, Choice };

        public static ValueKind? ValueKindOf(string? kind) => kind switch
        {
            Text => ValueKind.Text,
            Number => ValueKind.Number,
            Date => ValueKind.Date,
            Flag => ValueKind.Flag,
            Choice => ValueKind.Choice,
            _ => null
        };
    }

    // A custom collection's item layout (MemoryFiles.Layout): its blocks top to bottom, each full or half width (two
    // half blocks in a row stand side by side), and the fields its cards show. Defined once for the collection; the
    // items hold only the blocks' data. Fields another version wrote are kept (ExtensionData).
    public class CustomLayout
    {
        public const int MaxCardFields = 3;

        public List<LayoutBlock> Blocks { get; set; } = new List<LayoutBlock>();

        // Field Ids (of Fields blocks) a card shows under the item's name, in order; at most MaxCardFields.
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string>? CardFields { get; set; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? ExtensionData { get; set; }

        public LayoutBlock? Block(string id) => Blocks.FirstOrDefault(b => b.Id == id);

        // The Fields block holding a field, and the field.
        public (LayoutBlock Block, LayoutField Field)? FieldById(string id)
        {
            foreach (LayoutBlock block in Blocks.Where(b => b.Kind == BlockKinds.Fields))
                foreach (LayoutField field in block.Fields ?? new List<LayoutField>())
                    if (field.Id == id)
                        return (block, field);
            return null;
        }

        public bool HasGallery => Blocks.Any(b => b.Kind == BlockKinds.Gallery);

        // What a file from elsewhere may hold that the views don't expect: nulls in lists, blocks or fields without an
        // Id or sharing one (they would share their data), a second gallery, card fields that are no field. Such
        // entries are dropped; everything else, unknown kinds included, stays.
        public CustomLayout Clean()
        {
            HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
            bool gallery = false;
            Blocks = (Blocks ?? new List<LayoutBlock>()).Where(b => b != null && !string.IsNullOrEmpty(b.Id) && ids.Add(b.Id)).ToList();
            foreach (LayoutBlock block in Blocks)
            {
                block.Name ??= "";
                block.Kind ??= "";
                if (block.Kind == BlockKinds.Gallery && gallery)
                    block.Kind = "";  // a second picture set the item doesn't have: an unknown block, left out
                gallery |= block.Kind == BlockKinds.Gallery;
                if (block.Fields != null)
                    block.Fields = block.Fields.Where(f => f != null && !string.IsNullOrEmpty(f.Id) && ids.Add(f.Id)).ToList();
                foreach (LayoutField field in block.Fields ?? new List<LayoutField>())
                {
                    field.Name ??= "";
                    field.Kind ??= "";
                    field.Choices = field.Choices?.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.Ordinal).ToList();
                }
            }
            HashSet<string> fields = Blocks.Where(b => b.Kind == BlockKinds.Fields).SelectMany(b => b.Fields ?? new List<LayoutField>())
                .Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
            List<string>? card = CardFields?.Where(id => id != null && fields.Contains(id)).Distinct().Take(MaxCardFields).ToList();
            CardFields = card?.Count > 0 ? card : null;
            return this;
        }

        public CustomLayout Copy() => JsonSerializer.Deserialize<CustomLayout>(JsonSerializer.Serialize(this))!;

        public static string NewId() => Guid.NewGuid().ToString("N");

        // What a new custom collection starts with: its pictures, a block for its details and one for notes, named in the
        // current language (the user's own from then on, like the vehicle parts template).
        public static CustomLayout Starter() => new CustomLayout
        {
            Blocks = new List<LayoutBlock>
            {
                new LayoutBlock { Id = NewId(), Kind = BlockKinds.Gallery, Name = Loc.T("Custom.Block.Gallery") },
                new LayoutBlock { Id = NewId(), Kind = BlockKinds.Fields, Name = Loc.T("Custom.Starter.Details"), Fields = new List<LayoutField>() },
                new LayoutBlock { Id = NewId(), Kind = BlockKinds.Notes, Name = Loc.T("Custom.Block.Notes") }
            }
        };
    }

    // One block of a layout. Id names its data in every item (Entity.Details or Entity.Records); Name is the user's.
    public class LayoutBlock
    {
        public string Id { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Name { get; set; } = "";

        // Half the width: beside the half block before or after it, when there is room.
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool Half { get; set; }

        // A Fields block's fields, in order.
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<LayoutField>? Fields { get; set; }

        // A Log block's amounts: money (null or true, shown with the currency) or a number in Unit (none: a plain number).
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? Money { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Unit { get; set; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? ExtensionData { get; set; }

        [JsonIgnore]
        public bool IsMoney => Money != false;
    }

    // A field of a Fields block: its Id names its value in Entity.Details; Name, choices and unit are the user's.
    public class LayoutField
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Kind { get; set; } = FieldKinds.Text;

        // A Choice field's values, stored as written (the user's own words).
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string>? Choices { get; set; }

        // A Number field's unit, shown after the number ("kg").
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Unit { get; set; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? ExtensionData { get; set; }
    }

    // A custom collection's layout file. No file: the collection isn't custom.
    public static class CustomLayoutStore
    {
        // False (problem says why) if the file is there but can't be read: the collection is then shown read-only and
        // the file never saved over. layout is null when there is no file.
        public static bool TryLoad(Profile memory, out CustomLayout? layout, out string problem) => TryLoad(memory.Id, out layout, out problem);

        public static bool TryLoad(Guid memoryId, out CustomLayout? layout, out string problem)
        {
            layout = null;
            problem = "";
            string file = MemoryFiles.Layout(memoryId);
            try
            {
                if (!File.Exists(file))
                    return true;
                layout = (JsonSerializer.Deserialize<CustomLayout>(File.ReadAllText(file)) ?? throw new JsonException(Loc.T("Custom.Layout.Empty"))).Clean();
                return true;
            }
            catch (Exception e) when (e is JsonException || e is IOException || e is UnauthorizedAccessException || e is NotSupportedException)
            {
                problem = e.Message;
                return false;
            }
        }

        // Written whole through AtomicFile; the definitions made from it (CustomTypes) follow. False if it couldn't be
        // written: the file and the definitions are then as they were.
        public static bool TrySave(Profile memory, CustomLayout layout) => TrySave(memory.Id, layout);

        public static bool TrySave(Guid memoryId, CustomLayout layout)
        {
            if (!AtomicFile.TryWriteAllText(MemoryFiles.Layout(memoryId), JsonSerializer.Serialize(layout)))
                return false;
            CustomTypes.Forget(memoryId);
            return true;
        }

        // Only an undo of a layout's first save takes a file away (none is ever made undoable now): no file, not custom.
        internal static bool TryDelete(Guid memoryId)
        {
            try
            {
                string file = MemoryFiles.Layout(memoryId);
                if (File.Exists(file))
                    File.Delete(file);
                CustomTypes.Forget(memoryId);
                return true;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return false;
            }
        }
    }
}
