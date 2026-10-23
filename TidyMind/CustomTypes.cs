using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace TidyMind
{
    // The type of a custom collection's items is "Custom:<the collection's Id>" (Entity.EntityType): the type itself says
    // whose layout it is, so everything that asks EntityTypes about an item's type (the item window, search, exports,
    // transfer, reminders, Home) gets the definitions made from that layout (CustomType). A layout is read once and kept
    // until it is saved again (CustomLayoutStore) or the data restored.
    public static class CustomTypes
    {
        public const string Prefix = "Custom:";

        private static readonly object gate = new object();
        private static readonly Dictionary<Guid, CustomType?> made = new Dictionary<Guid, CustomType?>();

        // Fallback names (a block or field without one) are in the language shown: made again after a switch.
        static CustomTypes() => Loc.LanguageChanged += ForgetAll;

        public static string TypeOf(Guid memoryId) => Prefix + memoryId.ToString("N");

        public static bool IsCustom(string? type) => type != null && type.StartsWith(Prefix, StringComparison.Ordinal);

        public static Guid? MemoryOf(string? type) =>
            IsCustom(type) && Guid.TryParseExact(type!.Substring(Prefix.Length), "N", out Guid id) ? id : null;

        // A custom type's definitions; null for any other type, and for one whose layout is gone or can't be read (its
        // items then show as a type this version doesn't know: name, pictures, notes, the rest kept as it is).
        public static CustomType? Of(string? type)
        {
            if (MemoryOf(type) is not Guid id)
                return null;
            lock (gate)
            {
                if (made.TryGetValue(id, out CustomType? known))
                    return known;
                // A file that can't be read now (locked a moment by a backup or a virus scan) is read again next time:
                // only a layout, or no file at all, is kept.
                if (!CustomLayoutStore.TryLoad(id, out CustomLayout? layout, out _))
                    return null;
                CustomType? built = layout != null ? new CustomType(id, layout) : null;
                made[id] = built;
                return built;
            }
        }

        public static CustomType? OfMemory(Profile memory) => Of(TypeOf(memory.Id));

        // Its layout file was written or removed: read again when next asked.
        internal static void Forget(Guid memoryId)
        {
            lock (gate)
                made.Remove(memoryId);
        }

        // The data folder was replaced (a restore).
        public static void ForgetAll()
        {
            lock (gate)
                made.Clear();
        }
    }

    // The dates custom items have on a day, for the Day Plan: their Fields blocks' date fields and the dates their documents
    // run out. Read only; a collection whose items or layout can't be read has none.
    public static class CustomDates
    {
        public sealed record ItemDate(Profile Memory, Entity Item, string What);

        // Each custom collection's items, kept while its items file is unchanged: the Day Plan asks after every change on it.
        private static readonly Dictionary<Guid, (DateTime Written, List<Entity> Items)> read = new Dictionary<Guid, (DateTime, List<Entity>)>();

        public static List<ItemDate> On(IEnumerable<Profile> memories, DateTime day)
        {
            List<ItemDate> dates = new List<ItemDate>();
            foreach (Profile memory in memories.Where(m => m.Type == ProfileType.Collection))
            {
                if (CustomTypes.OfMemory(memory) is not CustomType custom)
                    continue;
                List<FieldDefinition> dateFields = custom.Tabs[0].Sections.SelectMany(s => s.Fields).Where(f => f.Kind == ValueKind.Date).ToList();
                foreach (Entity item in ItemsOf(memory).Where(e => e.EntityType == custom.Type))
                {
                    foreach (FieldDefinition field in dateFields.Where(f => Values.ParseDate(f.Read(item)) == day.Date))
                        dates.Add(new ItemDate(memory, item, field.Label));
                    foreach (Deadline deadline in EntityTypes.Expiries(item).Where(d => d.Date == day.Date))
                        dates.Add(new ItemDate(memory, item, Loc.F("Custom.Plan.RunsOut", deadline.Name)));
                }
            }
            return dates;
        }

        // A file that can't be read gives none, and is read again next time.
        private static List<Entity> ItemsOf(Profile memory)
        {
            string file = MemoryFiles.Items(memory);
            DateTime written = File.Exists(file) ? File.GetLastWriteTimeUtc(file) : DateTime.MinValue;
            lock (read)
            {
                if (read.TryGetValue(memory.Id, out var known) && known.Written == written)
                    return known.Items;
                if (!EntityStore.TryLoad(memory, out List<Entity> items, out _))
                    return new List<Entity>();
                read[memory.Id] = (written, items);
                return items;
            }
        }
    }

    // A block as the item window draws it: its layout entry and its definition. Fields and Notes are a section of the
    // item's Details; Checklist, Documents, Log and Quantities a list of its Records; the Gallery is its own pictures.
    public sealed record CustomBlock(LayoutBlock Layout, SectionDefinition? Section, RecordDefinition? Records)
    {
        public string Id => Layout.Id;
        public string Kind => Layout.Kind;
        public string Name => Layout.Name;
        public bool Half => Layout.Half;
    }

    // One custom collection's layout as definitions. Made once per saved layout, so every item of the collection shares
    // the same objects (the Excel export groups record lists by them).
    public sealed class CustomType
    {
        // The record fields of the kinds of block (their keys inside each record; names from "Entity.Field.<key>").
        public const string TextKey = "Text", DoneKey = "Done";
        public const string NameKey = "Name", NumberKey = "Number", ValidUntilKey = "ValidUntil", RemindKey = "RemindDays", FileKey = "File", NotesKey = "Notes";
        public const string DateKey = "Date", EntryKey = "Entry", AmountKey = "Amount", QuantityKey = "Quantity", UnitKey = "Unit";

        public Guid MemoryId { get; }
        public string Type => CustomTypes.TypeOf(MemoryId);

        // As read from the file: change a Copy() and save that (CustomLayoutStore), never this one.
        public CustomLayout Layout { get; }

        // The known blocks, top to bottom.
        public IReadOnlyList<CustomBlock> Blocks { get; }

        // One page with every section and list, for what goes through EntityTypes (search, Excel, transfer, reminders).
        public IReadOnlyList<TabDefinition> Tabs { get; }

        // What a card shows under the name (CustomLayout.CardFields), in order.
        public IReadOnlyList<FieldDefinition> CardFields { get; }

        internal CustomType(Guid memoryId, CustomLayout layout)
        {
            MemoryId = memoryId;
            Layout = layout;
            HashSet<string> onCard = (layout.CardFields ?? new List<string>()).ToHashSet(StringComparer.Ordinal);
            List<CustomBlock> blocks = new List<CustomBlock>();
            // An item has one set of pictures: a second gallery (from a file made elsewhere) is kept in the file, not shown.
            LayoutBlock? gallery = layout.Blocks.FirstOrDefault(b => b.Kind == BlockKinds.Gallery);
            foreach (LayoutBlock block in layout.Blocks.Where(b => BlockKinds.Known(b.Kind) && (b.Kind != BlockKinds.Gallery || b == gallery)))
            {
                blocks.Add(block.Kind switch
                {
                    BlockKinds.Fields => new CustomBlock(block, FieldsSection(block, onCard), null),
                    BlockKinds.Notes => new CustomBlock(block, NotesSection(block), null),
                    BlockKinds.Checklist => new CustomBlock(block, null, Checklist(block)),
                    BlockKinds.Documents => new CustomBlock(block, null, Documents(block)),
                    BlockKinds.Log => new CustomBlock(block, null, Log(block)),
                    BlockKinds.Quantities => new CustomBlock(block, null, Quantities(block)),
                    _ => new CustomBlock(block, null, null)
                });
            }
            Blocks = blocks;
            Tabs = new[]
            {
                new TabDefinition
                {
                    Key = "Custom",
                    Name = "",
                    Sections = blocks.Select(b => b.Section).OfType<SectionDefinition>().ToArray(),
                    Records = blocks.Select(b => b.Records).OfType<RecordDefinition>().ToArray()
                }
            };
            Dictionary<string, FieldDefinition> fields = Tabs[0].Sections.SelectMany(s => s.Fields).GroupBy(f => f.Key).ToDictionary(g => g.Key, g => g.First());
            CardFields = (layout.CardFields ?? new List<string>()).Where(fields.ContainsKey).Select(id => fields[id]).ToList();
        }

        public CustomBlock? Block(string id) => Blocks.FirstOrDefault(b => b.Id == id);

        // ---- The blocks' definitions -----------------------------------------------------------------

        // A field the user named; a kind this version doesn't know is left out (and kept in the file).
        private static FieldDefinition? Field(LayoutField field, bool inForm)
        {
            if (FieldKinds.ValueKindOf(field.Kind) is not ValueKind kind)
                return null;
            string? unit = kind == ValueKind.Number && !string.IsNullOrWhiteSpace(field.Unit) ? Units.Own(field.Unit.Trim()) : null;
            return new FieldDefinition
            {
                Key = field.Id,
                Name = Named(field.Name, "Custom.Field.Untitled"),
                Kind = kind,
                Choices = kind == ValueKind.Choice ? (field.Choices ?? new List<string>()).ToArray() : Array.Empty<string>(),
                ChoiceLabel = value => value, // the user's own words
                Unit = unit,
                InForm = inForm
            };
        }

        private static SectionDefinition FieldsSection(LayoutBlock block, HashSet<string> onCard) => new SectionDefinition
        {
            Key = block.Id,
            Name = Named(block.Name, "Custom.Block.Fields"),
            Fields = (block.Fields ?? new List<LayoutField>()).Select(f => Field(f, onCard.Contains(f.Id))).OfType<FieldDefinition>().ToArray()
        };

        // Its text in the item's Details under the block's Id; the field has the block's name, so it shows bare under it.
        private static SectionDefinition NotesSection(LayoutBlock block)
        {
            string name = Named(block.Name, "Custom.Block.Notes");
            return new SectionDefinition
            {
                Key = block.Id,
                Name = name,
                Fields = new[] { new FieldDefinition { Key = block.Id, Name = name, Kind = ValueKind.LongText } }
            };
        }

        private static RecordDefinition Checklist(LayoutBlock block) => new RecordDefinition
        {
            Key = block.Id,
            Name = Named(block.Name, "Custom.Block.Checklist"),
            TextKey = "CustomChecklist",
            Fields = new[]
            {
                new FieldDefinition { Key = TextKey, Kind = ValueKind.Text },
                new FieldDefinition { Key = DoneKey, Kind = ValueKind.Flag }
            },
            TitleKeys = new[] { TextKey },
            Title = line => Or(line.Get(TextKey), "Entity.Records.CustomChecklist.Untitled")
        };

        // As a vehicle's documents, without their fixed types: a name, a number, the date it runs out on and when to be
        // reminded of it (VehicleReminders), a file, notes.
        private static RecordDefinition Documents(LayoutBlock block) => new RecordDefinition
        {
            Key = block.Id,
            Name = Named(block.Name, "Custom.Block.Documents"),
            TextKey = "CustomDocuments",
            Fields = new[]
            {
                new FieldDefinition { Key = NameKey, Kind = ValueKind.Text },
                new FieldDefinition { Key = NumberKey, Kind = ValueKind.Text },
                new FieldDefinition { Key = ValidUntilKey, Kind = ValueKind.Date, IsExpiry = true },
                new FieldDefinition
                {
                    Key = RemindKey, Kind = ValueKind.Choice,
                    Choices = VehicleSchema.RemindChoices.Select(d => d.ToString(CultureInfo.InvariantCulture)).ToArray(),
                    ChoiceLabel = days => Values.ToInt(days) is int n ? Loc.Plural("Entity.Remind.DaysBefore", n) : Loc.T("Entity.Remind.None")
                },
                new FieldDefinition { Key = FileKey, Kind = ValueKind.File },
                new FieldDefinition { Key = NotesKey, Kind = ValueKind.LongText }
            },
            TitleKeys = new[] { NameKey },
            Title = DocumentTitle,
            Subtitle = (document, _) => document.Get(NumberKey) ?? "",
            Figures = new[] { ValidUntilKey },
            Icon = _ => "", // Document
            Start = (document, _) => document.Set(RemindKey, "30"),
            DueKey = ValidUntilKey,
            RemindKey = RemindKey,
            DeadlineName = DocumentTitle,
            ReminderText = (document, date) => Loc.F("Entity.Reminder.Message", DocumentTitle(document), date.ToString(Loc.T("Format.LongDate"), Loc.Culture))
        };

        private static string DocumentTitle(EntityRecord document) => Or(document.Get(NameKey), "Entity.Records.CustomDocuments.Untitled");

        // Dated entries, newest first, each with what happened and an amount: money (added up per year and in all, as a
        // vehicle's costs) or a number in the block's unit.
        private static RecordDefinition Log(LayoutBlock block)
        {
            string? unit = block.IsMoney ? Units.Money : string.IsNullOrWhiteSpace(block.Unit) ? null : Units.Own(block.Unit.Trim());
            return new RecordDefinition
            {
                Key = block.Id,
                Name = Named(block.Name, "Custom.Block.Log"),
                TextKey = "CustomLog",
                Fields = new[]
                {
                    new FieldDefinition { Key = DateKey, Kind = ValueKind.Date },
                    new FieldDefinition { Key = EntryKey, Kind = ValueKind.LongText },
                    new FieldDefinition { Key = AmountKey, Kind = ValueKind.Number, Unit = unit }
                },
                TitleKeys = new[] { DateKey },
                Title = entry => Values.ParseDate(entry.Get(DateKey)) is DateTime date
                    ? date.ToString(Loc.T("Format.ShortDate"), Loc.Culture) : Loc.T("Entity.Records.CustomLog.Untitled"),
                Subtitle = (entry, _) => entry.Get(EntryKey)?.Split('\n')[0].Trim() ?? "",
                Figures = new[] { AmountKey },
                Start = (entry, _) => entry.Set(DateKey, Values.FromDate(DateTime.Today)),
                NewestFirstKey = DateKey,
                TotalKey = block.IsMoney ? AmountKey : null
            };
        }

        // Short lines of what there is how much of, all on view as a table with - and + beside the amount (CompactRecords).
        private static RecordDefinition Quantities(LayoutBlock block) => new RecordDefinition
        {
            Key = block.Id,
            Name = Named(block.Name, "Custom.Block.Quantities"),
            TextKey = "CustomQuantities",
            Fields = new[]
            {
                new FieldDefinition { Key = NameKey, Kind = ValueKind.Text },
                new FieldDefinition { Key = QuantityKey, Kind = ValueKind.Number },
                new FieldDefinition { Key = UnitKey, Kind = ValueKind.Text }
            },
            TitleKeys = new[] { NameKey },
            Title = line => Or(line.Get(NameKey), "Entity.Records.CustomQuantities.Untitled"),
            Compact = true,
            StepKey = QuantityKey,
            Start = (line, _) => line.Set(QuantityKey, "1")
        };

        private static string Named(string name, string untitledKey) => string.IsNullOrWhiteSpace(name) ? Loc.T(untitledKey) : name.Trim();

        private static string Or(string? text, string untitledKey) => string.IsNullOrWhiteSpace(text) ? Loc.T(untitledKey) : text.Trim();

        // ---- What the blocks hold, as text ------------------------------------------------------------

        // A checklist's lines, the open ones first (TaskOrder), each with whether it's done.
        public static List<(EntityRecord Line, bool Done)> ChecklistLines(RecordDefinition list, Entity item) =>
            TaskOrder.OpenFirst(list.Read(item).Select(r => (Line: r, Done: !string.IsNullOrEmpty(r.Get(DoneKey)))), x => x.Done);

        // The blocks that hold something, in the layout's order, as label and text in the current language (exports,
        // search): a Fields block's filled fields one by one, a Notes block's text, a list's lines (a checklist's open ones
        // first, then the done ones after "Done:"; a log newest first). The item's own notes (from elsewhere) last.
        public List<(string Label, string Value)> Describe(Entity item)
        {
            List<(string, string)> lines = new List<(string, string)>();
            foreach (CustomBlock block in Blocks)
            {
                if (block.Section != null)
                {
                    foreach (FieldDefinition field in block.Section.Fields)
                    {
                        string shown = field.Show(field.Read(item), item);
                        if (shown.Length > 0)
                            lines.Add((field.Label, shown));
                    }
                    continue;
                }
                if (block.Records is not RecordDefinition records)
                    continue;
                if (block.Kind == BlockKinds.Checklist)
                {
                    List<(EntityRecord Line, bool Done)> checks = ChecklistLines(records, item);
                    List<string> text = checks.Where(c => !c.Done).Select(c => records.Title(c.Line)).ToList();
                    List<string> done = checks.Where(c => c.Done).Select(c => records.Title(c.Line)).ToList();
                    if (done.Count > 0)
                        text.Add(Loc.F("Custom.Checklist.DoneLine", string.Join(", ", done)));
                    if (text.Count > 0)
                        lines.Add((records.Label, string.Join("\n", text)));
                    continue;
                }
                List<EntityRecord> list = records.Read(item);
                if (records.NewestFirstKey != null)
                    list = list.OrderByDescending(r => Values.ParseDate(r.Get(records.NewestFirstKey)) ?? DateTime.MinValue).ToList();
                if (list.Count > 0)
                    lines.Add((records.Label, string.Join("\n", list.Select(r => records.Line(r, item)))));
            }
            if (!string.IsNullOrWhiteSpace(item.Notes))
                lines.Add((Loc.T("Entity.Field.Notes"), item.Notes));
            return lines;
        }
    }
}
