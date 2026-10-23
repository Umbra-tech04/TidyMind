using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TidyMind;

// Harness custom <data> <shots>: custom collections. M1: the layout file (round trip, fields of other versions and
// unknown kinds kept, what a file from elsewhere may hold cleaned), the definitions made from it (sections, lists,
// cached until saved), item data in Details and Records, an unreadable layout leaving the page read-only, creating a
// custom collection by choosing Custom in the item picker (New Collection asks only a name), a layout change undone and redone. M2: the item window draws every block
// (half blocks side by side, stacked when narrow), edits in place, the checklist (ticks, order, add, delete, undo), the
// quantities stepper with decimals, the gallery in place of the pictures on top, nothing cut off at three widths; the
// card's key fields and due line; the item form asking for the card fields.
static partial class Program
{
    static async Task CustomScenarios(MainWindow main)
    {
        await Wait(1200);
        GuidedTour.Current?.Stop(TourEnd.Skipped);
        main.Topmost = true;
        main.Width = 1240;
        main.Height = 820;
        StartSelectWatcher();
        await Foreground(main);

        try
        {
            await CustomLayoutFile(main);
            await CustomDefinitions(main);
            await CustomUnreadable(main);
            await CustomCreate(main);
            await CustomLayoutUndo(main);
            await CustomWindow(main);
            await CustomCardsAndForm(main);
            await CustomBuilder(main);
            await CustomReviewFixes(main);
            await CustomIntegration(main);
        }
        finally
        {
            Console.WriteLine("message boxes: " + selectBoxes.Count);
            lock (selectBoxes)
                foreach (string box in selectBoxes)
                    Console.WriteLine("  " + box);
        }
    }

    // ---- Helpers ------------------------------------------------------------------------------------

    // A layout with one block of every kind, a field of every kind, and what a newer version may add: a block and a
    // field of kinds this one doesn't know, and fields of its own on the layout, a block and a field.
    static CustomLayout SampleLayout(out Dictionary<string, string> ids)
    {
        Dictionary<string, string> made = new Dictionary<string, string>();
        ids = made;
        string Id(string name) => made[name] = CustomLayout.NewId();
        JsonElement Future(string text) => JsonDocument.Parse("\"" + text + "\"").RootElement.Clone();
        return new CustomLayout
        {
            Blocks = new List<LayoutBlock>
            {
                new LayoutBlock
                {
                    Id = Id("fields"), Kind = BlockKinds.Fields, Name = "Specs",
                    Fields = new List<LayoutField>
                    {
                        new LayoutField { Id = Id("brand"), Name = "Brand", Kind = FieldKinds.Text },
                        new LayoutField { Id = Id("weight"), Name = "Weight", Kind = FieldKinds.Number, Unit = "kg" },
                        new LayoutField { Id = Id("bought"), Name = "Bought", Kind = FieldKinds.Date },
                        new LayoutField { Id = Id("boxed"), Name = "Boxed", Kind = FieldKinds.Flag },
                        new LayoutField { Id = Id("colour"), Name = "Colour", Kind = FieldKinds.Choice, Choices = new List<string> { "Red", "Blue" } },
                        new LayoutField { Id = Id("rating"), Name = "Rating", Kind = "Stars", ExtensionData = new Dictionary<string, JsonElement> { ["Max"] = Future("5") } }
                    },
                    ExtensionData = new Dictionary<string, JsonElement> { ["Collapsed"] = Future("yes") }
                },
                new LayoutBlock { Id = Id("gallery"), Kind = BlockKinds.Gallery, Name = "Photos", Half = true },
                new LayoutBlock { Id = Id("notes"), Kind = BlockKinds.Notes, Name = "Remarks", Half = true },
                new LayoutBlock { Id = Id("checklist"), Kind = BlockKinds.Checklist, Name = "Packing" },
                new LayoutBlock { Id = Id("documents"), Kind = BlockKinds.Documents, Name = "Papers" },
                new LayoutBlock { Id = Id("log"), Kind = BlockKinds.Log, Name = "Repairs" },
                new LayoutBlock { Id = Id("quantities"), Kind = BlockKinds.Quantities, Name = "Spares" },
                new LayoutBlock { Id = Id("timeline"), Kind = "Timeline", Name = "Later", ExtensionData = new Dictionary<string, JsonElement> { ["Scale"] = Future("week") } }
            },
            CardFields = new List<string> { made["brand"], made["weight"] },
            ExtensionData = new Dictionary<string, JsonElement> { ["Theme"] = Future("dark") }
        };
    }

