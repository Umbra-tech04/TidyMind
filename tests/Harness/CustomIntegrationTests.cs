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

// Harness custom, M4: a custom collection with everything else. A Documents block's reminders (set, retitled, gone with
// the document, the block, the item), Home's deadlines, global search, the Day Plan's dates, PDF/Word/Excel exports,
// .tidymind packages (the layout goes along, new Ids, items re-pointed, a single item merged into another custom
// collection's layout or a new one, a damaged layout refused), several items deleted at once and undone, a deleted custom
// collection brought back, backup and restore.
static partial class Program
{
    static string customWork;

    static async Task CustomIntegration(MainWindow main)
    {
        customWork = Path.Combine(Path.GetDirectoryName(AppPaths.DataFolder), "custom-work");
        Directory.CreateDirectory(customWork);
        try
        {
            await CustomReminders(main);
            CustomSearchHomeExports(main);
            await CustomDayPlan(main);
            await CustomTransfer(main);
            await CustomBulkAndUndo(main);
            CustomBackup(main);
        }
        finally
        {
            foreach (Reminder reminder in ReminderManager.LoadReminders())
                ReminderManager.DeleteReminder(reminder.Id);
        }
    }


    // ---- Reminders ----------------------------------------------------------------------------------------

    static async Task CustomReminders(MainWindow main)
    {
        Console.WriteLine("A Documents block's reminders");
        UndoService.EndAll();
        Profile memory = Profiles(main).First(p => p.Name == "Gear");
        Dictionary<string, string> ids = GearIds(memory);
        CollectionView view = OpenCollectionView(main, memory);
        await Wait(300);
        Guid tentId = EntityStore.Load(memory).First(e => e.Name == "Tent").Id;
        RecordDefinition documents = EntityTypes.Records(CustomTypes.TypeOf(memory.Id)).First(r => r.Key == ids["documents"]);
        DateTime until = DateTime.Today.AddDays(45);

        await WithVehicle(main, view, tentId, async w =>
        {
            await AddRecord(main, w, documents, async dialog =>
            {
                List<FieldEditor> fields = dialog.Editors.ToList();
                (Descendants(fields[0].Element).OfType<TextBox>().FirstOrDefault() ?? (TextBox)fields[0].Element).Text = "Passport";
                (Descendants(fields[2].Element).OfType<DatePicker>().FirstOrDefault() ?? (DatePicker)fields[2].Element).SelectedDate = until;
                await Wait(100);
            });
        });
        EntityRecord passport = StoredLines(memory, tentId, ids["documents"]).First(r => r.Get("Name") == "Passport");
        Reminder reminder = ReminderOf(passport.Id);
        Check(reminder != null && reminder.Title == "Tent" && reminder.NextFireTime == until.AddDays(-30).AddHours(VehicleReminders.Hour),
            "a document with its date gets its reminder, 30 days before at 9:00: " + reminder?.NextFireTime);
        Check(VehicleReminders.ShownMessage(reminder, VehicleReminders.DeadlineIndex())
              == Loc.F("Entity.Reminder.Message", "Passport", until.ToString(Loc.T("Format.LongDate"), Loc.Culture)), "it says what runs out when");
        Check(ReminderTaskNames().Contains("TidyMind_Reminder_" + passport.Id), "its scheduled task is there");

        DashboardData home = DashboardDataAggregator.Build(Profiles(main), DateTime.Now);
        Check(home.Expiries.Any(x => x.ItemName == "Tent" && x.Deadline?.Name == "Warranty"), "Home lists what comes due in a custom collection");

        // Renamed item: the reminder's title follows; the document deleted: the reminder goes, an undo brings it back.
        await WithVehicle(main, view, tentId, async w =>
        {
            Entity renamed = StoredItem(memory, tentId);
            renamed.Name = "Tent 2";
            Invoke(w, "Commit", new Action<Entity>(v => v.Name = "Tent 2"));
            await Wait(300);
            Check(ReminderOf(passport.Id)?.Title == "Tent 2", "renamed: the reminder's title follows");
            Invoke(w, "Commit", new Action<Entity>(v => v.Name = "Tent"));
            await Wait(300);
            Invoke(w, "DeleteRecord", documents, passport);
            await Wait(400);
            Check(ReminderOf(passport.Id) == null, "the document deleted: its reminder is gone");
            Check(UndoHost.Of(w).Undo(), "undone");
            await Wait(400);
            Check(ReminderOf(passport.Id) != null, "and the reminder is back");
        });

        // The whole Documents block removed in the layout editor: its reminders go too; Ctrl+Z brings them back.
        await WithVehicle(main, view, tentId, async w =>
        {
            Invoke(w, "EditLayout");
            Invoke(w, "RemoveBlock", ids["documents"]);
            await Wait(400);
            Check(ReminderOf(passport.Id) == null, "its block removed: the reminder is gone");
            Check(UndoHost.Of(w).Undo(), "undone");
            await Wait(400);
            Check(ReminderOf(passport.Id) != null && StoredLines(memory, tentId, ids["documents"]).Any(r => r.Id == passport.Id), "the block, its documents and the reminder back");
        });
    }

