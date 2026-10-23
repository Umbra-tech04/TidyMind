using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ClosedXML.Excel;
using TidyMind;

// Harness custom, mixed collections: Add item always shows the picker (an empty collection, one with clothing, with
// vehicles, with custom items, a mixed one); Custom added to a clothing collection and to a vehicle collection (the first
// page, the layout made, the items already there untouched; with a layout, a new item of it); clothing and a vehicle added
// to a custom collection; in a collection of all three kinds: cards, search, a layout change touching only the custom
// items, PDF/Word/Excel, reminders, Home and the Day Plan, a collection package and single items (a custom item into a
// collection without a layout, a vehicle into one with it), selecting and deleting several at once, undo, backup.
static partial class Program
{
    static async Task CustomMixed(MainWindow main)
    {
        Console.WriteLine("Mixed collections: the picker is always shown, the kinds side by side");
        UndoService.EndAll();
        try
        {
            await MixedClothingAndVehicle(main);
            await MixedCustomCollection(main);
            await MixedOperations(main);
            await MixedImportIntoPlain(main);
        }
        finally
        {
            foreach (Reminder reminder in ReminderManager.LoadReminders())
                ReminderManager.DeleteReminder(reminder.Id);
            foreach (Profile left in Profiles(main).Where(p => p.Name.StartsWith("Mixed ")).ToList())
                RemoveMemory(main, left);
        }
    }

    // ---- Helpers ----------------------------------------------------------------------------------------

    static Func<EntityTypeSelector, Task> ClickKind(string tag) => async picker =>
    {
        ClickNow(Descendants(picker).OfType<Button>().First(b => (string)b.Tag == tag));
        await Task.CompletedTask;
    };

    // Add item, the picker answered with `kind`, the form given `name`, the new item's window driven by `inWindow` (then closed).
    static async Task AddVia(MainWindow main, CollectionView view, string kind, string name, Func<ItemWindow, Task> inWindow = null)
    {
        AnswerPicker(main, ClickKind(kind));
        DriveEntityForm(main, form =>
        {
            Field<TextBox>(form, "nameBox").Text = name;
            return Task.CompletedTask;
        });
        DriveTitled(main, name, inWindow ?? (w => Task.CompletedTask));
        Invoke(view, "AddEntityButton_Click", null, null);
        await Wait(600);
    }

    // Add item with the picker closed again at once: it was shown, with the three kinds and the coming-soon line.
    static async Task PickerShows(MainWindow main, CollectionView view, string what)
    {
        bool shown = false;
        AnswerPicker(main, async picker =>
        {
            shown = true;
            Check(new[] { "Clothing", "Vehicle", "Custom" }.All(tag => Descendants(picker).OfType<Button>().Any(b => (string)b.Tag == tag && b.IsEnabled)),
                what + ": Clothing, Vehicle and Custom, all enabled");
            Check(Descendants(picker).OfType<TextBlock>().Any(t => t.Text == Loc.T("Entity.Picker.Soon")), what + ": and the coming-soon line");
            picker.Close();
            await Task.CompletedTask;
        });
        Invoke(view, "AddEntityButton_Click", null, null);
        await Wait(200);
        Check(shown, what + ": Add item shows the picker");
    }

    static string ItemsText(Profile memory) => File.ReadAllText(MemoryFiles.Items(memory));

    static string Serialized(Profile memory, string name) => JsonSerializer.Serialize(EntityStore.Load(memory).Single(e => e.Name == name));

    // ---- Custom added to a clothing collection and to a vehicle collection ---------------------------------