    // An item of the layout with something in every block.
    static Entity SampleItem(Profile memory, Dictionary<string, string> ids, string name)
    {
        Entity item = new Entity { Id = Guid.NewGuid(), EntityType = CustomTypes.TypeOf(memory.Id), Name = name, CreatedDate = DateTime.Now };
        item.Details = new Dictionary<string, string>
        {
            [ids["brand"]] = "Acme", [ids["weight"]] = "2.5", [ids["bought"]] = "2025-04-01", [ids["boxed"]] = "true", [ids["colour"]] = "Blue",
            [ids["rating"]] = "4", [ids["notes"]] = "Keep dry"
        };
        EntityRecord Line(params (string Key, string Value)[] values)
        {
            EntityRecord record = new EntityRecord { Id = Guid.NewGuid() };
            foreach ((string key, string value) in values)
                record.Set(key, value);
            return record;
        }
        item.Records = new Dictionary<string, List<EntityRecord>>
        {
            [ids["checklist"]] = new List<EntityRecord> { Line(("Text", "Tent"), ("Done", "true")), Line(("Text", "Stove")) },
            [ids["documents"]] = new List<EntityRecord> { Line(("Name", "Warranty"), ("Number", "W-1"), ("ValidUntil", DateTime.Today.AddDays(20).ToString("yyyy-MM-dd")), ("RemindDays", "7")) },
            [ids["log"]] = new List<EntityRecord> { Line(("Date", "2025-05-01"), ("Entry", "New zip"), ("Amount", "12.5")), Line(("Date", "2025-08-01"), ("Entry", "Seam sealed"), ("Amount", "4")) },
            [ids["quantities"]] = new List<EntityRecord> { Line(("Name", "Pegs"), ("Quantity", "12"), ("Unit", "pcs")) },
            ["future-list"] = new List<EntityRecord> { Line(("X", "kept")) }
        };
        return item;
    }

    static string Json(object o) => JsonSerializer.Serialize(o);

    // ---- The layout file ------------------------------------------------------------------------------

