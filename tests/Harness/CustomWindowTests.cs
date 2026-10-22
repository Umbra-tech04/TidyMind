using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using TidyMind;

// Harness custom, M2: a custom item in the shared item window, its card, and the item form.
static partial class Program
{
    // The "Gear" collection's layout ids, by kind and by field name (as CustomDefinitions made them).
    static Dictionary<string, string> GearIds(Profile memory)
    {
        CustomLayoutStore.TryLoad(memory, out CustomLayout layout, out _);
        Dictionary<string, string> ids = layout.Blocks.Where(b => BlockKinds.Known(b.Kind)).ToDictionary(b => b.Kind, b => b.Id);
        foreach (LayoutField field in layout.Blocks.First(b => b.Kind == BlockKinds.Fields).Fields)
            ids[field.Name] = field.Id;
        return new Dictionary<string, string>
        {
            ["brand"] = ids["Brand"], ["weight"] = ids["Weight"], ["bought"] = ids["Bought"], ["boxed"] = ids["Boxed"], ["colour"] = ids["Colour"],
            ["rating"] = ids["Rating"], ["fields"] = ids[BlockKinds.Fields], ["gallery"] = ids[BlockKinds.Gallery], ["notes"] = ids[BlockKinds.Notes],
            ["checklist"] = ids[BlockKinds.Checklist], ["documents"] = ids[BlockKinds.Documents], ["log"] = ids[BlockKinds.Log], ["quantities"] = ids[BlockKinds.Quantities]
        };
    }

    static Entity StoredItem(Profile memory, Guid id) => EntityStore.Load(memory).First(e => e.Id == id);

    static List<EntityRecord> StoredLines(Profile memory, Guid id, string block) =>
        StoredItem(memory, id).Records?.GetValueOrDefault(block) ?? new List<EntityRecord>();

    static ChecklistSection Checklist(ItemWindow w) => Descendants(PageOf(w)).OfType<ChecklistSection>().Single();

    // ---- The item window --------------------------------------------------------------------------------