    static async Task MixedClothingAndVehicle(MainWindow main)
    {
        Console.WriteLine("Custom in a clothing collection and in a vehicle collection");
        Profile empty = AddMemory(main, "Mixed empty", ProfileType.Collection);
        CollectionView emptyView = OpenCollectionView(main, empty);
        await Wait(300);
        await PickerShows(main, emptyView, "an empty collection");

        // Clothing first, then Custom: the first page, the layout made, the garment untouched.
        Profile wardrobe = AddMemory(main, "Mixed wardrobe", ProfileType.Collection);
        Entity jacket = Garment("Jacket");
        jacket.Brand = "Zed";
        EntityStore.TrySave(wardrobe, new List<Entity> { jacket });
        CollectionView view = OpenCollectionView(main, wardrobe);
        await Wait(400);
        await PickerShows(main, view, "a clothing collection");
        string before = ItemsText(wardrobe);
        AnswerPicker(main, ClickKind("Custom"));
        DriveTitled(main, "Mixed wardrobe", async w =>
        {
            Check(Customizing(w) && Field<ItemWindow.Host>(w, "host").Preview, "Custom in a clothing collection: its first page opens, customizing");
            await Task.CompletedTask;
        });
        Invoke(view, "AddEntityButton_Click", null, null);
        await Wait(500);
        Check(File.Exists(MemoryFiles.Layout(wardrobe)) && ItemsText(wardrobe) == before, "its layout made, the jacket untouched, no custom item stored yet");
        Check(NavItem(main, wardrobe).ContextMenu.Items.OfType<MenuItem>().Any(i => (string)i.Header == Loc.T("Custom.Menu.Customize")),
            "its sidebar menu has Customize layout");

        // With a layout built, Custom is a new item of it, Customize off; the picker is still shown.
        CustomLayoutStore.TrySave(wardrobe, SampleLayout(out _));
        string layoutText = File.ReadAllText(MemoryFiles.Layout(wardrobe));
        await AddVia(main, view, "Custom", "Tent", async w =>
        {
            Check(!Customizing(w) && !Field<ItemWindow.Host>(w, "host").Preview && CustomizeSwitch(w) != null, "a new custom item: Customize off, the switch there");
            await Task.CompletedTask;
        });
        List<Entity> items = EntityStore.Load(wardrobe);
        Check(items.Count == 2 && items.Single(e => e.Name == "Tent").EntityType == CustomTypes.TypeOf(wardrobe.Id)
              && items.Single(e => e.Name == "Jacket").EntityType == EntityTypes.Clothing, "a custom item and a garment in one collection, each its own type");
        Check(File.ReadAllText(MemoryFiles.Layout(wardrobe)) == layoutText, "adding the item left the layout as it was");
        await PickerShows(main, view, "a collection with clothing and a custom item");
        await AddVia(main, view, "Clothing", "Scarf");
        Check(EntityStore.Load(wardrobe).Single(e => e.Name == "Scarf").EntityType == EntityTypes.Clothing, "and clothing is added beside it");

        // A vehicle collection.
        Profile garage = AddMemory(main, "Mixed garage", ProfileType.Collection);
        Entity golf = Vehicle("Golf");
        EntityRecord insurance = Document("Insurance", DateTime.Today.AddDays(50), 30, "Allianz");
        VehicleSchema.Documents.Write(golf, new List<EntityRecord> { insurance });
        EntityStore.TrySave(garage, new List<Entity> { golf });
        VehicleReminders.Follow(null, golf);
        view = OpenCollectionView(main, garage);
        await Wait(400);
        await PickerShows(main, view, "a vehicle collection");
        before = ItemsText(garage);
        AnswerPicker(main, ClickKind("Custom"));
        DriveTitled(main, "Mixed garage", async w =>
        {
            Check(Customizing(w) && Field<ItemWindow.Host>(w, "host").Preview, "Custom in a vehicle collection: its first page opens, customizing");
            await Task.CompletedTask;
        });
        Invoke(view, "AddEntityButton_Click", null, null);
        await Wait(500);
        Check(File.Exists(MemoryFiles.Layout(garage)) && ItemsText(garage) == before && ReminderOf(insurance.Id) != null,
            "its layout made, the car and its reminder untouched");
        CustomLayoutStore.TrySave(garage, SampleLayout(out _));
        await AddVia(main, view, "Custom", "Hammock");
        await AddVia(main, view, "Vehicle", "Moped");
        items = EntityStore.Load(garage);
        Check(items.Select(e => e.EntityType).OrderBy(t => t).SequenceEqual(new[] { CustomTypes.TypeOf(garage.Id), EntityTypes.Vehicle, EntityTypes.Vehicle }.OrderBy(t => t)),
            "a car, a moped and a hammock in one collection: " + string.Join(", ", items.Select(e => e.Name + "=" + e.EntityType)));
        Check(items.Single(e => e.Name == "Golf").Id == golf.Id && ReminderOf(insurance.Id) != null, "the car is the same item, its reminder still there");

        Invoke(main, "NavigateHome");
        RemoveMemory(main, empty);
        RemoveMemory(main, wardrobe);
        RemoveMemory(main, garage);
    }

    // ---- Clothing and a vehicle added to a custom collection -------------------------------------------------