    static async Task CustomLayoutFile(MainWindow main)
    {
        Console.WriteLine("Layout file: round trip, other versions' fields and kinds kept, cleaned");
        Profile memory = AddMemory(main, "Gear", ProfileType.Collection);
        CustomLayout layout = SampleLayout(out Dictionary<string, string> ids);
        Check(CustomLayoutStore.TrySave(memory, layout), "saved");
        string written = File.ReadAllText(MemoryFiles.Layout(memory));
        Check(CustomLayoutStore.TryLoad(memory, out CustomLayout read, out _) && read != null, "read back");
        Check(Json(read) == written, "read and written again: the same JSON");
        JsonObject file = JsonNode.Parse(written).AsObject();
        Check(file["Theme"]?.ToString() == "dark", "a field of the layout's from another version kept");
        Check(read.Blocks.Count == 8 && read.Blocks[7].Kind == "Timeline" && read.Blocks[7].ExtensionData?.ContainsKey("Scale") == true,
            "a block of a kind this version doesn't know kept, with its own fields");
        Check(read.Blocks[0].Fields.Count == 6 && read.Blocks[0].Fields[5].Kind == "Stars" && read.Blocks[0].Fields[5].ExtensionData?.ContainsKey("Max") == true,
            "a field of a kind this version doesn't know kept");
        Check(read.Blocks[0].ExtensionData?.ContainsKey("Collapsed") == true, "a block's field from another version kept");
        Check(!written.Contains("\"Half\":false") && written.Contains("\"Half\":true") && !written.Contains("\"Money\""), "defaults left out of the file");

        // What a file from elsewhere may hold: nulls, blocks without an Id or sharing one, a second gallery, card fields
        // that are no field or too many.
        string messy = "{\"Blocks\":[null,{\"Id\":\"a\",\"Kind\":\"Gallery\",\"Name\":\"One\"},{\"Id\":\"a\",\"Kind\":\"Notes\",\"Name\":\"Same Id\"},"
                       + "{\"Id\":\"\",\"Kind\":\"Notes\"},{\"Id\":\"b\",\"Kind\":\"Gallery\",\"Name\":\"Two\"},"
                       + "{\"Id\":\"c\",\"Kind\":\"Fields\",\"Name\":null,\"Fields\":[null,{\"Id\":\"f1\",\"Name\":\"A\",\"Kind\":\"Text\"},{\"Id\":\"f1\",\"Name\":\"Twin\",\"Kind\":\"Text\"},"
                       + "{\"Id\":\"f2\",\"Name\":\"B\",\"Kind\":\"Text\"},{\"Id\":\"f3\",\"Name\":\"C\",\"Kind\":\"Text\"},{\"Id\":\"f4\",\"Name\":\"D\",\"Kind\":\"Choice\",\"Choices\":[\"x\",\"\",\"x\",null]}]}],"
                       + "\"CardFields\":[\"nope\",\"f1\",\"f1\",\"f2\",\"f3\",\"f4\"]}";
        Profile other = AddMemory(main, "Messy", ProfileType.Collection);
        File.WriteAllText(MemoryFiles.Layout(other), messy);
        Check(CustomLayoutStore.TryLoad(other, out CustomLayout clean, out _) && clean != null, "a messy layout is read");
        Check(clean.Blocks.Select(b => b.Id).SequenceEqual(new[] { "a", "b", "c" }), "nulls, a block without an Id and one sharing an Id dropped");
        Check(clean.Blocks[1].Kind == BlockKinds.Gallery && CustomTypes.OfMemory(other).Blocks.Count(b => b.Kind == BlockKinds.Gallery) == 1,
            "a second gallery is kept as written, only the first is shown");
        Check(clean.Blocks[2].Name == "" && clean.Blocks[2].Fields.Select(f => f.Id).SequenceEqual(new[] { "f1", "f2", "f3", "f4" }), "a field sharing an Id dropped");
        Check(clean.Blocks[2].Fields[3].Choices.SequenceEqual(new[] { "x" }), "empty and repeated choices dropped");
        Check(clean.CardFields.SequenceEqual(new[] { "f1", "f2", "f3" }), "card fields: only fields, once each, at most three");
        Check(CustomTypes.OfMemory(other).Blocks.Single(b => b.Kind == BlockKinds.Fields).Section.Label == Loc.T("Custom.Block.Fields"), "a block without a name is called by its kind");
        Check(File.ReadAllText(MemoryFiles.Layout(other)) == messy, "reading changed nothing on disk");

        File.WriteAllText(MemoryFiles.Layout(other), "");
        CustomTypes.ForgetAll();
        Check(!CustomLayoutStore.TryLoad(other, out _, out string problem) && problem.Length > 0, "an empty file can't be read, and says why");
        Check(CustomTypes.OfMemory(other) == null && EntityTypes.Tabs(CustomTypes.TypeOf(other.Id)).Count == 0,
            "its items have no definitions (shown as a type this version doesn't know)");
        RemoveMemory(main, other);
        await Task.CompletedTask;
    }

    // ---- The definitions --------------------------------------------------------------------------------