    static async Task CustomWindow(MainWindow main)
    {
        Console.WriteLine("The item window: every block, in place");
        Profile memory = Profiles(main).First(p => p.Name == "Gear");
        Dictionary<string, string> ids = GearIds(memory);
        Entity tent = SampleItem(memory, ids, "Tent");
        File.WriteAllText(MemoryFiles.Items(memory), JsonSerializer.Serialize(new List<Entity> { tent }));
        CollectionView view = OpenCollectionView(main, memory);
        await Wait(400);

        await WithVehicle(main, view, tent.Id, async w =>
        {
            w.Width = 1100;
            await Wait(400);
            List<FrameworkElement> blocks = Descendants(PageOf(w)).OfType<FrameworkElement>()
                .Where(e => e is FieldsSection || e is ChecklistSection || e is CompactRecords || e is PictureBrowser || e is RecordRow).ToList();
            Check(Sections(w).Select(s => s.Definition.Label).SequenceEqual(new[] { "Specifications", "Remarks" }), "the Fields and Notes blocks as sections");
            Check(Descendants(PageOf(w)).OfType<ChecklistSection>().Count() == 1 && Descendants(PageOf(w)).OfType<CompactRecords>().Count() == 1,
                "the checklist and the quantities table");
            Check(Rows(w).Count == 3, "a row per document and log entry: " + Rows(w).Count);
            Check(PageTexts(w).Contains("Packing") && PageTexts(w).Contains("Papers") && PageTexts(w).Contains("Repairs") && PageTexts(w).Contains("Spares"),
                "every block under its own name");
            Check(Field<ContentControl>(w, "Hero").Visibility == Visibility.Collapsed, "no pictures on top: the layout has a gallery block");
            Check(PageTexts(w).Contains(Loc.T("Custom.Gallery.Empty")), "the empty gallery says so");
            Check(Field<FrameworkElement>(w, "Nav").Visibility == Visibility.Collapsed, "one page: no page list");
            Check(!PageTexts(w).Contains("Later"), "a block of a kind this version doesn't know isn't shown");

            // Half blocks: side by side when wide, one under the other when narrow.
            HalfRow half = Descendants(PageOf(w)).OfType<HalfRow>().Single();
            UIElement remarks = Sections(w).Single(s => s.Definition.Label == "Remarks");
            double X(UIElement e) => e.TransformToAncestor(w).Transform(new Point(0, 0)).X;
            double Y(UIElement e) => e.TransformToAncestor(w).Transform(new Point(0, 0)).Y;
            UIElement galleryBlock = (UIElement)VisualTreeHelper.GetChild(half, 0);
            Check(X(remarks) > X(galleryBlock) + 200 && Math.Abs(Y(remarks) - Y(galleryBlock)) < 1, "two half blocks side by side at 1100 px");
            TextBlock Head(string text) => Descendants(PageOf(w)).OfType<TextBlock>().First(t => t.Text == text);
            Check(Math.Abs(Y(Head("Photos")) - Y(Head("Remarks"))) < 0.5, "an empty one's heading in line with its filled neighbour's");
            w.Width = 640;
            await Wait(400);
            Check(Math.Abs(X(remarks) - X(galleryBlock)) < 1 && Y(remarks) > Y(galleryBlock), "one under the other at 640 px");
            foreach (double width in new[] { 560.0, 760, 1100 })
            {
                w.Width = width;
                await Wait(350);
                List<string> cut = CutOff(w).Concat(Clipped(w)).ToList();
                Check(cut.Count == 0, "nothing cut off at " + width + " px" + (cut.Count > 0 ? ": " + string.Join("; ", cut.Take(4)) : ""));
            }
            w.Width = 1100;
            await Wait(300);
            RenderWindow(w, "custom-item");

            // A Fields block edited in place, as every type's sections.
            FieldsSection specs = SectionTitled(w, "Specifications");
            specs.BeginEdit();
            await Wait(200);
            List<FieldEditor> editors = Field<List<FieldEditor>>(specs, "editors");
            Check(editors.Select(e => e.Field.Label).SequenceEqual(new[] { "Brand", "Weight", "Bought", "Boxed", "Colour" }), "an input per field");
            (Descendants(editors[0].Element).OfType<TextBox>().FirstOrDefault() ?? (TextBox)editors[0].Element).Text = "Hilleberg";
            TextBox weight = editors[1].Element as TextBox ?? Descendants(editors[1].Element).OfType<TextBox>().First();
            weight.Text = "3,25";
            ClickNow(ButtonNamed(specs, Loc.T("Common.Save")));
            await Wait(400);
            Entity stored = StoredItem(memory, tent.Id);
            Check(stored.Details[ids["brand"]] == "Hilleberg" && stored.Details[ids["weight"]] == "3.25", "saved as typed, the number invariant: "
                + stored.Details[ids["weight"]]);
            Check(stored.Details[ids["rating"]] == "4", "the value of a field this version doesn't know kept");
            Check(stored.Records.ContainsKey("future-list"), "a list this version doesn't know kept");
        });

        await WithVehicle(main, view, tent.Id, async w =>
        {
            Console.WriteLine("The checklist");
            string block = ids["checklist"];
            List<string> Shown() => Descendants(Checklist(w)).OfType<CheckBox>().Select(c => System.Windows.Automation.AutomationProperties.GetName(c)).ToList();
            Check(Shown().SequenceEqual(new[] { "Stove", "Tent" }), "the open line above the done one: " + string.Join(", ", Shown()));
            CheckBox stove = Descendants(Checklist(w)).OfType<CheckBox>().First();
            stove.Focus();
            stove.IsChecked = true;
            stove.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, stove));
            await Wait(400);
            Check(StoredLines(memory, tent.Id, block).Select(r => r.Get("Text") + "=" + r.Get("Done")).SequenceEqual(new[] { "Tent=true", "Stove=true" }),
                "ticked and saved, the stored order untouched");
            Check(Shown().SequenceEqual(new[] { "Tent", "Stove" }), "both done: their stored order");
            Check(Keyboard.FocusedElement is CheckBox focused && System.Windows.Automation.AutomationProperties.GetName(focused) == "Stove",
                "the keyboard back on the same tick");
            CheckBox tentBox = Descendants(Checklist(w)).OfType<CheckBox>().First();
            tentBox.IsChecked = false;
            tentBox.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, tentBox));
            await Wait(400);
            Check(Shown().SequenceEqual(new[] { "Tent", "Stove" }) && StoredLines(memory, tent.Id, block)[0].Get("Done") == null, "unticked: back among the open ones");

            TextBox input = Descendants(Checklist(w)).OfType<TextBox>().Last();
            input.Focus();
            input.Text = "Lamp";
            input.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(input), 0, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent });
            await Wait(400);
            Check(StoredLines(memory, tent.Id, block).Select(r => r.Get("Text")).SequenceEqual(new[] { "Tent", "Stove", "Lamp" }), "a line added at the end");
            Check(Shown().SequenceEqual(new[] { "Tent", "Lamp", "Stove" }), "shown among the open ones");
            Check(Keyboard.FocusedElement is TextBox box && box == Descendants(Checklist(w)).OfType<TextBox>().Last() && box.Text == "",
                "the add box keeps the keyboard, empty");

            Button delete = Descendants(Checklist(w)).OfType<Button>().First(b => System.Windows.Automation.AutomationProperties.GetName(b) == Loc.T("Common.Delete"));
            ClickNow(delete);
            await Wait(400);
            Check(StoredLines(memory, tent.Id, block).Select(r => r.Get("Text")).SequenceEqual(new[] { "Stove", "Lamp" }), "a line deleted without a question");
            Check(UndoHost.Of(w).Undo(), "its delete undone with the window's own Ctrl+Z");
            await Wait(400);
            Check(StoredLines(memory, tent.Id, block).Select(r => r.Get("Text")).SequenceEqual(new[] { "Tent", "Stove", "Lamp" }), "back in its place");
            Check(Shown().Contains("Tent"), "and in the window");

            Console.WriteLine("Quantities: the stepper keeps decimals");
            CompactRecords table = Descendants(PageOf(w)).OfType<CompactRecords>().Single();
            ClickNow(Descendants(table).OfType<Button>().First(b => System.Windows.Automation.AutomationProperties.GetName(b) == Loc.T("Entity.Records.CustomQuantities.More")));
            await Wait(400);
            Check(StoredLines(memory, tent.Id, ids["quantities"])[0].Get("Quantity") == "13", "one more: 13");
        });

        // An amount with decimals steps by one and keeps them.
        Entity edited = StoredItem(memory, tent.Id);
        edited.Records[ids["quantities"]][0].Set("Quantity", "2.5");
        List<Entity> all = EntityStore.Load(memory);
        all[all.FindIndex(e => e.Id == tent.Id)] = edited;
        EntityStore.TrySave(memory, all);
        view.ReloadAfterUndo();
        await WithVehicle(main, view, tent.Id, async w =>
        {
            CompactRecords table = Descendants(PageOf(w)).OfType<CompactRecords>().Single();
            Check(Descendants(table).OfType<TextBlock>().Any(t => t.Text == 2.5.ToString("0.###", Loc.Culture)), "2.5 shown");
            ClickNow(Descendants(table).OfType<Button>().First(b => System.Windows.Automation.AutomationProperties.GetName(b) == Loc.T("Entity.Records.CustomQuantities.More")));
            await Wait(400);
            Check(StoredLines(memory, tent.Id, ids["quantities"])[0].Get("Quantity") == "3.5", "one more: 3.5");

            Console.WriteLine("A document and a log entry through their dialogs");
            RecordDefinition documents = EntityTypes.Records(tent.EntityType).First(r => r.Key == ids["documents"]);
            await AddRecord(main, w, documents, async dialog =>
            {
                List<FieldEditor> fields = dialog.Editors.ToList();
                (Descendants(fields[0].Element).OfType<TextBox>().FirstOrDefault() ?? (TextBox)fields[0].Element).Text = "Insurance";
                (Descendants(fields[2].Element).OfType<DatePicker>().FirstOrDefault() ?? (DatePicker)fields[2].Element).SelectedDate = DateTime.Today.AddDays(60);
                await Wait(100);
            });
            Check(StoredLines(memory, tent.Id, ids["documents"]).Any(r => r.Get("Name") == "Insurance" && r.Get("RemindDays") == "30"),
                "a document added, reminded 30 days before by default");
            RecordDefinition log = EntityTypes.Records(tent.EntityType).First(r => r.Key == ids["log"]);
            await AddRecord(main, w, log, async dialog =>
            {
                List<FieldEditor> fields = dialog.Editors.ToList();
                (Descendants(fields[1].Element).OfType<TextBox>().FirstOrDefault() ?? (TextBox)fields[1].Element).Text = "Pole replaced";
                (Descendants(fields[2].Element).OfType<TextBox>().FirstOrDefault() ?? (TextBox)fields[2].Element).Text = "20";
                await Wait(100);
            });
            List<EntityRecord> entries = StoredLines(memory, tent.Id, ids["log"]);
            Check(entries.Count == 3 && entries[2].Get("Date") == DateTime.Today.ToString("yyyy-MM-dd") && entries[2].Get("Amount") == "20",
                "a log entry dated today by default");
            Check(PageTexts(w).Contains(Loc.T("Entity.Costs.AllTime")), "a money log adds up its amounts");
        });

        // Pictures: the gallery block shows them; Left and Right browse them.
        Entity withPictures = StoredItem(memory, tent.Id);
        EntityImages.SetNames(withPictures, new[] { StoredPicture(1), StoredPicture(2) });
        all = EntityStore.Load(memory);
        all[all.FindIndex(e => e.Id == tent.Id)] = withPictures;
        EntityStore.TrySave(memory, all);
        view.ReloadAfterUndo();
        await WithVehicle(main, view, tent.Id, async w =>
        {
            PictureBrowser browser = Descendants(PageOf(w)).OfType<PictureBrowser>().SingleOrDefault();
            Check(browser != null && Field<ContentControl>(w, "Hero").Visibility == Visibility.Collapsed, "the pictures in the gallery block, none on top");
            Check(browser?.Selected == 0, "the cover first");
            w.Focus();
            Keyboard.Focus(w);
            w.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(w), 0, Key.Right) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            await Wait(300);
            Check(Descendants(PageOf(w)).OfType<PictureBrowser>().Single().Selected == 1, "Right shows the next picture");
            RenderWindow(w, "custom-item-pictures");
        });
    }

    // ---- The card and the item form ------------------------------------------------------------------

    static async Task CustomCardsAndForm(MainWindow main)
    {
        Console.WriteLine("The card: cover, name, the layout's card fields, what comes due");
        Profile memory = Profiles(main).First(p => p.Name == "Gear");
        Dictionary<string, string> ids = GearIds(memory);
        CollectionView view = OpenCollectionView(main, memory);
        await Wait(500);
        Panel cards = (Panel)view.FindName("EntityPanel");
        Grid card = cards.Children.OfType<Grid>().First();
        List<string> runs = Descendants(card).OfType<TextBlock>().SelectMany(t => t.Inlines.OfType<Run>().Select(r => r.Text.Trim())).ToList();
        Check(runs.Contains("Brand") && runs.Contains("Hilleberg") && runs.Contains("Weight"), "the card fields under the name: " + string.Join(" | ", runs));
        Check(Descendants(card).OfType<TextBlock>().Any(t => t.Text.StartsWith("Warranty")), "the document that comes due first");
        Check(Descendants(card).OfType<Image>().Any(), "its cover");
        List<string> cut = Clipped(card).ToList();
        Check(cut.Count == 0, "nothing on the card cut without an ellipsis" + (cut.Count > 0 ? ": " + string.Join("; ", cut) : ""));
        Shot(main, "custom-cards");

        Console.WriteLine("Add: no type to pick, the form asks for the card fields");
        DriveEntityForm(main, async form =>
        {
            await Wait(300);
            List<FieldEditor> editors = Field<List<FieldEditor>>(form, "fieldEditors");
            Check(editors.Select(e => e.Field.Label).SequenceEqual(new[] { "Brand", "Weight" }), "the card fields: " + string.Join(", ", editors.Select(e => e.Field.Label)));
            Check(Field<TextBox>(form, "notesBox") == null, "no notes box: the layout has its own notes");
            Field<TextBox>(form, "nameBox").Text = "Stove";
            (Descendants(editors[0].Element).OfType<TextBox>().FirstOrDefault() ?? (TextBox)editors[0].Element).Text = "Primus";
        });
        DriveItem(main, async w =>
        {
            Check(w.Title == "Stove", "the new item opens in its window");
            await Task.CompletedTask;
        });
        ClickNow(Descendants(view).OfType<Button>().First(b => b.Content is TextBlock t && t.Inlines.OfType<Run>().Any(r => r.Text == Loc.T("Collection.AddItem"))));
        await Wait(800);
        Entity stove = EntityStore.Load(memory).FirstOrDefault(e => e.Name == "Stove");
        Check(stove != null && stove.EntityType == CustomTypes.TypeOf(memory.Id) && stove.Details?[ids["brand"]] == "Primus",
            "a custom item of this collection, its card field saved");
        Check(Application.Current.Windows.OfType<EntityTypeSelector>().Count() == 0, "no type picker");
    }
}