    static async Task MixedCustomCollection(MainWindow main)
    {
        Console.WriteLine("Clothing and a vehicle added to a custom collection");
        Profile camping = AddMemory(main, "Mixed camping", ProfileType.Collection);
        CustomLayoutStore.TrySave(camping, SampleLayout(out _));
        Dictionary<string, string> ids = GearIds(camping);
        Entity tent = SampleItem(camping, ids, "Camp tent");
        EntityStore.TrySave(camping, new List<Entity> { tent });
        CollectionView view = OpenCollectionView(main, camping);
        await Wait(400);
        string layoutText = File.ReadAllText(MemoryFiles.Layout(camping));
        await PickerShows(main, view, "a custom collection");
        string tentBefore = Serialized(camping, "Camp tent"); // after the page's own first-open tidying

        await AddVia(main, view, "Clothing", "Boots");
        await AddVia(main, view, "Vehicle", "Van");
        List<Entity> items = EntityStore.Load(camping);
        Check(items.Count == 3 && items.Single(e => e.Name == "Boots").EntityType == EntityTypes.Clothing
              && items.Single(e => e.Name == "Van").EntityType == EntityTypes.Vehicle
              && items.Single(e => e.Name == "Camp tent").EntityType == CustomTypes.TypeOf(camping.Id), "clothing and a vehicle added to a custom collection, each its own type");
        Check(File.ReadAllText(MemoryFiles.Layout(camping)) == layoutText && Serialized(camping, "Camp tent") == tentBefore,
            "the layout and the custom item are as they were");

        // The garment and the car open in their own windows (no custom blocks, no Customize switch).
        foreach (string name in new[] { "Boots", "Van" })
            await WithVehicle(main, view, items.Single(e => e.Name == name).Id, async w =>
            {
                Check(CustomizeSwitch(w) == null && Custom(w) == null, name + ": its own window, no Customize switch");
                await Task.CompletedTask;
            });

        // Some data on the others, for the next steps.
        Entity van = items.Single(e => e.Name == "Van");
        VehicleSchema.Documents.Write(van, new List<EntityRecord> { Document("Insurance", DateTime.Today.AddDays(40), 30, "Allianz") });
        Entity boots = items.Single(e => e.Name == "Boots");
        boots.Brand = "Walker";
        boots.Details ??= new Dictionary<string, string>();
        boots.Details[ids["notes"]] = "stray"; // a key the layout also uses: the layout never touches a garment's data
        EntityStore.TrySave(camping, items);
        VehicleReminders.Follow(null, van);
        VehicleReminders.Follow(null, items.Single(e => e.Name == "Camp tent"));
        view.ReloadAfterUndo();
    }

    static CustomType Custom(ItemWindow w) => CustomTypes.Of(Field<Entity>(w, "item").EntityType);

    // ---- Everything else, in a collection of all three kinds -------------------------------------------------