    static async Task CustomDefinitions(MainWindow main)
    {
        Console.WriteLine("Definitions: made from the layout, items' data in Details and Records");
        Profile memory = Profiles(main).First(p => p.Name == "Gear");
        CustomLayoutStore.TryLoad(memory, out CustomLayout layout, out _);
        Dictionary<string, string> ids = layout.Blocks.Where(b => b.Kind != "Timeline").ToDictionary(b => b.Kind, b => b.Id);
        Dictionary<string, string> fieldIds = layout.Blocks[0].Fields.ToDictionary(f => f.Name, f => f.Id);
        string type = CustomTypes.TypeOf(memory.Id);
        CustomType custom = CustomTypes.Of(type);
        Check(custom != null && custom.Blocks.Count == 7, "every known block, the unknown one left out");
        Check(EntityTypes.Tabs(type).Count == 1, "one page");
        Check(EntityTypes.Sections(type).Select(s => s.Label).SequenceEqual(new[] { "Specs", "Remarks" }), "Fields and Notes blocks are sections, named as the user named them");
        Check(EntityTypes.Records(type).Select(r => r.Label).SequenceEqual(new[] { "Packing", "Papers", "Repairs", "Spares" }), "the lists, named as the user named them");
        Check(EntityTypes.Fields(type).Select(f => f.Label).SequenceEqual(new[] { "Brand", "Weight", "Bought", "Boxed", "Colour", "Remarks" }),
            "every field of a known kind, the notes text last");
        Check(custom.CardFields.Select(f => f.Label).SequenceEqual(new[] { "Brand", "Weight" }), "the card's fields");
        Check(EntityTypes.Fields(type).Where(f => f.InForm).Select(f => f.Label).SequenceEqual(new[] { "Brand", "Weight" }), "the card's fields are asked for in the item form");
        Check(ReferenceEquals(CustomTypes.Of(type), custom), "made once, then kept");

        Dictionary<string, string> sampleIds = new Dictionary<string, string>
        {
            ["brand"] = fieldIds["Brand"], ["weight"] = fieldIds["Weight"], ["bought"] = fieldIds["Bought"], ["boxed"] = fieldIds["Boxed"],
            ["colour"] = fieldIds["Colour"], ["rating"] = fieldIds["Rating"], ["notes"] = ids[BlockKinds.Notes], ["checklist"] = ids[BlockKinds.Checklist],
            ["documents"] = ids[BlockKinds.Documents], ["log"] = ids[BlockKinds.Log], ["quantities"] = ids[BlockKinds.Quantities]
        };
        Entity item = SampleItem(memory, sampleIds, "Tent");
        string json = Json(item);
        Check(Json(JsonSerializer.Deserialize<Entity>(json)) == json, "an item round-trips byte for byte");
        Check(!json.Contains("MoreImages") && !json.Contains("VehicleKind"), "nothing of other types written");

        List<(string Label, string Value)> lines = EntityTypes.Describe(item);
        Check(lines.Select(l => l.Label).SequenceEqual(new[] { "Brand", "Weight", "Bought", "Boxed", "Colour", "Remarks", "Packing", "Papers", "Repairs", "Spares" }),
            "described in the layout's order: " + string.Join(", ", lines.Select(l => l.Label)));
        string Line(string label) => lines.First(l => l.Label == label).Value;
        Check(Line("Weight") == 2.5.ToString("#,0.###", Loc.Culture) + " kg","a number with the user's unit (no break before it): " + Line("Weight"));
        Check(Line("Boxed") == Loc.T("Entity.Flag.Yes") && Line("Colour") == "Blue", "a flag and a choice as shown");
        Check(Line("Packing") == "Stove\n" + Loc.F("Custom.Checklist.DoneLine", "Tent"), "a checklist: open first, the done ones after: " + Line("Packing").Replace("\n", " / "));
        Check(Line("Repairs").StartsWith(new DateTime(2025, 8, 1).ToString(Loc.T("Format.ShortDate"), Loc.Culture)), "a log newest first");
        Check(Line("Spares").StartsWith("Pegs"), "quantities: " + Line("Spares").Replace("\n", " / "));
        string search = EntityTypes.SearchText(item);
        Check(search.StartsWith("Tent") && search.Contains("Acme") && search.Contains("Keep dry") && search.Contains("Warranty") && search.Contains("Seam sealed"),
            "everything it holds is searchable");

        Deadline due = EntityTypes.NearestExpiry(item);
        Check(due != null && due.Name == "Warranty" && due.RemindDays == 7, "a document's date comes due, with its reminder days");

        // Saved again: the definitions follow.
        layout.Blocks[0].Name = "Specifications";
        Check(CustomLayoutStore.TrySave(memory, layout), "the layout saved again");
        Check(!ReferenceEquals(CustomTypes.Of(type), custom) && EntityTypes.Sections(type).First().Label == "Specifications", "the definitions made again from it");

        // An item whose collection's layout is gone: what a type this version doesn't know shows.
        Entity orphan = new Entity { Id = Guid.NewGuid(), EntityType = CustomTypes.TypeOf(Guid.NewGuid()), Name = "Lost" };
        Check(EntityTypes.Tabs(orphan.EntityType).Count == 0 && EntityTypes.SearchText(orphan) == null && EntityTypes.Expiries(orphan).Count == 0,
            "an item of a layout that is gone: no definitions");
        Check(!CustomTypes.IsCustom(EntityTypes.Vehicle) && CustomTypes.Of("Custom:not-a-guid") == null, "other types aren't custom");
        await Task.CompletedTask;
    }