    // ---- Search, Home, exports -----------------------------------------------------------------------

    static void CustomSearchHomeExports(MainWindow main)
    {
        Console.WriteLine("Global search and exports");
        Profile memory = Profiles(main).First(p => p.Name == "Gear");
        List<SearchResult> index = SearchIndexBuilder.Build(Profiles(main));
        List<SearchResult> found = SearchIndexBuilder.Filter(index, "Hilleberg", 10);
        Check(found.Any(r => r.Kind == SearchResultKind.Entity && r.Title == "Tent" && r.Profile.Id == memory.Id), "a field's value finds the item");
        Check(SearchIndexBuilder.Filter(index, "Seam sealed", 10).Any(r => r.Title == "Tent"), "so does a log entry");
        Check(SearchIndexBuilder.Filter(index, "Stove", 10).Any(r => r.Title == "Tent"), "and a checklist line");

        Entity tent = EntityStore.Load(memory).First(e => e.Name == "Tent");
        string docx = Path.Combine(customWork, "tent.docx"), pdf = Path.Combine(customWork, "tent.pdf"), xlsx = Path.Combine(customWork, "gear.xlsx");
        ExportService.ExportEntity(tent, docx, ExportFormat.Docx);
        string text = DocxText(docx);
        Check(new[] { "Hilleberg", "Remarks", "Keep dry", "Packing", "Papers", "Passport", "Repairs", "Seam sealed", "Spares", "Pegs" }.All(text.Contains),
            "Word: every block under its name: " + text.Substring(0, Math.Min(400, text.Length)));
        Check(text.IndexOf("Hilleberg", StringComparison.Ordinal) < text.IndexOf("Keep dry", StringComparison.Ordinal)
              && text.IndexOf("Keep dry", StringComparison.Ordinal) < text.IndexOf("Seam sealed", StringComparison.Ordinal), "in the layout's order");
        ExportService.ExportEntity(tent, pdf, ExportFormat.Pdf);
        Check(File.Exists(pdf) && new FileInfo(pdf).Length > 1000, "PDF written");
        ExportService.ExportMemory(memory, xlsx, ExportFormat.Excel);
        using (XLWorkbook workbook = new XLWorkbook(xlsx))
        {
            List<string> sheets = workbook.Worksheets.Select(s => s.Name).ToList();
            Check(new[] { "Packing", "Papers", "Repairs", "Spares" }.All(n => sheets.Any(s => s.StartsWith(n))), "Excel: a sheet per list: " + string.Join(", ", sheets));
            List<string> headers = workbook.Worksheets.SelectMany(s => s.CellsUsed()).Select(c => c.GetString()).ToList();
            Check(headers.Contains("Brand") && headers.Contains("Weight (kg)"), "the fields as columns, a number's unit in its name: " + string.Join(" | ", headers.Take(40)));
        }
        string allDocx = Path.Combine(customWork, "all.docx");
        ExportService.ExportAll(Profiles(main), allDocx, ExportFormat.Docx);
        Check(DocxText(allDocx).Contains("Seam sealed"), "Export all has it too");
    }

    // ---- The Day Plan ------------------------------------------------------------------------------------