    static async Task MixedOperations(MainWindow main)
    {
        Console.WriteLine("A collection of clothing, a vehicle and a custom item");
        Profile camping = Profiles(main).First(p => p.Name == "Mixed camping");
        Dictionary<string, string> ids = GearIds(camping);
        CollectionView view = OpenCollectionView(main, camping);
        await Wait(400);
        List<Entity> items = EntityStore.Load(camping);
        Guid tentId = items.Single(e => e.Name == "Camp tent").Id, vanId = items.Single(e => e.Name == "Van").Id;

        // Cards.
        Panel cards = (Panel)view.FindName("EntityPanel");
        Grid CardOf(string name) => cards.Children.OfType<Grid>().First(c => Descendants(c).OfType<TextBlock>().Any(t => t.Text == name));
        List<string> TextsOf(string name) => Descendants(CardOf(name)).OfType<TextBlock>().SelectMany(t => t.Inlines.OfType<System.Windows.Documents.Run>().Select(r => r.Text.Trim()).Append(t.Text)).ToList();
        Check(cards.Children.Count == 3, "three cards");
        Check(TextsOf("Camp tent").Contains("Acme") && TextsOf("Camp tent").Any(t => t.StartsWith("Warranty")), "the custom card: its card fields and what comes due");
        Check(TextsOf("Van").Any(t => t.StartsWith("Allianz")), "the vehicle card: what comes due (the document, by its name)");
        Check(!TextsOf("Boots").Any(t => t.Contains("Acme") || t.Contains("Allianz") || t.Contains("Warranty")), "the garment's card has none of those");

        // Search.
        List<SearchResult> index = SearchIndexBuilder.Build(Profiles(main));
        bool Finds(string query, string title) => SearchIndexBuilder.Filter(index, query, 20).Any(r => r.Kind == SearchResultKind.Entity && r.Title == title && r.Profile.Id == camping.Id);
        Check(Finds("Seam sealed", "Camp tent") && Finds("Acme", "Camp tent") && Finds("Boots", "Boots") && Finds("Allianz", "Van"), "global search finds each kind by what it holds");
        Check(!Finds("Seam sealed", "Boots") && !Finds("Allianz", "Camp tent"), "and not by what another holds");

        // A layout change reaches the custom items only: the layout's notes block removed (asked first, one step), Ctrl+Z.
        string bootsBefore = Serialized(camping, "Boots"), vanBefore = Serialized(camping, "Van");
        await WithVehicle(main, view, tentId, async w =>
        {
            Invoke(w, "Customize", true);
            Invoke(w, "RemoveBlock", ids["notes"]);
            await Wait(400);
            Check(StoredItem(camping, tentId).Details?.ContainsKey(ids["notes"]) != true, "the custom item lost the removed block's text");
            Check(Serialized(camping, "Boots") == bootsBefore && Serialized(camping, "Van") == vanBefore, "the garment (even with a key the layout uses) and the vehicle are untouched");
            Check(UndoHost.Of(w).Undo(), "undone");
            await Wait(400);
            Check(StoredItem(camping, tentId).Details?[ids["notes"]] == "Keep dry" && Serialized(camping, "Boots") == bootsBefore && Serialized(camping, "Van") == vanBefore,
                "the text is back, the others as they were");
        });

        // Exports.
        string xlsx = Path.Combine(customWork, "mixed.xlsx"), docx = Path.Combine(customWork, "mixed.docx"), pdf = Path.Combine(customWork, "mixed.pdf");
        ExportService.ExportMemory(camping, xlsx, ExportFormat.Excel);
        using (XLWorkbook workbook = new XLWorkbook(xlsx))
        {
            List<string> sheets = workbook.Worksheets.Select(s => s.Name).ToList();
            List<string> cells = workbook.Worksheets.SelectMany(s => s.CellsUsed()).Select(c => c.GetString()).ToList();
            Check(new[] { "Camp tent", "Boots", "Van", "Acme", "Walker", "Allianz" }.All(cells.Contains), "Excel: every item and what it holds: " + string.Join(" | ", cells.Take(30)));
            Check(sheets.Any(s => s.StartsWith("Papers")) && sheets.Any(s => s.StartsWith(VehicleSchema.Documents.Label)), "Excel: a sheet for the custom documents and one for the vehicle's: " + string.Join(", ", sheets));
        }
        ExportService.ExportMemory(camping, docx, ExportFormat.Docx);
        string text = DocxText(docx);
        Check(new[] { "Camp tent", "Boots", "Van", "Acme", "Seam sealed", "Walker", "Allianz" }.All(text.Contains), "Word: every item and what it holds");
        ExportService.ExportMemory(camping, pdf, ExportFormat.Pdf);
        Check(File.Exists(pdf) && new FileInfo(pdf).Length > 1000, "PDF written");
        ExportService.ExportAll(Profiles(main), Path.Combine(customWork, "mixed-all.docx"), ExportFormat.Docx);
        Check(DocxText(Path.Combine(customWork, "mixed-all.docx")).Contains("Allianz"), "Export all has it too");

        // Reminders, Home, the Day Plan.
        Guid vanDoc = EntityFiles.Records(StoredItem(camping, vanId)).Single().Id, tentDoc = EntityFiles.Records(StoredItem(camping, tentId)).First(r => r.Get("Name") == "Warranty").Id;
        Check(ReminderOf(vanDoc)?.Title == "Van" && ReminderOf(tentDoc)?.Title == "Camp tent", "the vehicle's and the custom item's documents each have their reminder");
        DashboardData home = DashboardDataAggregator.Build(Profiles(main), DateTime.Now);
        Check(home.Expiries.Any(x => x.ItemName == "Van") && home.Expiries.Any(x => x.ItemName == "Camp tent"), "Home lists what comes due for both");
        List<CustomDates.ItemDate> planned = CustomDates.On(Profiles(main), DateTime.Today.AddDays(20)).Where(d => d.Memory.Id == camping.Id).ToList();
        Check(planned.Count == 1 && planned[0].Item.Name == "Camp tent", "the Day Plan lists the custom item's date (and nothing of the others)");

        // A collection package: the layout, every type kept, only the custom items re-pointed.
        string package = Path.Combine(customWork, "mixed" + TransferPackage.Extension);
        CustomLayoutStore.TryLoad(camping, out CustomLayout layout, out _);
        TransferPackage.ExportCollection(camping, new List<MemoryTab>(), EntityStore.Load(camping), null, 0, package, null, CancellationToken.None, layout);
        OpenedPackage opened = TransferPackage.Open(package);
        Check(opened.Manifest.Format == 5 && opened.Layout != null, "the collection package is format 5, with the layout");
        Profile copy;
        using (StagedImport staged = TransferPackage.Stage(opened, null, CancellationToken.None))
        {
            copy = TransferPackage.CommitCollection(staged, Profiles(main), "#3498DB");
            foreach (Entity added in staged.Added)
                VehicleReminders.Follow(null, added);
        }
        Invoke(main, "BuildNav");
        List<Entity> copied = EntityStore.Load(copy);
        Check(copied.Count == 3 && copied.Single(e => e.Name == "Camp tent").EntityType == CustomTypes.TypeOf(copy.Id)
              && copied.Single(e => e.Name == "Boots").EntityType == EntityTypes.Clothing && copied.Single(e => e.Name == "Van").EntityType == EntityTypes.Vehicle,
            "the copy: each item its type, the custom one of the new collection");
        Check(copied.Single(e => e.Name == "Boots").Details?[ids["notes"]] == "stray" && copied.Single(e => e.Name == "Boots").Brand == "Walker", "the garment as it was");
        EntityRecord copiedDoc = EntityFiles.Records(copied.Single(e => e.Name == "Van")).Single();
        Check(copiedDoc.Id != vanDoc && ReminderOf(copiedDoc.Id)?.Title == "Van", "the copy's vehicle document has a new Id and its own reminder");
        Check(EntityTypes.Describe(copied.Single(e => e.Name == "Camp tent")).Any(l => l.Value.Contains("Seam sealed")), "the copied custom item shows everything");

        // Single items: a vehicle into this collection (its layout stays as it is), a garment too.
        string layoutText = File.ReadAllText(MemoryFiles.Layout(camping));
        string vanPackage = Path.Combine(customWork, "van" + TransferPackage.Extension);
        TransferPackage.ExportItem(EntityStore.Load(camping).Single(e => e.Name == "Van"), vanPackage, null, CancellationToken.None, null);
        using (StagedImport staged = TransferPackage.Stage(TransferPackage.Open(vanPackage), null, CancellationToken.None))
        {
            TransferPackage.CommitItem(staged, camping, Profiles(main), "#3498DB", "Imported");
            foreach (Entity added in staged.Added)
                VehicleReminders.Follow(null, added);
        }
        Entity vanTwo = EntityStore.Load(camping).Single(e => e.Name == "Van (2)");
        Check(vanTwo.EntityType == EntityTypes.Vehicle && File.ReadAllText(MemoryFiles.Layout(camping)) == layoutText, "a vehicle into a collection with a layout: still a vehicle, the layout untouched");
        Check(ReminderOf(EntityFiles.Records(vanTwo).Single().Id) != null, "with its own document reminder");

        // Several at once, one question, one undo; their reminders go and come back.
        UndoService.EndAll();
        view = OpenCollectionView(main, camping);
        await Wait(400);
        items = EntityStore.Load(camping);
        string start = ItemsText(camping);
        List<Guid> reminders = items.SelectMany(EntityFiles.Records).Select(r => r.Id).Where(id => ReminderOf(id) != null).ToList();
        Check(items.Count == 4 && reminders.Count == 3, "four items and three document reminders before");
        bool deleted = (bool)typeof(CollectionView).GetMethod("DeleteEntities", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Invoke(view, new object[] { (IReadOnlyList<Entity>)items });
        await Wait(400);
        Check(deleted && EntityStore.Load(camping).Count == 0 && reminders.All(id => ReminderOf(id) == null), "all four deleted at once, their reminders gone");
        Check(main.Undo.Undo() && ItemsText(camping) == start, "Ctrl+Z: back byte for byte");
        await Wait(300);
        Check(reminders.All(id => ReminderOf(id) != null), "with their reminders");
        Check(EntityStore.Load(camping).Select(e => e.EntityType).Distinct().Count() == 3, "all three kinds again");

        // One of them alone, undone: only its own reminders.
        bool oneDeleted = (bool)typeof(CollectionView).GetMethod("DeleteEntities", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Invoke(view, new object[] { (IReadOnlyList<Entity>)new List<Entity> { EntityStore.Load(camping).Single(e => e.Name == "Boots") } });
        await Wait(300);
        Check(oneDeleted && EntityStore.Load(camping).Count == 3 && reminders.All(id => ReminderOf(id) != null), "the garment alone: the others and their reminders stay");
        Check(main.Undo.Undo() && ItemsText(camping) == start, "undone byte for byte");

        // Backup has it all.
        string zip = Path.Combine(customWork, "mixed-backup.zip");
        DataBackup.Create(zip);
        using (ZipArchive archive = ZipFile.OpenRead(zip))
        {
            string folder = "Memories/" + camping.Id.ToString("N") + "/";
            Check(archive.GetEntry(folder + "layout.json") != null && archive.GetEntry(folder + Path.GetFileName(MemoryFiles.Items(camping))) != null,
                "the backup has the collection's layout and its items");
        }
        Invoke(main, "NavigateHome");
        RemoveMemory(main, copy);
    }

    // ---- A custom item into a collection of another kind -----------------------------------------------------

    static async Task MixedImportIntoPlain(MainWindow main)
    {
        Console.WriteLine("A custom item imported into a vehicle collection that has no layout");
        Profile camping = Profiles(main).First(p => p.Name == "Mixed camping");
        Entity tent = EntityStore.Load(camping).Single(e => e.Name == "Camp tent");
        CustomLayoutStore.TryLoad(camping, out CustomLayout layout, out _);
        string package = Path.Combine(customWork, "mixed-tent" + TransferPackage.Extension);
        TransferPackage.ExportItem(tent, package, null, CancellationToken.None, layout);
        OpenedPackage item = TransferPackage.Open(package);

        Profile shelf = AddMemory(main, "Mixed shelf", ProfileType.Collection);
        Entity scooter = Vehicle("Scooter");
        EntityStore.TrySave(shelf, new List<Entity> { scooter });
        string scooterBefore = Serialized(shelf, "Scooter");
        ImportDialog preview = new ImportDialog(item, Profiles(main), shelf);
        ComboBox targets = (ComboBox)preview.FindName("TargetBox");
        Check(targets.Items.OfType<ComboBoxItem>().Any(i => (string)i.Content == "Mixed shelf"), "the collection is offered");
        Check(((TextBlock)preview.FindName("WhereText")).Text.Contains(Loc.F("Transfer.Import.AddsLayout", "Mixed shelf")), "and the preview says it gets a layout");
        preview.Close();

        using (StagedImport staged = TransferPackage.Stage(item, null, CancellationToken.None))
        {
            TransferPackage.CommitItem(staged, shelf, Profiles(main), "#3498DB", "Imported");
            foreach (Entity added in staged.Added)
                VehicleReminders.Follow(null, added);
        }
        List<Entity> items = EntityStore.Load(shelf);
        CustomLayoutStore.TryLoad(shelf, out CustomLayout made, out _);
        Check(items.Count == 2 && items.Single(e => e.Name == "Camp tent").EntityType == CustomTypes.TypeOf(shelf.Id) && made != null, "the collection has a layout and the custom item of it");
        Check(Serialized(shelf, "Scooter") == scooterBefore, "the scooter is byte for byte as it was");
        Check(made.Blocks.Count > 0 && made.Blocks.All(b => layout.Blocks.Any(x => x.Id == b.Id)) && made.Blocks.All(b => b.Kind != "Timeline" && b.Kind != BlockKinds.Gallery)
              && EntityTypes.Describe(items.Single(e => e.Name == "Camp tent")).Any(l => l.Value.Contains("Seam sealed")), "the layout is made of the blocks the item uses; it shows everything");

        // Then Add item > Custom there is a new item of that layout (not the first page again).
        CollectionView view = OpenCollectionView(main, shelf);
        await Wait(400);
        await AddVia(main, view, "Custom", "Another tent", async w =>
        {
            Check(!Customizing(w), "Custom there: a new item of the layout, Customize off");
            await Task.CompletedTask;
        });
        Check(EntityStore.Load(shelf).Count == 3, "three items of two kinds");
        Invoke(main, "NavigateHome");
        RemoveMemory(main, shelf);
    }
}