    // ---- An unreadable layout -----------------------------------------------------------------------

    static async Task CustomUnreadable(MainWindow main)
    {
        Console.WriteLine("Unreadable layout: the page is read-only, nothing written");
        Profile memory = AddMemory(main, "Broken layout", ProfileType.Collection);
        File.WriteAllText(MemoryFiles.Items(memory), Json(new List<Entity> { new Entity { Id = Guid.NewGuid(), Name = "Kept", EntityType = CustomTypes.TypeOf(memory.Id) } }));
        File.WriteAllText(MemoryFiles.Layout(memory), "{ not json");
        CustomTypes.ForgetAll();
        Dictionary<string, string> before = FolderSnapshot();
        CollectionView view = OpenCollectionView(main, memory);
        await Wait(400);
        TextBlock empty = (TextBlock)view.FindName("EmptyState");
        Check(empty.Visibility == Visibility.Visible && empty.Text.Contains("layout.json"), "the page says the layout can't be read: " + empty.Text);
        Check(((Panel)view.FindName("EntityPanel")).Children.Count == 0, "no cards to change");
        Panel toolbar = (Panel)view.FindName("Toolbar");
        Check(toolbar.Children.OfType<Button>().Where(b => b.Visibility == Visibility.Visible).All(b => b.Name == "MoreButton"), "only the memory's menu is left in the toolbar");
        Invoke(main, "NavigateHome");
        await Wait(300);
        Dictionary<string, string> after = FolderSnapshot();
        Check(SameSnapshot(before, after), "nothing written" + SnapshotDiff(before, after));
        RemoveMemory(main, memory);
    }

    // ---- Creating one ------------------------------------------------------------------------------