    static async Task CustomDayPlan(MainWindow main)
    {
        Console.WriteLine("The Day Plan: a custom item's dates on that day");
        Profile memory = Profiles(main).First(p => p.Name == "Gear");
        Dictionary<string, string> ids = GearIds(memory);
        List<Entity> items = EntityStore.Load(memory);
        Entity tent = items.First(e => e.Name == "Tent");
        FieldDefinition.WriteDetail(tent, ids["bought"], DateTime.Today.ToString("yyyy-MM-dd"));
        EntityStore.TrySave(memory, items);
        DateTime warranty = DateTime.Today.AddDays(20);

        Invoke(main, "ShowDayPlan", DateTime.Today, null);
        await Wait(500);
        DayPlanView plan = (DayPlanView)Content(main);
        Panel dates = (Panel)plan.FindName("DatesPanel");
        Check(((FrameworkElement)plan.FindName("DatesSection")).Visibility == Visibility.Visible && dates.Children.Count == 1, "today: the date field set to today");
        Check(Descendants(dates).OfType<TextBlock>().Any(t => t.Text == "Tent") && Descendants(dates).OfType<TextBlock>().Any(t => t.Text.StartsWith("Bought")),
            "the item's name and which date");
        List<CustomDates.ItemDate> later = CustomDates.On(Profiles(main), warranty);
        Check(later.Any(d => d.Item.Name == "Tent" && d.What == Loc.F("Custom.Plan.RunsOut", "Warranty")), "the day a document runs out");
        Check(CustomDates.On(Profiles(main), DateTime.Today.AddDays(3)).Count == 0, "a day without dates has none");

        DriveItem(main, async w =>
        {
            Check(w.Title == "Tent", "a click opens the item");
            await Task.CompletedTask;
        });
        ClickNow((Button)dates.Children[0]);
        await Wait(800);
        Check(Content(main) is CollectionView, "on its collection's page");
    }

    // ---- Transfer ----------------------------------------------------------------------------------------