    static async Task CustomCreate(MainWindow main)
    {
        Console.WriteLine("New Collection is as it was; Custom is a choice in the item picker");
        int count = Profiles(main).Count;
        AnswerPrompt(main, "Bird book");
        Invoke(main, "AddProfile", ProfileType.Collection);
        await Wait(500);
        Profile birds = Profiles(main).FirstOrDefault(p => p.Name == "Bird book");
        Check(Profiles(main).Count == count + 1 && birds != null && !File.Exists(MemoryFiles.Layout(birds)),
            "New Collection asks only for a name: an ordinary collection, no second question");
        Check(!Application.Current.Windows.OfType<EntityTypeSelector>().Any(), "no picker opens with it");
        CollectionView view = (CollectionView)Content(main);

        // The picker: three choices, Custom enabled in an empty collection; closing it changes nothing.
        AnswerPicker(main, async picker =>
        {
            Button custom = (Button)picker.FindName("CustomButton");
            List<string> texts = Descendants(picker).OfType<TextBlock>().Select(t => t.Text).ToList();
            Check(new[] { Loc.T("Entity.Type.Clothing"), Loc.T("Entity.Type.Vehicle"), Loc.T("Entity.Type.Custom") }.All(texts.Contains), "Clothing, Vehicle and Custom, side by side");
            Check(texts.Contains(Loc.T("Entity.Type.Custom.Text")) && custom.IsEnabled, "Custom with its line, enabled in an empty collection");
            Check(texts.Contains(Loc.T("Entity.Picker.Soon")), "and the \"more entities coming soon\" line under them");
            List<string> cut = CutOff(picker).Concat(Clipped(picker)).ToList();
            Check(cut.Count == 0, "nothing cut off" + (cut.Count > 0 ? ": " + string.Join("; ", cut) : ""));
            RenderWindow(picker, "custom-picker");
            picker.Close();
            await Task.CompletedTask;
        });
        Invoke(view, "AddEntityButton_Click", null, null);
        await Wait(300);
        Check(!File.Exists(MemoryFiles.Layout(birds)), "closing the picker adds no layout");

        // Custom: the layout is written and the collection's empty page opens, Customize on; what is added there is the
        // page every item gets. The collection is custom from then on.
        AnswerPicker(main, async picker =>
        {
            ClickNow((Button)picker.FindName("CustomButton"));
            await Task.CompletedTask;
        });
        DriveTitled(main, "Bird book", async w =>
        {
            Check(Customizing(w) && Field<ItemWindow.Host>(w, "host").Preview, "choosing Custom opens the collection's empty page, customizing");
            Check(CustomizeSwitch(w)?.IsChecked == true && ((TextBox)w.FindName("NameText")).Text == Loc.T("Custom.Template.ItemName"),
                "the item's name at the top, the switch on");
            Check(GearLayout(birds).Blocks.Count == 0 && PageTexts(w).Contains(Loc.T("Custom.Layout.Start")) && ButtonNamed(w, Loc.T("Custom.Layout.Add")) != null,
                "a blank page: what it is for, and + Add");
            Check(!Descendants(w).OfType<Button>().Any(b => (b.Content as string) == Loc.T("Common.Delete") || (b.Content as string) == Loc.T("Vehicle.AddPictures")),
                "no item actions on the empty page");
            w.Width = 760;
            await Wait(300);
            RenderWindow(w, "custom-empty");

            // + Add > Fields: on the page at once, the keyboard in its add box; a name and Enter add a text field.
            ContextMenu Menu() => (ContextMenu)typeof(ItemWindow).GetMethod("AddMenu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .Invoke(w, new object[] { new Button(), CustomTypes.OfMemory(birds) });
            void Add(string kind) => Menu().Items.OfType<MenuItem>().First(i => System.Windows.Automation.AutomationProperties.GetName(i) == Loc.T("Custom.Kind." + kind))
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Add(BlockKinds.Fields);
            await Wait(400);
            LayoutBlock fields = GearLayout(birds).Blocks.Single();
            Check(fields.Kind == BlockKinds.Fields && fields.Name == Loc.T("Custom.Block.Fields") && !fields.Half && fields.Fields.Count == 0,
                "a Fields block, full width, named by its kind, no question asked");
            TextBox addField = LayoutTarget(w, fields.Id, "+");
            Check(FocusManager.GetFocusedElement(w) == addField, "the keyboard in its Add a field box");
            TypeAndEnter(addField, "Wingspan");
            await Wait(400);
            Check(GearLayout(birds).Blocks.Single().Fields.Single() is LayoutField wing && wing.Name == "Wingspan" && wing.Kind == FieldKinds.Text, "a text field, by its name");
            PickKind(w, "Wingspan", FieldKinds.Number);
            await Wait(500);
            Check(GearLayout(birds).Blocks.Single().Fields.Single().Kind == FieldKinds.Number
                  && Descendants(PageOf(w)).OfType<TextBox>().Any(t => System.Windows.Automation.AutomationProperties.GetName(t) == Loc.T("Custom.Field.Unit")),
                "its kind from the dropdown: a number, with a unit beside it");
            Add(BlockKinds.Gallery);
            Add(BlockKinds.Notes);
            await Wait(400);
            Check(GearLayout(birds).Blocks.Select(b => b.Kind).SequenceEqual(new[] { BlockKinds.Fields, BlockKinds.Gallery, BlockKinds.Notes }), "pictures and notes after it");
            Check(!Menu().Items.OfType<MenuItem>().First(i => System.Windows.Automation.AutomationProperties.GetName(i) == Loc.T("Custom.Kind.Gallery")).IsEnabled, "one gallery");
            List<string> cut = CutOff(w).Concat(Clipped(w)).ToList();
            Check(cut.Count == 0, "nothing cut off" + (cut.Count > 0 ? ": " + string.Join("; ", cut.Take(4)) : ""));

            // What an item will hold is shown, never used: the empty page is no item.
            Check(Descendants(PageOf(w)).OfType<Button>().Where(b => (b.Content as string) == Loc.T("Entity.Card.Fill")).All(b => !b.IsHitTestVisible && !b.Focusable),
                "the notes' Fill in is shown and takes no click");
            Click(CustomizeSwitch(w));
            await Wait(300);
            Check(!Customizing(w) && Sections(w).Count == 2 && !Descendants(PageOf(w)).OfType<Button>().Any(b => System.Windows.Automation.AutomationProperties.GetName(b) == Loc.T("Custom.Layout.Add")), "off: the page an item will have");
            Check(Descendants(PageOf(w)).OfType<Control>().All(c => !c.Focusable), "and nothing in it to type into");
            RenderWindow(w, "custom-empty-off");
            Click(CustomizeSwitch(w));
            await Wait(300);

            // Its own Ctrl+Z; Esc closes it.
            Check(UndoHost.Of(w).Undo() && GearLayout(birds).Blocks.Count == 2 && w.IsVisible && Customizing(w), "Ctrl+Z takes the last block back");
            Check(UndoHost.Of(w).Redo() && GearLayout(birds).Blocks.Count == 3, "and redo");
            PressKey(w, Key.Escape);
            await Wait(300);
            Check(!w.IsVisible, "Esc closes the empty page");
        });
        Invoke(view, "AddEntityButton_Click", null, null); // returns once the page is closed
        await Wait(600);
        Check(CustomLayout.Starter().Blocks.Count == 0, "a new layout starts empty");
        Check(CustomLayoutStore.TryLoad(birds, out CustomLayout starter, out _) && starter?.Blocks.Count == 3, "the collection has the layout built on its page");
        Check(Content(main) is CollectionView, "its page is shown");
        ContextMenu sidebar = NavItem(main, birds).ContextMenu;
        Check(sidebar.Items.OfType<MenuItem>().Any(i => (string)i.Header == Loc.T("Custom.Menu.Customize")), "its sidebar menu has Customize layout");

        // Now Add makes an item of the layout: no picker.
        DriveEntityForm(main, form =>
        {
            Check(!Application.Current.Windows.OfType<EntityTypeSelector>().Any(), "a custom collection's Add goes straight to the item form");
            Field<TextBox>(form, "nameBox").Text = "Sparrow";
            return Task.CompletedTask;
        });
        DriveTitled(main, "Sparrow", w => Task.CompletedTask);
        Invoke(view, "AddEntityButton_Click", null, null);
        await Wait(500);
        Entity sparrow = EntityStore.Load(birds).FirstOrDefault(e => e.Name == "Sparrow");
        Check(sparrow != null && sparrow.EntityType == CustomTypes.TypeOf(birds.Id), "an item of the custom layout");

        // A collection that already has items: Custom isn't offered, nothing in it is turned into something else.
        Profile shoes = AddMemory(main, "Shoe box", ProfileType.Collection);
        File.WriteAllText(MemoryFiles.Items(shoes), JsonSerializer.Serialize(new List<Entity> { new Entity { Id = Guid.NewGuid(), EntityType = EntityTypes.Clothing, Name = "Boot" } }));
        CollectionView shoeView = OpenCollectionView(main, shoes);
        await Wait(400);
        string itemsBefore = File.ReadAllText(MemoryFiles.Items(shoes)); // after the page's own first-open tidying
        AnswerPicker(main, async picker =>
        {
            Button custom = (Button)picker.FindName("CustomButton");
            Check(!custom.IsEnabled && Descendants(picker).OfType<TextBlock>().Any(t => t.Text == Loc.T("Entity.Type.Custom.NeedsEmpty")),
                "with items in the collection Custom is disabled, and says why");
            Check(((Button)Descendants(picker).OfType<Button>().First(b => (string)b.Tag == "Clothing")).IsEnabled, "Clothing and Vehicle stay");
            picker.Close();
            await Task.CompletedTask;
        });
        Invoke(shoeView, "AddEntityButton_Click", null, null);
        await Wait(300);
        Check(!File.Exists(MemoryFiles.Layout(shoes)) && File.ReadAllText(MemoryFiles.Items(shoes)) == itemsBefore, "no layout made, its items untouched");
        RemoveMemory(main, shoes);
        Invoke(main, "NavigateHome");
    }

    // Drives the next item picker (EntityTypeSelector) that opens.
    static void AnswerPicker(MainWindow main, Func<EntityTypeSelector, Task> act)
    {
        _ = main.Dispatcher.BeginInvoke(new Action(async () =>
        {
            EntityTypeSelector picker = null;
            for (int i = 0; i < 80 && picker == null; i++)
            {
                await Wait(100);
                picker = Application.Current.Windows.OfType<EntityTypeSelector>().FirstOrDefault();
            }
            if (picker == null)
            {
                Check(false, "the item picker opened");
                return;
            }
            try
            {
                await Wait(200);
                await act(picker);
            }
            catch (Exception e)
            {
                Check(false, "the picker driver threw: " + e);
                picker.Close();
            }
        }), DispatcherPriority.Background);
    }

    // ---- Undo --------------------------------------------------------------------------------------------

    static async Task CustomLayoutUndo(MainWindow main)
    {
        Console.WriteLine("A layout change undone and redone");
        Profile memory = Profiles(main).First(p => p.Name == "Gear");
        OpenCollectionView(main, memory);
        await Wait(300);
        string file = MemoryFiles.Layout(memory);
        string before = File.ReadAllText(file);
        using (UndoChange change = UndoChange.Begin(UndoService.Page, UndoParts.Layout(memory)))
        {
            CustomLayout layout = CustomTypes.OfMemory(memory).Layout.Copy();
            layout.Blocks.Reverse();
            CustomLayoutStore.TrySave(memory, layout);
            change.Commit("Layout");
        }
        string after = File.ReadAllText(file);
        Check(after != before && EntityTypes.Records(CustomTypes.TypeOf(memory.Id)).First().Label == "Spares", "changed");
        Check(main.Undo.Undo() && File.ReadAllText(file) == before, "undone: the file byte for byte");
        Check(EntityTypes.Records(CustomTypes.TypeOf(memory.Id)).First().Label == "Packing", "the definitions follow the undo");
        Check(main.Undo.Redo() && File.ReadAllText(file) == after, "redone");
        Check(main.Undo.Undo() && File.ReadAllText(file) == before, "undone again");
        await Task.CompletedTask;
    }
}