    static async Task CustomTransfer(MainWindow main)
    {
        Console.WriteLine("Transfer: the layout goes along");
        Profile gear = Profiles(main).First(p => p.Name == "Gear");
        CustomLayoutStore.TryLoad(gear, out CustomLayout layout, out _);
        List<Entity> items = EntityStore.Load(gear);
        string package = Path.Combine(customWork, "gear" + TransferPackage.Extension);
        TransferPackage.ExportCollection(gear, new List<MemoryTab>(), items, null, 0, package, null, CancellationToken.None, layout);
        OpenedPackage opened = TransferPackage.Open(package);
        Check(opened.Manifest.Format == 5 && opened.Layout != null, "a custom collection's package is format 5, with its layout");
        Profile copy;
        using (StagedImport staged = TransferPackage.Stage(opened, null, CancellationToken.None))
        {
            copy = TransferPackage.CommitCollection(staged, Profiles(main), "#3498DB");
            foreach (Entity added in staged.Added)
                VehicleReminders.Follow(null, added);
        }
        Invoke(main, "BuildNav");
        CustomLayoutStore.TryLoad(copy, out CustomLayout copied, out _);
        Check(copied != null && copied.Blocks.Select(b => b.Id).SequenceEqual(layout.Blocks.Select(b => b.Id)), "the copy has the layout, its blocks' Ids kept");
        List<Entity> copiedItems = EntityStore.Load(copy);
        Entity tentCopy = copiedItems.First(e => e.Name == "Tent");
        Entity tent = items.First(e => e.Name == "Tent");
        Check(copiedItems.All(e => e.EntityType == CustomTypes.TypeOf(copy.Id)) && tentCopy.Id != tent.Id, "its items are of the new collection, with new Ids");
        Check(tentCopy.Details.OrderBy(d => d.Key).SequenceEqual(tent.Details.OrderBy(d => d.Key)), "the fields' values as they were");
        Check(EntityTypes.Records(tentCopy.EntityType).All(r => r.Read(tentCopy).Count == r.Read(tent).Count), "every list with all its lines");
        EntityRecord passportCopy = tentCopy.Records.Values.SelectMany(l => l).First(r => r.Get("Name") == "Passport");
        Check(EntityFiles.Records(tentCopy).All(r => EntityFiles.Records(tent).All(o => o.Id != r.Id)), "the records have new Ids");
        Check(ReminderOf(passportCopy.Id) != null, "the imported document gets its own reminder");

        Console.WriteLine("Transfer: one custom item");
        string single = Path.Combine(customWork, "tent" + TransferPackage.Extension);
        TransferPackage.ExportItem(tent, single, null, CancellationToken.None, layout);
        OpenedPackage item = TransferPackage.Open(single);
        Check(item.Manifest.Format == 5 && item.Layout != null, "a custom item's package carries its layout");

        // Into the copy of its own collection: the layout gains nothing.
        string copyLayout = File.ReadAllText(MemoryFiles.Layout(copy));
        using (StagedImport staged = TransferPackage.Stage(item, null, CancellationToken.None))
            TransferPackage.CommitItem(staged, copy, Profiles(main), "#3498DB", "Imported");
        Check(File.ReadAllText(MemoryFiles.Layout(copy)) == copyLayout, "into a copy of its collection: that layout is left as it is");
        Check(EntityStore.Load(copy).Count(e => e.Name.StartsWith("Tent")) == 2, "added there, " + string.Join(", ", EntityStore.Load(copy).Select(e => e.Name)));

        // Into an unrelated custom collection: the blocks it holds something in are added to that layout.
        Profile birds = Profiles(main).First(p => p.Name == "Bird book");
        Profile plain = AddMemory(main, "Plain shelf", ProfileType.Collection);
        int birdBlocks = GearLayout(birds).Blocks.Count;
        ImportDialog preview = new ImportDialog(item, Profiles(main), birds);
        ComboBox targets = (ComboBox)preview.FindName("TargetBox");
        List<string> offered = targets.Items.OfType<ComboBoxItem>().Select(i => (string)i.Content).ToList();
        Check(offered.Contains("Bird book") && offered.Contains("Gear") && !offered.Contains("Plain shelf"), "only custom collections are offered: " + string.Join(", ", offered));
        Check(((TextBlock)preview.FindName("WhereText")).Text.Contains(Loc.F("Transfer.Import.AddsBlocks", "Bird book")), "the preview says the layout gains blocks");
        preview.Close();
        using (StagedImport staged = TransferPackage.Stage(item, null, CancellationToken.None))
            TransferPackage.CommitItem(staged, birds, Profiles(main), "#3498DB", "Imported");
        CustomLayout birdLayout = GearLayout(birds);
        Entity tentBird = EntityStore.Load(birds).First(e => e.Name == "Tent");
        Check(tentBird.EntityType == CustomTypes.TypeOf(birds.Id), "the item is of that collection now");
        Check(birdLayout.Blocks.Count > birdBlocks && birdLayout.Blocks.Any(b => b.Id == GearIds(gear)["documents"]) && birdLayout.Blocks.All(b => b.Kind != "Timeline"),
            "the blocks it holds something in were added (not ones it doesn't use)");
        Check(birdLayout.Blocks.Count(b => b.Kind == BlockKinds.Gallery) == 1, "never a second gallery");
        Check(EntityTypes.Describe(tentBird).Any(l => l.Value.Contains("Seam sealed")), "everything it holds shows there");

        // A standard collection can't take it; with no target a new custom collection gets its layout.
        bool refused = false;
        using (StagedImport staged = TransferPackage.Stage(item, null, CancellationToken.None))
        {
            try { TransferPackage.CommitItem(staged, plain, Profiles(main), "#3498DB", "Imported"); }
            catch (TransferException e) { refused = e.Message.Contains("Plain shelf"); }
        }
        Check(refused && EntityStore.LoadOrEmpty(plain).Count == 0, "a standard collection refuses a custom item, nothing added");
        RemoveMemory(main, plain);
        Profile created;
        using (StagedImport staged = TransferPackage.Stage(item, null, CancellationToken.None))
            created = TransferPackage.CommitItem(staged, null, Profiles(main), "#3498DB", "Imported things");
        Invoke(main, "BuildNav");
        Check(CustomLayoutStore.TryLoad(created, out CustomLayout createdLayout, out _) && createdLayout != null
              && EntityStore.Load(created).Single().EntityType == CustomTypes.TypeOf(created.Id), "with nowhere to go: a new custom collection with its layout");

        // A package whose layout is damaged is refused before anything is written; one with odd entries is cleaned.
        string damaged = Path.Combine(customWork, "damaged" + TransferPackage.Extension);
        File.Copy(package, damaged, true);
        using (ZipArchive zip = ZipFile.Open(damaged, ZipArchiveMode.Update))
        {
            zip.GetEntry("layout.json").Delete();
            using (StreamWriter writer = new StreamWriter(zip.CreateEntry("layout.json").Open()))
                writer.Write("{ not json");
        }
        Dictionary<string, string> before = FolderSnapshot();
        bool damagedRefused = false;
        try { TransferPackage.Open(damaged); }
        catch (TransferException) { damagedRefused = true; }
        Check(damagedRefused && SameSnapshot(before, FolderSnapshot()), "a damaged layout: the package is refused, nothing written");
        string odd = Path.Combine(customWork, "odd" + TransferPackage.Extension);
        File.Copy(package, odd, true);
        using (ZipArchive zip = ZipFile.Open(odd, ZipArchiveMode.Update))
        {
            zip.GetEntry("layout.json").Delete();
            using (StreamWriter writer = new StreamWriter(zip.CreateEntry("layout.json").Open()))
                writer.Write("{\"Blocks\":[null,{\"Id\":\"x\",\"Kind\":\"Notes\",\"Name\":\"..\\\\..\\\\evil\"},{\"Id\":\"x\",\"Kind\":\"Log\"}],\"CardFields\":[\"nope\"]}");
        }
        OpenedPackage cleaned = TransferPackage.Open(odd);
        Check(cleaned.Layout.Blocks.Count == 1 && cleaned.Layout.Blocks[0].Name == "..\\..\\evil" && cleaned.Layout.CardFields == null,
            "an odd layout is cleaned (a name is only a name, never a path)");
        await Task.CompletedTask;

    }

    // ---- Several at once, undo, a deleted collection --------------------------------------------------

    static async Task CustomBulkAndUndo(MainWindow main)
    {
        Console.WriteLine("Several custom items deleted at once, and undone; a custom collection deleted and brought back");
        Profile copy = Profiles(main).First(p => p.Name == "Gear (2)");
        CollectionView view = OpenCollectionView(main, copy);
        await Wait(400);
        List<Entity> items = EntityStore.Load(copy);
        string file = MemoryFiles.Items(copy);
        string start = File.ReadAllText(file);
        List<Guid> reminders = items.SelectMany(EntityFiles.Records).Select(r => r.Id).Where(id => ReminderOf(id) != null).ToList();
        Check(reminders.Count > 0, "the copy's documents have reminders");
        bool deleted = (bool)typeof(CollectionView).GetMethod("DeleteEntities", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Invoke(view, new object[] { (IReadOnlyList<Entity>)items });
        await Wait(400);
        Check(deleted && EntityStore.Load(copy).Count == 0, "both deleted, one question");
        Check(reminders.All(id => ReminderOf(id) == null), "their reminders gone");
        Check(main.Undo.Undo() && File.ReadAllText(file) == start, "Ctrl+Z: back byte for byte");
        await Wait(300);
        Check(reminders.All(id => ReminderOf(id) != null), "with their reminders");

        string layoutFile = MemoryFiles.Layout(copy), layoutText = File.ReadAllText(layoutFile);
        Invoke(main, "DeleteMemory", copy);
        await Wait(500);
        Check(!Profiles(main).Any(p => p.Id == copy.Id), "the custom collection deleted");
        Check(reminders.All(id => ReminderOf(id) == null), "its documents' reminders gone");
        Check(main.Undo.Undo(), "undone");
        await Wait(500);
        Check(Profiles(main).Any(p => p.Id == copy.Id) && File.ReadAllText(layoutFile) == layoutText && File.ReadAllText(file) == start,
            "back with its layout and items, byte for byte");
        Check(CustomTypes.OfMemory(copy)?.Blocks.Count == 7 && reminders.All(id => ReminderOf(id) != null), "its blocks and reminders too");
    }

    // ---- Backup and restore ---------------------------------------------------------------------------

    static void CustomBackup(MainWindow main)
    {
        Console.WriteLine("Backup and restore");
        Profile gear = Profiles(main).First(p => p.Name == "Gear");
        string zip = Path.Combine(customWork, "backup.zip");
        DataBackup.Create(zip);
        string entry = "Memories/" + gear.Id.ToString("N") + "/layout.json";
        using (ZipArchive archive = ZipFile.OpenRead(zip))
            Check(archive.GetEntry(entry) != null, "the backup has " + entry);
        byte[] saved = File.ReadAllBytes(MemoryFiles.Layout(gear));
        CustomLayout changed = GearLayout(gear);
        changed.Blocks.Reverse();
        CustomLayoutStore.TrySave(gear, changed);
        CustomTypes.OfMemory(gear); // made from the changed layout, kept until the restore
        DataBackup.Restore(zip, out _);
        Check(File.ReadAllBytes(MemoryFiles.Layout(gear)).SequenceEqual(saved), "restored byte for byte");
        Check(CustomTypes.OfMemory(gear).Blocks[0].Kind == BlockKinds.Fields, "the definitions are made from the restored layout");

        string damaged = Path.Combine(customWork, "damaged.zip");
        File.Copy(zip, damaged, true);
        using (ZipArchive archive = ZipFile.Open(damaged, ZipArchiveMode.Update))
        {
            archive.GetEntry(entry).Delete();
            using (StreamWriter writer = new StreamWriter(archive.CreateEntry(entry).Open()))
                writer.Write("{ damaged");
        }
        Dictionary<string, string> before = FolderSnapshot();
        bool refused = false;
        try { DataBackup.Restore(damaged, out _); }
        catch (InvalidDataException e) { refused = e.Message.Contains("layout.json"); }
        Check(refused && SameSnapshot(before, FolderSnapshot()), "a backup with an unreadable layout is refused, nothing changed");
    }
}
