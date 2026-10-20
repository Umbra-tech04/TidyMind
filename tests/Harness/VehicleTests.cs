using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TidyMind;

// Harness vehicle <data> <shotsFolder>: the vehicle record. Old vehicles are read without a write and their two
// dates show as documents; an older version keeps the new fields and its date edits are picked up. The item window:
// the title block's chips, its pages, a section edited in place (numbers checked, kW and hp), records added in a
// dialog and shown as rows that open (documents with a file and their reminders: set, moved, removed, retitled, after
// a delete of the document, the vehicle, a selection and the memory; wheel sets; parts under parts; the service log
// with its costs and filter), the odometer update, Esc, picture keys, its remembered size, three languages at a narrow
// size without clipped text. Transfer packages (format 3, document files, new Ids, reminders on import), exports,
// search and Home. A few screenshots.
static partial class Program
{
    static string vehicleWork;

    static async Task VehicleScenarios(MainWindow main)
    {
        await Wait(1200);
        GuidedTour.Current?.Stop(TourEnd.Skipped);
        StartSelectWatcher(); // answers (Yes, OK) and logs message boxes
        vehicleWork = Path.Combine(Path.GetDirectoryName(AppPaths.DataFolder), "vehicle-work");
        if (Directory.Exists(vehicleWork))
            Directory.Delete(vehicleWork, true);
        Directory.CreateDirectory(vehicleWork);

        try
        {
            VehicleFireTimes();
            await VehicleOldData(main);
            VehicleOlderVersion();
            await VehicleWindowAndSections(main);
            await VehicleDocumentsAndReminders(main);
            await VehicleOdometer(main);
            await VehicleWheels(main);
            await VehicleParts(main);
            VehicleServiceLegacy();
            await VehicleServiceLog(main);
            await VehicleLanguagesAndSize(main);
            await VehicleTransfer(main);
            VehicleExportsAndSearch(main);
            RecordTreeMoves();
            await VehicleDeletes(main);
        }
        finally
        {
            foreach (Reminder reminder in ReminderManager.LoadReminders())
                ReminderManager.DeleteReminder(reminder.Id);
        }

        Console.WriteLine("message boxes: " + selectBoxes.Count);
        lock (selectBoxes)
            foreach (string box in selectBoxes)
                Console.WriteLine("  " + box);
    }

    // ---- Helpers -----------------------------------------------------------------------

    // Runs `act` on the item window once it is up (the caller's ShowDialog is open meanwhile), then closes it.
    static void DriveItem(MainWindow main, Func<ItemWindow, Task> act)
    {
        _ = main.Dispatcher.BeginInvoke(new Action(async () =>
        {
            ItemWindow window = null;
            for (int i = 0; i < 50 && window == null; i++)
            {
                await Wait(100);
                window = Application.Current.Windows.OfType<ItemWindow>().FirstOrDefault();
            }
            try
            {
                await Wait(300);
                await act(window);
            }
            catch (Exception e)
            {
                Check(false, "the item window driver threw: " + e);
            }
            window?.Close();
        }), DispatcherPriority.Background);
    }

    // Opens the item from the collection page (its window, as a click on the card does) and drives it.
    static async Task WithVehicle(MainWindow main, CollectionView view, Guid id, Func<ItemWindow, Task> act)
    {
        DriveItem(main, act);
        view.Reveal(id); // returns once the window is closed
        await Wait(200);
    }

    // Fills in the record dialog about to open with `fill`, then presses its Save (it closes once saved).
    static void AnswerRecord(MainWindow main, Func<EditDialog, Task> fill)
    {
        _ = main.Dispatcher.BeginInvoke(new Action(async () =>
        {
            EditDialog dialog = null;
            for (int i = 0; i < 50 && dialog == null; i++)
            {
                await Wait(100);
                dialog = Application.Current.Windows.OfType<EditDialog>().FirstOrDefault();
            }
            if (dialog == null)
            {
                Check(false, "a record dialog opened");
                return;
            }
            try
            {
                await Wait(200);
                await fill(dialog);
                ClickNow(ButtonNamed(dialog, Loc.T("Common.Save")));
                await Wait(500);
            }
            catch (Exception e)
            {
                Check(false, "the record dialog driver threw: " + e);
            }
            if (dialog.IsVisible)
            {
                Check(false, "the record dialog closed after Save");
                dialog.Close();
            }
        }), DispatcherPriority.Background);
    }

    // A new record of `records` through its dialog (as "+ Add …" does), filled in by `fill`.
    static async Task AddRecord(MainWindow main, ItemWindow w, RecordDefinition records, Func<EditDialog, Task> fill)
    {
        AnswerRecord(main, fill);
        Invoke(w, "OpenRecord", records, null, null); // returns once the dialog is closed
        await Wait(300);
    }

    // Types `value` into the next text prompt (InputDialog) and confirms it, or cancels it with null.
    static void AnswerPrompt(MainWindow main, string value)
    {
        _ = main.Dispatcher.BeginInvoke(new Action(async () =>
        {
            InputDialog dialog = null;
            for (int i = 0; i < 50 && dialog == null; i++)
            {
                await Wait(100);
                dialog = Application.Current.Windows.OfType<InputDialog>().FirstOrDefault();
            }
            if (dialog == null)
                return;
            if (value == null)
            {
                dialog.Close();
                return;
            }
            Field<TextBox>(dialog, "ValueInput").Text = value;
            await Wait(100);
            dialog.DialogResult = true;
        }), DispatcherPriority.Background);
    }

    static StackPanel PageOf(ItemWindow window) => Field<StackPanel>(window, "Page");

    static List<FieldsSection> Sections(ItemWindow window) => Descendants(PageOf(window)).OfType<FieldsSection>().ToList();

    static FieldsSection SectionTitled(ItemWindow window, string title) => Sections(window).First(s => s.Definition.Label == title);

    static List<RecordRow> Rows(ItemWindow window) => Descendants(PageOf(window)).OfType<RecordRow>().ToList();

    static RecordRow RowOf(ItemWindow window, Guid id) => Rows(window).Single(r => r.Record.Id == id);

    static RecordRow RowNamed(ItemWindow window, string name) => Rows(window).First(r => r.Record.Get("Name") == name);

    static List<string> PageTexts(ItemWindow window) => Descendants(PageOf(window)).OfType<TextBlock>().Select(t => t.Text).ToList();

    static List<string> ChipTexts(ItemWindow window) => Descendants(Field<WrapPanel>(window, "Chips")).OfType<TextBlock>().Select(t => t.Text).ToList();

    // The search's matches: a row each, named "where: what".
    static List<Button> Matches(ItemWindow window) => Descendants(PageOf(window)).OfType<Button>()
        .Where(b => System.Windows.Automation.AutomationProperties.GetName(b).Contains(": ")).ToList();

    static List<FieldEditor> Editors(FieldsSection section) => Field<List<FieldEditor>>(section, "editors");

    static FieldEditor EditorOf(FieldsSection section, string key) => Editors(section).First(e => e.Field.Key == key);

    static FieldEditor EditorOf(EditDialog dialog, string key) => dialog.Editors.First(e => e.Field.Key == key);

    static void Choose(FieldEditor editor, string value) =>
        ((ComboBox)editor.Element).SelectedItem = ((ComboBox)editor.Element).Items.Cast<ComboBoxItem>().First(i => (string)i.Tag == value);

    static string Chosen(FieldEditor editor) => (string)((ComboBoxItem)((ComboBox)editor.Element).SelectedItem).Tag;

    static TextBox BoxOf(FieldEditor editor, int index = 0) =>
        editor.Element as TextBox ?? Descendants(editor.Element).OfType<TextBox>().ElementAt(index);

    static async Task SaveSection(FieldsSection section)
    {
        ClickNow(ButtonNamed(section, Loc.T("Common.Save")));
        await Wait(400);
    }

    static Entity StoredVehicle(Profile memory, Guid id) => EntityStore.Load(memory).Single(e => e.Id == id);

    static Reminder ReminderOf(Guid id) => ReminderManager.LoadReminders().SingleOrDefault(r => r.Id == id);

    static string Sha(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));

    // ---- When a document's reminder fires ------------------------------------------------------

    static void VehicleFireTimes()
    {
        Console.WriteLine("Document reminder times");
        DateTime now = new DateTime(2026, 10, 19, 10, 0, 0);
        Check(VehicleReminders.FireTime(new DateTime(2026, 12, 1), 30, now) == new DateTime(2026, 11, 1, 9, 0, 0),
            "30 days before Dec 1: Nov 1, 09:00");
        Check(VehicleReminders.FireTime(new DateTime(2026, 10, 25), 30, now) == new DateTime(2026, 10, 20, 9, 0, 0),
            "that day already past (10:00): the next 09:00, tomorrow");
        Check(VehicleReminders.FireTime(new DateTime(2026, 10, 25), 30, now.AddHours(-2)) == new DateTime(2026, 10, 19, 9, 0, 0),
            "...or today at 09:00 when it's still before");
        Check(VehicleReminders.FireTime(now.Date, 7, now) == null, "runs out today, after 09:00: none (tomorrow is too late)");
        Check(VehicleReminders.FireTime(now.Date.AddDays(-1), 7, now) == null, "already run out: none");
        Check(VehicleReminders.FireTime(now.Date.AddDays(90), null, now) == null
              && VehicleReminders.FireTime(null, 30, now) == null, "no days or no date: none");
    }

    // ---- Old vehicles ----------------------------------------------------------------------------

    static async Task VehicleOldData(MainWindow main)
    {
        Console.WriteLine("A vehicle from before: read and shown without a write");
        Profile garage = AddMemory(main, "Régi garázs", ProfileType.Collection);
        Entity golf = Vehicle("Golf");
        golf.VehicleKind = "Car";
        golf.Plate = "ABC-123";
        golf.Year = 2015;
        golf.OdometerKm = 120500;
        golf.InsuranceExpires = DateTime.Today.AddDays(20);
        golf.InspectionExpires = DateTime.Today.AddDays(200);
        golf.Logs = new Dictionary<string, List<Dictionary<string, string>>>
        {
            ["Service"] = new List<Dictionary<string, string>> { new Dictionary<string, string> { ["Date"] = "2026-03-01", ["Work"] = "Olajcsere", ["Km"] = "118000" } }
        };
        EntityStore.SaveOrWarn(garage, new List<Entity> { golf });
        CollectionView view = OpenCollectionView(main, garage); // gives it its tab: a write of its own
        await Wait(500);
        string file = MemoryFiles.Items(garage);
        string before = Sha(file);
        string json = File.ReadAllText(file);
        Check(!json.Contains("\"Details\"") && !json.Contains("\"Records\""), "an old vehicle's file has no new fields");

        List<EntityRecord> documents = VehicleDocuments.Of(golf);
        Check(documents.Count == 2 && documents.Select(d => d.Get(VehicleDocuments.TypeKey)).SequenceEqual(new[] { "Insurance", "Inspection" })
              && VehicleDocuments.ValidUntil(documents[0]) == golf.InsuranceExpires && documents[0].Id == VehicleDocuments.Of(golf)[0].Id,
            "its two dates show as an Insurance and an Inspection document, with the same Id every time");
        Check(Descendants(view).OfType<TextBlock>().Any(t => t.Text == Loc.F("Entity.ExpiryLine", "Insurance", Expiry.Describe(golf.InsuranceExpires.Value, DateTime.Today))),
            "its card says when the insurance runs out");

        await WithVehicle(main, view, golf.Id, async w =>
        {
            List<string> chips = ChipTexts(w);
            Check(Field<TextBox>(w, "NameText").Text == "Golf" && chips.Contains("ABC-123") && chips.Contains(Units.Format(120500, Units.Distance, golf)),
                "the title block: the name, the plate and the odometer as chips");
            Check(chips.Contains(Loc.F("Entity.ExpiryLine", "Insurance", Expiry.Describe(golf.InsuranceExpires.Value, DateTime.Today)))
                  && chips.Count(c => c.Contains("Inspection")) == 0, "the insurance soon as a chip; the inspection, months off, only on the Overview");
            Check(PageTexts(w).Contains("Inspection") && PageTexts(w).Contains("Insurance"), "the Overview's Coming up lists both");
            Check(Field<ListBox>(w, "SectionList").Items.Count == 6, "six pages: Overview, Specs, Wheels & tyres, Fluids & parts, Documents, Service & costs");
            foreach (string tab in new[] { "Specs", "Wheels", "Parts", "Documents", "Service", "Overview" })
            {
                w.SelectPage(tab);
                await Wait(150);
            }
        });
        Check(Sha(file) == before, "opening its window and every tab wrote nothing");
        DashboardDataAggregator.Build(Profiles(main), DateTime.Now);
        Check(Sha(file) == before, "Home read it without a write too");
        Invoke(main, "NavigateHome");
    }

    // An older TidyMind (1.3.0) reads only the fields it knows and keeps the others as unknown ones.
    sealed class VehicleBeforeRecords
    {
        public Guid Id { get; set; }
        public string EntityType { get; set; }
        public string Name { get; set; }
        public DateTime? InsuranceExpires { get; set; }
        [JsonExtensionData] public Dictionary<string, JsonElement> ExtensionData { get; set; }
    }

    static void VehicleOlderVersion()
    {
        Console.WriteLine("An older version keeps the new fields and its date edits count");
        Entity car = Vehicle("Octavia");
        VehicleSchema.Engine.Fields.First(f => f.Key == "Power").Write(car, "110");
        VehicleSchema.Documents.Write(car, new List<EntityRecord> { Document("Insurance", new DateTime(2027, 1, 31), 30, "Allianz"), Document("Insurance", new DateTime(2026, 5, 1), null) });
        Check(car.InsuranceExpires == new DateTime(2027, 1, 31) && car.InspectionExpires == null, "storing the documents sets the old field to the newest insurance date");

        string json = JsonSerializer.Serialize(car);
        VehicleBeforeRecords older = JsonSerializer.Deserialize<VehicleBeforeRecords>(json);
        older.InsuranceExpires = new DateTime(2027, 6, 30); // edited there
        Entity back = JsonSerializer.Deserialize<Entity>(JsonSerializer.Serialize(older));
        Check(back.Details["Power"] == "110" && back.Records[VehicleDocuments.Key].Count == 2, "an older version wrote the new fields back as it found them");
        List<EntityRecord> documents = VehicleDocuments.Of(back);
        Check(documents.Count == 2 && VehicleDocuments.ValidUntil(documents[0]) == new DateTime(2027, 6, 30)
              && VehicleDocuments.ValidUntil(documents[1]) == new DateTime(2026, 5, 1)
              && VehicleDocuments.ValidUntil(back.Records[VehicleDocuments.Key][0]) == new DateTime(2027, 1, 31),
            "its edit of the date shows on the newest insurance document (a copy: the item itself unchanged until saved)");
    }

    // ---- The window ------------------------------------------------------------------------------

    static Profile vehicleGarage;
    static Guid octavia;

    static async Task VehicleWindowAndSections(MainWindow main)
    {
        Console.WriteLine("The window and a section edited in place");
        vehicleGarage = AddMemory(main, "Garázs", ProfileType.Collection);
        Entity car = Vehicle("Családi autó");
        car.Plate = "SU-123-AB";
        car.OdometerKm = 152300;
        string[] pictures = Enumerable.Range(0, 3).Select(i =>
        {
            string own = Path.Combine(vehicleWork, "car" + i + ".png");
            ColorPicture(own, i);
            return ImageStore.Items.Import(own);
        }).ToArray();
        EntityImages.SetNames(car, pictures);
        octavia = car.Id;
        EntityStore.SaveOrWarn(vehicleGarage, new List<Entity> { car });
        CollectionView view = OpenCollectionView(main, vehicleGarage);
        await Wait(500);

        await WithVehicle(main, view, octavia, async w =>
        {
            PictureHero hero = Field<PictureHero>(w, "hero");
            await Wait(500);
            PressKey(w, Key.Right);
            Check(hero.Selected == 1, "Right shows the next picture");
            PressKey(w, Key.Left);
            Check(hero.Selected == 0, "Left goes back");
            PressKey(w, Key.Left);
            Check(hero.Selected == 0, "...and no further than the first");

            w.SelectPage("Specs");
            await Wait(200);
            Check(Sections(w).Count == 5, "Specs: identity, engine, body, ownership, odometer");
            FieldsSection engine = SectionTitled(w, VehicleSchema.Engine.Label);
            Check(Descendants(engine).OfType<TextBlock>().Any(t => t.Text == Loc.T("Entity.Card.Empty")), "an empty section says so, on one line");
            engine.BeginEdit();
            await Wait(200);
            BoxOf(EditorOf(engine, "Power")).Text = "110";
            Check(BoxOf(EditorOf(engine, "Power"), 1).Text == "150", "110 kW typed: the hp box says 150");
            BoxOf(EditorOf(engine, "Power"), 1).Text = "190";
            Check(BoxOf(EditorOf(engine, "Power")).Text == (190 / 1.35962).ToString("0.#", Loc.Culture), "190 hp typed: the kW box follows");
            BoxOf(EditorOf(engine, "Power")).Text = "110";
            BoxOf(EditorOf(engine, "Displacement")).Text = "sok";
            Choose(EditorOf(engine, "Fuel"), "Diesel");
            int boxes = selectBoxes.Count;
            await SaveSection(engine);
            Check(selectBoxes.Count == boxes + 1 && StoredVehicle(vehicleGarage, octavia).Details == null, "a number field with words: said, nothing saved");
            BoxOf(EditorOf(engine, "Displacement")).Text = "1 968";
            await SaveSection(engine);
            Entity saved = StoredVehicle(vehicleGarage, octavia);
            Check(saved.Details?["Power"] == "110" && saved.Details["Displacement"] == "1968" && saved.Details["Fuel"] == "Diesel"
                  && saved.Plate == "SU-123-AB" && EntityImages.Names(saved).SequenceEqual(pictures), "saved at once: invariant numbers, the choice's English value, the rest kept");
            engine = SectionTitled(w, VehicleSchema.Engine.Label);
            Check(Descendants(engine).OfType<TextBox>().Any(t => t.Text == "110 kW (150 hp)"), "shown as 110 kW (150 hp)");

            FieldsSection ownership = SectionTitled(w, VehicleSchema.Ownership.Label);
            ownership.BeginEdit();
            await Wait(200);
            WrapPanel checks = Descendants(EditorOf(ownership, "Equipment").Element).OfType<WrapPanel>().First();
            ((CheckBox)checks.Children[0]).IsChecked = true;
            TextBox own = Descendants(EditorOf(ownership, "Equipment").Element).OfType<TextBox>().First();
            own.Text = "Állófűtés";
            ClickNow(Descendants(EditorOf(ownership, "Equipment").Element).OfType<Button>().First());
            ((CheckBox)EditorOf(ownership, "ServiceBook").Element).IsChecked = true;
            BoxOf(EditorOf(ownership, "PurchasePrice")).Text = "4500000";
            await SaveSection(ownership);
            saved = StoredVehicle(vehicleGarage, octavia);
            Check(saved.Details["Equipment"] == "Air conditioning\nÁllófűtés" && saved.Details["ServiceBook"] == "true" && saved.Details["PurchasePrice"] == "4500000",
                "equipment: an offered one and the user's own, one per line; the flag; the price as a plain number");

            // Esc while editing stops the edit (asking first: the watcher says Yes), and a second Esc closes.
            SectionTitled(w, VehicleSchema.Body.Label).BeginEdit();
            await Wait(200);
            BoxOf(EditorOf(SectionTitled(w, VehicleSchema.Body.Label), "Doors")).Text = "5";
            boxes = selectBoxes.Count;
            PressKey(w, Key.Escape);
            await Wait(300);
            Check(selectBoxes.Count == boxes + 1 && !SectionTitled(w, VehicleSchema.Body.Label).IsEditing && w.IsVisible,
                "Esc during an edit with changes asks, then stops it; the window stays");
            RenderWindow(w, "vehicle-01-specs-en");
        });
        Check(Application.Current.Windows.OfType<ItemWindow>().Count() == 0, "closed");
        Invoke(main, "NavigateHome");
    }

    // ---- Documents and reminders -----------------------------------------------------------------

    static async Task VehicleDocumentsAndReminders(MainWindow main)
    {
        Console.WriteLine("Documents: file, date, reminder");
        CollectionView view = OpenCollectionView(main, vehicleGarage);
        await Wait(400);
        string scan = Path.Combine(vehicleWork, "kötvény.pdf");
        File.WriteAllText(scan, "%PDF-1.4 test");
        Guid documentId = Guid.Empty;
        DateTime due = DateTime.Today.AddDays(45);

        await WithVehicle(main, view, octavia, async w =>
        {
            w.SelectPage("Documents");
            await Wait(200);
            Check(PageTexts(w).Contains(Loc.T("Entity.Records.Documents.Empty")), "no documents yet: the list says what it is for");
            Check(Descendants(w).OfType<Button>().Any(b => b.Content as string == "+ " + VehicleSchema.Documents.AddLabel),
                "its Add on the page title's line (the page is that one list)");
            await AddRecord(main, w, VehicleSchema.Documents, d =>
            {
                Check(d.Title == VehicleSchema.Documents.AddLabel && Chosen(EditorOf(d, "Type")) == "Insurance" && Chosen(EditorOf(d, "RemindDays")) == "30",
                    "a new document, in a dialog: Insurance, 30 days before");
                BoxOf(EditorOf(d, "Name")).Text = "Allianz casco";
                ((DatePicker)EditorOf(d, "ValidUntil").Element).SelectedDate = due;
                typeof(FieldEditor).GetProperty("PickedFile").GetSetMethod(true).Invoke(EditorOf(d, "File"), new object[] { scan });
                return Task.CompletedTask;
            });
            Check(Rows(w).Count == 1 && Rows(w)[0].IsOpen, "saved: its row, open");
        });

        Entity saved = StoredVehicle(vehicleGarage, octavia);
        EntityRecord document = VehicleDocuments.Of(saved).Single();
        documentId = document.Id;
        Check(document.File != null && document.File.DisplayName == "kötvény.pdf" && File.ReadAllText(AttachmentStore.PathOf(document.File.StoredFileName)) == "%PDF-1.4 test",
            "its file is copied in (own name kept for showing)");
        Check(saved.InsuranceExpires == due, "the old insurance field follows the document");
        Reminder reminder = ReminderOf(documentId);
        Check(reminder != null && reminder.NextFireTime == due.AddDays(-30).AddHours(9) && reminder.Title == "Családi autó" && reminder.Message == ""
              && TaskTrigger(documentId) == reminder.NextFireTime, "a one-off reminder with the document's Id, 30 days before at 09:00, its task too");
        Check(VehicleReminders.ShownMessage(reminder, VehicleReminders.DeadlineIndex())
              == Loc.F("Entity.Reminder.Message", "Insurance · Allianz casco", due.ToString(Loc.T("Format.LongDate"), Loc.Culture)),
            "what it says is made when shown: " + VehicleReminders.ShownMessage(reminder, VehicleReminders.DeadlineIndex()));
        Check(DashboardDataAggregator.Build(Profiles(main), DateTime.Now).Expiries.Any(x => x.Name == "Allianz casco" && x.Date == due),
            "Home lists it under its own name");

        // A changed date moves the reminder, a renamed vehicle retitles it, no days removes it.
        Entity moved = JsonSerializer.Deserialize<Entity>(JsonSerializer.Serialize(saved));
        List<EntityRecord> documents = VehicleSchema.Documents.Read(moved);
        documents[0].Set(VehicleDocuments.ValidUntilKey, Values.FromDate(due.AddDays(10)));
        VehicleSchema.Documents.Write(moved, documents);
        Check((bool)view.GetType().GetMethod("Replace", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(view, new object[] { moved })
              && ReminderOf(documentId).NextFireTime == due.AddDays(-20).AddHours(9), "a later date: the reminder moves with it");
        Entity renamed = JsonSerializer.Deserialize<Entity>(JsonSerializer.Serialize(moved));
        renamed.Name = "Škoda „Đurđa”";
        Invoke(view, "Replace", renamed);
        Check(ReminderOf(documentId).Title == "Škoda „Đurđa”" && ReminderOf(documentId).NextFireTime == due.AddDays(-20).AddHours(9),
            "renamed: the reminder's title follows, its time stays");
        Entity quiet = JsonSerializer.Deserialize<Entity>(JsonSerializer.Serialize(renamed));
        documents = VehicleSchema.Documents.Read(quiet);
        documents[0].Set(VehicleDocuments.RemindKey, null);
        VehicleSchema.Documents.Write(quiet, documents);
        Invoke(view, "Replace", quiet);
        Check(ReminderOf(documentId) == null && TaskTrigger(documentId) == null, "no reminder days: the reminder and its task are gone");
        Entity again = JsonSerializer.Deserialize<Entity>(JsonSerializer.Serialize(quiet));
        documents = VehicleSchema.Documents.Read(again);
        documents[0].Set(VehicleDocuments.RemindKey, "14");
        VehicleSchema.Documents.Write(again, documents);
        Invoke(view, "Replace", again);
        Check(ReminderOf(documentId)?.NextFireTime == due.AddDays(10 - 14).AddHours(9), "days set again: back, 14 days before");

        // Deleting the document in the window: its file and reminder go.
        string stored = AttachmentStore.PathOf(document.File.StoredFileName);
        await WithVehicle(main, view, octavia, async w =>
        {
            w.SelectPage("Documents");
            await Wait(200);
            RecordRow row = Rows(w).Single();
            Check(Descendants(row).OfType<Button>().Any(b => b.Content as string == "kötvény.pdf"), "the document's row, opened, links its file");
            ClickNow(ButtonNamed(row, Loc.T("Common.Delete")));
            await Wait(400);
        });
        Check(VehicleDocuments.Of(StoredVehicle(vehicleGarage, octavia)).Count == 0 && File.Exists(stored) && ReminderOf(documentId) == null,
            "deleted (asked first): the reminder is gone, the file kept while it can be undone");
        UndoService.EndAll();
        Check(!File.Exists(stored), "then the file is gone too");
        Check(StoredVehicle(vehicleGarage, octavia).InsuranceExpires == null, "...and the old insurance field with it");
        Invoke(main, "NavigateHome");
    }

    // ---- Odometer ------------------------------------------------------------------------------------

    static async Task VehicleOdometer(MainWindow main)
    {
        Console.WriteLine("Update odometer");
        CollectionView view = OpenCollectionView(main, vehicleGarage);
        await Wait(400);
        await WithVehicle(main, view, octavia, async w =>
        {
            AnswerPrompt(main, "153 450");
            Invoke(w, "UpdateOdometer");
            await Wait(300);
            Entity saved = StoredVehicle(vehicleGarage, octavia);
            List<Dictionary<string, string>> readings = saved.Logs[VehicleSchema.OdometerLog];
            Check(saved.OdometerKm == 153450 && readings.Count == 1 && readings[0]["Reading"] == "153450" && readings[0]["Date"] == Values.FromDate(DateTime.Today),
                "a reading: the current one and a dated row in the readings");
            Check(ChipTexts(w).Contains(Units.Format(153450, Units.Distance, saved)), "the title block's chip shows it");

            int boxes = selectBoxes.Count;
            AnswerPrompt(main, "150000");
            Invoke(w, "UpdateOdometer");
            await Wait(300);
            Check(selectBoxes.Count == boxes + 1 && StoredVehicle(vehicleGarage, octavia).OdometerKm == 150000, "lower than before: asked (Yes here), then saved");

            w.SelectPage("Specs");
            await Wait(200);
            FieldsSection odometer = SectionTitled(w, VehicleSchema.OdometerSection.Label);
            odometer.BeginEdit();
            await Wait(200);
            Choose(EditorOf(odometer, VehicleSchema.OdometerUnitKey), Units.Mi);
            await SaveSection(odometer);
            Check(ChipTexts(w).Contains("150,000 mi"), "miles chosen: every distance says mi (" + string.Join(", ", ChipTexts(w)) + ")");
        });
        Invoke(main, "NavigateHome");
    }

    // ---- Wheels and tyres --------------------------------------------------------------------------

    static async Task VehicleWheels(MainWindow main)
    {
        Console.WriteLine("Wheels and tyres: sets, the mounted one");
        CollectionView view = OpenCollectionView(main, vehicleGarage);
        await Wait(400);
        await WithVehicle(main, view, octavia, async w =>
        {
            w.SelectPage("Wheels");
            await Wait(200);
            Check(PageTexts(w).Contains(Loc.T("Entity.Records.WheelSets.Empty")), "no sets yet: the list says what it is for");
            foreach ((string name, string season, string size) in new[] { ("Nyári", "Summer", "205/55 R16 91V"), ("Téli", "Winter", "195/65 R15 91T") })
                await AddRecord(main, w, VehicleSchema.WheelSets, d =>
                {
                    BoxOf(EditorOf(d, "Name")).Text = name;
                    Choose(EditorOf(d, "Season"), season);
                    BoxOf(EditorOf(d, "TyreSize")).Text = size;
                    BoxOf(EditorOf(d, "BoltPattern")).Text = "5x112";
                    BoxOf(EditorOf(d, "BoltTorque")).Text = "120";
                    return Task.CompletedTask;
                });
            List<EntityRecord> sets = VehicleSchema.WheelSets.Read(StoredVehicle(vehicleGarage, octavia));
            Check(sets.Count == 2 && sets[0].Get("TyreSize") == "205/55 R16 91V" && sets[1].Get("BoltTorque") == "120" && sets[1].Get("Season") == "Winter",
                "two sets stored in order: the tyre size as typed, numbers invariant, the season's English value");
            Check(Rows(w).Count == 2 && RowOf(w, sets[1].Id).IsOpen && !RowOf(w, sets[0].Id).IsOpen, "two rows, the one just added open");

            ClickNow(ButtonNamed(RowOf(w, sets[1].Id), Loc.T("Entity.Records.WheelSets.MakeCurrent")));
            await Wait(300);
            Check(StoredVehicle(vehicleGarage, octavia).Details[VehicleSchema.MountedWheelSetKey] == sets[1].Id.ToString(), "Mark as mounted: stored");
            Check(Descendants(RowOf(w, sets[1].Id)).OfType<TextBlock>().Any(t => t.Text == Loc.T("Entity.Records.WheelSets.Current"))
                  && Descendants(RowOf(w, sets[0].Id)).OfType<Button>().Any(b => b.Content as string == Loc.T("Entity.Records.WheelSets.MakeCurrent")),
                "the mounted set says so, the other offers it");
            ClickNow(ButtonNamed(RowOf(w, sets[1].Id), Loc.T("Common.Delete")));
            await Wait(400);
            Entity saved = StoredVehicle(vehicleGarage, octavia);
            Check(VehicleSchema.WheelSets.Read(saved).Count == 1 && !saved.Details.ContainsKey(VehicleSchema.MountedWheelSetKey),
                "the mounted set deleted: nothing is mounted any more");
        });
        Invoke(main, "NavigateHome");
    }

    // ---- Fluids and parts ----------------------------------------------------------------------------

    static Button ButtonNamed(DependencyObject root, string text) =>
        Descendants(root).OfType<Button>().First(b => b.Content as string == text || System.Windows.Automation.AutomationProperties.GetName(b) == text);

    static EntityRecord PartNamed(Entity vehicle, string name) =>
        RecordTree.Walk(VehicleSchema.Parts.Read(vehicle)).Select(x => x.Record).First(r => r.Get("Name") == name);

    static async Task VehicleParts(MainWindow main)
    {
        Console.WriteLine("Fluids and the parts tree: template, parts under parts, specs, photos, moves, search");
        string photo = Path.Combine(vehicleWork, "tárcsa.png");
        ColorPicture(photo, 3);
        CollectionView view = OpenCollectionView(main, vehicleGarage);
        await Wait(400);
        await WithVehicle(main, view, octavia, async w =>
        {
            w.SelectPage("Parts");
            await Wait(200);
            FieldsSection fluids = SectionTitled(w, VehicleSchema.Fluids.Label);
            fluids.BeginEdit();
            await Wait(200);
            BoxOf(EditorOf(fluids, "OilFilter")).Text = "Mann W 712/52";
            BoxOf(EditorOf(fluids, "OilCapacity")).Text = "4.3";
            BoxOf(EditorOf(fluids, "OilViscosity")).Text = "5W-30";
            await SaveSection(fluids);
            Entity saved = StoredVehicle(vehicleGarage, octavia);
            Check(saved.Details["OilFilter"] == "Mann W 712/52" && saved.Details["OilCapacity"] == "4.3", "fluids saved in place");

            ClickNow(ButtonNamed(w, Loc.T("Entity.Records.Parts.Template")));
            await Wait(300);
            List<EntityRecord> systems = VehicleSchema.Parts.Read(StoredVehicle(vehicleGarage, octavia));
            Check(systems.Select(s => s.Get("Name")).SequenceEqual(VehicleSchema.PartsTemplate.Select(k => Loc.T("Entity.Parts.Template." + k))),
                "Start with the usual systems: nine, named in the language");
            Check(Rows(w).Count == 9, "...a row each, nothing under them yet");

            await AddUnder(main, w, Loc.T("Entity.Parts.Template.Brakes"), d =>
            {
                Check(d.Title == Loc.F("Entity.Records.Parts.NewUnder", "Brakes"), "a new part's dialog says where it goes");
                BoxOf(EditorOf(d, "Name")).Text = "Front brakes";
                return Task.CompletedTask;
            });
            Check(RowNamed(w, "Front brakes") != null, "the system opened to show it");
            await AddUnder(main, w, "Front brakes", async d =>
            {
                BoxOf(EditorOf(d, "Name")).Text = "Brake disc";
                BoxOf(EditorOf(d, "Brand")).Text = "ATE";
                BoxOf(EditorOf(d, "OemNumber")).Text = "1K0615301AA";
                Button addSpec = ButtonNamed(EditorOf(d, "Specs").Element, Loc.T("Entity.Specs.Add"));
                ClickNow(addSpec);
                ClickNow(addSpec);
                ClickNow(addSpec); // one left empty: not stored
                await Wait(100);
                List<TextBox> specBoxes = Descendants(EditorOf(d, "Specs").Element).OfType<TextBox>().ToList();
                specBoxes[0].Text = "Size";
                specBoxes[1].Text = "M10x1.25x40";
                specBoxes[2].Text = "Torque";
                specBoxes[3].Text = "45 Nm";
                EditorOf(d, "Photos").AddPictures(new[] { photo });
            });

            saved = StoredVehicle(vehicleGarage, octavia);
            EntityRecord disc = PartNamed(saved, "Brake disc");
            Check(PartNamed(saved, "Brakes").Children.Single().Get("Name") == "Front brakes" && PartNamed(saved, "Front brakes").Children.Single() == disc
                  && disc.Specs.Count == 2 && disc.Specs[1].Value == "45 Nm" && disc.Pictures.Count == 1 && Stored(disc.Pictures[0])
                  && SameBytes(ImageStore.Items.PathOf(disc.Pictures[0]), photo), "Brakes › Front brakes › Brake disc: specs (no empty one) and its photo copied in");
            RecordRow row = RowOf(w, disc.Id);
            Check(row.IsOpen && Descendants(row).OfType<TextBlock>().Any(t => t.Text == "Brake disc")
                  && Descendants(row).OfType<TextBox>().Any(t => t.Text == "M10x1.25x40"), "the new part's row, open under the ones above it, its specs listed");
            RenderWindow(w, "vehicle-04-parts-en");

            ClickNow(ButtonNamed(row, Loc.T("Entity.Records.Parts.Out")));
            await Wait(300);
            Check(PartNamed(StoredVehicle(vehicleGarage, octavia), "Brakes").Children.Select(c => c.Get("Name")).SequenceEqual(new[] { "Front brakes", "Brake disc" })
                  && PartNamed(StoredVehicle(vehicleGarage, octavia), "Front brakes").Children == null, "Out: beside Front brakes, which holds nothing now");
            row = RowOf(w, disc.Id);
            Check(row.IsOpen && !ButtonNamed(row, Loc.T("Entity.Records.Parts.Down")).IsEnabled, "still open; the last one can't go down");
            ClickNow(ButtonNamed(row, Loc.T("Entity.Records.Parts.In")));
            await Wait(300);
            Check(PartNamed(StoredVehicle(vehicleGarage, octavia), "Front brakes").Children.Single().Id == disc.Id, "In: under Front brakes again");

            // Search over the whole vehicle: accents and case ignored, a match opens where it is.
            w.SelectPage("Overview");
            await Wait(200);
            TextBox search = Field<TextBox>(w, "SearchBox");
            search.Text = "m10";
            await Wait(200);
            List<Button> found = Matches(w);
            Check(found.Count == 1 && System.Windows.Automation.AutomationProperties.GetName(found[0]).Contains("Brakes › Front brakes › Brake disc: Size: M10x1.25x40"),
                "\"m10\": the spec, with where it is (" + string.Join("; ", found.Select(b => System.Windows.Automation.AutomationProperties.GetName(b))) + ")");
            ClickNow(found[0]);
            await Wait(300);
            Check(search.Text == "" && Field<string>(w, "page") == "Parts" && RowOf(w, disc.Id).IsOpen
                  && ((ListBoxItem)Field<ListBox>(w, "SectionList").SelectedItem)?.Tag as string == "Parts", "a click: Fluids & parts, the disc open");
            search.Text = "W 712";
            await Wait(200);
            Check(Matches(w).Any(b => System.Windows.Automation.AutomationProperties.GetName(b).Contains("Mann W 712/52")), "a fluid field is found too");
            search.Text = "fekbetet-nincs";
            await Wait(200);
            Check(PageTexts(w).Contains(Loc.F("Vehicle.Search.None", "fekbetet-nincs")) && Matches(w).Count == 0, "nothing found: said so");
            PressKey(search, Key.Escape);
            await Wait(200);
            Check(search.Text == "" && w.IsVisible, "Esc empties the search; the window stays");

            // A system with a part under it: one question naming how many go with it.
            w.SelectPage("Parts");
            await Wait(200);
            string interior = Loc.T("Entity.Parts.Template.Interior");
            await AddUnder(main, w, interior, d =>
            {
                BoxOf(EditorOf(d, "Name")).Text = "Üléshuzat";
                return Task.CompletedTask;
            });
            int boxes = selectBoxes.Count;
            ClickNow(ButtonNamed(RowNamed(w, interior), Loc.T("Common.Delete")));
            await Wait(400);
            Check(selectBoxes.Skip(boxes).Any(b => b.Contains(Loc.Plural("Entity.Records.Parts.DeleteWithChildren", 1, interior))),
                "deleting a system asks once, saying 1 part goes with it");
            Check(!RecordTree.Walk(VehicleSchema.Parts.Read(StoredVehicle(vehicleGarage, octavia))).Any(x => x.Record.Get("Name") == "Üléshuzat"),
                "...and both are gone");
        });
        Invoke(main, "NavigateHome");
    }

    // A part under `parent` through its row's Add a part under it (the dialog filled in by `fill`).
    static async Task AddUnder(MainWindow main, ItemWindow w, string parent, Func<EditDialog, Task> fill)
    {
        AnswerRecord(main, fill);
        ClickNow(ButtonNamed(RowNamed(w, parent), Loc.T("Entity.Records.Parts.AddUnder"))); // returns once the dialog is closed
        await Wait(300);
    }

    // ---- Service log and costs ------------------------------------------------------------------------

    static void VehicleServiceLegacy()
    {
        Console.WriteLine("The old Service log: shown as entries, kept in step");
        Entity car = Vehicle("Régi napló");
        car.Logs = new Dictionary<string, List<Dictionary<string, string>>>
        {
            ["Service"] = new List<Dictionary<string, string>>
            {
                new Dictionary<string, string> { ["Date"] = "2025-04-01", ["Work"] = "Olajcsere", ["Km"] = "100000", ["Cost"] = "30000", ["Where"] = "Bosch" },
                new Dictionary<string, string> { ["Date"] = "2026-04-01", ["Work"] = "Fékbetét" }
            }
        };
        List<EntityRecord> entries = VehicleService.Of(car);
        Check(entries.Count == 2 && entries[0].Get("Type") == "Service" && entries[0].Get("Odometer") == "100000" && entries[0].Get("Where") == "Bosch"
              && entries[0].Id == VehicleService.Of(car)[0].Id && car.Records == null, "old rows show as service entries (the same Ids every time), nothing written");

        entries[1].Set("Cost", "12000");
        entries.Add(Entry("Repair", new DateTime(2026, 5, 2), "8000", "Izzócsere"));
        VehicleSchema.ServiceLog.Write(car, entries);
        Check(car.Records["ServiceLog"].Count == 3 && car.Logs["Service"].Count == 3 && car.Logs["Service"][1]["Cost"] == "12000"
              && car.Logs["Service"][2]["Work"] == "Izzócsere", "stored: the entries, and the old log written in step for an older version");
        Check(VehicleService.Of(car).Count == 3, "read back: no row twice");

        car.Logs["Service"].Add(new Dictionary<string, string> { ["Date"] = "2026-06-01", ["Work"] = "Mosás" }); // an older version adds one
        Check(VehicleService.Of(car).Count == 4 && VehicleService.Of(car)[3].Get("Work") == "Mosás", "a row an older version added shows as an entry");

        List<EntityRecord> latest = VehicleService.Latest(VehicleService.Of(car)).ToList();
        Check(latest.Count == 2 && latest.Any(e => e.Get("Work") == "Mosás") && latest.Any(e => e.Get("Work") == "Izzócsere"),
            "the newest of each kind is the one whose next due counts");
    }

    static async Task VehicleServiceLog(MainWindow main)
    {
        Console.WriteLine("Service log: an entry fits a part, next due by date and by reading, filter, costs");
        CollectionView view = OpenCollectionView(main, vehicleGarage);
        await Wait(400);
        Entity before = StoredVehicle(vehicleGarage, octavia);
        EntityRecord disc = PartNamed(before, "Brake disc");
        int reading = before.OdometerKm!.Value;
        DateTime due = DateTime.Today.AddDays(200);

        await WithVehicle(main, view, octavia, async w =>
        {
            w.SelectPage("Service");
            await Wait(200);
            await AddRecord(main, w, VehicleSchema.ServiceLog, d =>
            {
                Check(Chosen(EditorOf(d, "Type")) == "Service" && ((DatePicker)EditorOf(d, "Date").Element).SelectedDate == DateTime.Today
                      && BoxOf(EditorOf(d, "Odometer")).Text == reading.ToString(), "a new entry: a service, today, at the current reading");
                BoxOf(EditorOf(d, "Work")).Text = "Fékcsere elöl";
                BoxOf(EditorOf(d, "Cost")).Text = "85000";
                CheckBox discBox = Descendants(EditorOf(d, "LinkedParts").Element).OfType<CheckBox>().Single(c => (string)c.Tag == disc.Id.ToString());
                Check((string)discBox.Content == "Brakes › Front brakes › Brake disc", "the parts to tick are named with the ones above them");
                discBox.IsChecked = true;
                ((DatePicker)EditorOf(d, "NextDueDate").Element).SelectedDate = due;
                BoxOf(EditorOf(d, "NextDueOdometer")).Text = (reading + 800).ToString();
                return Task.CompletedTask;
            });

            Entity saved = StoredVehicle(vehicleGarage, octavia);
            EntityRecord entry = saved.Records["ServiceLog"].Single(e => e.Get("Work") == "Fékcsere elöl");
            Check(entry.Get("LinkedParts") == disc.Id.ToString() && saved.Logs["Service"].Any(r => r.GetValueOrDefault("Work") == "Fékcsere elöl" && r["Km"] == reading.ToString()),
                "stored with its part, and as a row of the old log");
            EntityRecord fitted = PartNamed(saved, "Brake disc");
            Check(fitted.Get("InstalledDate") == Values.FromDate(DateTime.Today) && fitted.Get("InstalledOdometer") == reading.ToString(),
                "the part got the entry's date and reading as fitted");
            Reminder reminder = ReminderOf(entry.Id);
            Check(reminder?.NextFireTime == due.AddDays(-14).AddHours(9)
                  && VehicleReminders.ShownMessage(reminder, VehicleReminders.DeadlineIndex()) == Loc.F("Entity.Reminder.Due", "Service", due.ToString(Loc.T("Format.LongDate"), Loc.Culture)),
                "the next due date has its reminder, two weeks before: " + reminder?.NextFireTime);
            DistanceDue soon = EntityTypes.DistanceDues(saved).Single();
            Check(soon.Remaining == 800 && soon.Level == ExpiryLevel.Soon && ChipTexts(w).Contains(Loc.F("Entity.ExpiryLine", "Service", soon.Describe(saved))),
                "due in 800 by the odometer: a chip says so (" + soon.Describe(saved) + ")");
            Check(DashboardDataAggregator.Build(Profiles(main), DateTime.Now).Expiries.Any(x => x.Reading != null && x.ItemName == saved.Name),
                "...and Home lists it");

            // The part's row shows what fitted it (found through the search, which opens the parts above it).
            Field<TextBox>(w, "SearchBox").Text = "brake disc";
            await Wait(200);
            ClickNow(Matches(w).First(b => System.Windows.Automation.AutomationProperties.GetName(b).EndsWith("› Front brakes: Brake disc")));
            await Wait(300);
            RecordRow part = RowOf(w, disc.Id);
            Check(part.IsOpen && Descendants(part).OfType<TextBox>().Any(t => t.Text.Contains("Fékcsere elöl")), "the part's row, open, lists the entry in its history");

            // Another kind and a newer service without a next due: filter, costs, and the old next due no longer counts.
            foreach (EntityRecord added in new[] { Entry("Repair", DateTime.Today.AddDays(-400), "20000", "Izzó"), Entry("Fee", DateTime.Today, "15000", "Pályamatrica"),
                         Entry("Service", DateTime.Today.AddDays(1), "40000", "Olajcsere") })
            {
                Entity edited = JsonSerializer.Deserialize<Entity>(JsonSerializer.Serialize(StoredVehicle(vehicleGarage, octavia)));
                List<EntityRecord> list = VehicleSchema.ServiceLog.Read(edited);
                list.Add(added);
                VehicleSchema.ServiceLog.Write(edited, list);
                Invoke(view, "Replace", edited);
            }
            Check(ReminderOf(entry.Id) == null && EntityTypes.DistanceDues(StoredVehicle(vehicleGarage, octavia)).Count == 0,
                "a newer service: the older one's reminder and reading due are gone");
        });

        await WithVehicle(main, view, octavia, async w =>
        {
            w.SelectPage("Service");
            await Wait(200);
            string money(double amount) => Units.Format(amount, Units.Money, StoredVehicle(vehicleGarage, octavia));
            List<string> shown = Descendants(PageOf(w)).OfType<TextBox>().Select(t => t.Text).ToList();
            Check(shown.Contains(money(125000)) && shown.Contains(money(20000)) && shown.Contains(money(160000)),
                "costs as figures: per kind (services 125 000), all time (160 000): " + string.Join(", ", shown.Take(8)));
            int all = Rows(w).Count;
            RadioButton repairs = Descendants(w).OfType<RadioButton>().First(r => r.GroupName == "FilterServiceLog" && r.Content as string == Loc.T("Choice.Repair"));
            repairs.IsChecked = true;
            await Wait(200);
            Check(Rows(w).Count == 1 && all >= 4, "Repair chosen: only the repair (of " + all + ")");
            Check(Descendants(Rows(w).Single()).OfType<TextBlock>().Any(t => t.Text.EndsWith(" · " + Loc.T("Choice.Repair"))), "its row: date · kind");
            Descendants(w).OfType<RadioButton>().First(r => r.GroupName == "FilterServiceLog" && r.Content as string == Loc.T("Entity.Filter.All")).IsChecked = true;
            await Wait(200);
            List<DateTime> dates = Rows(w).Select(r => Values.ParseDate(r.Record.Get(VehicleService.DateKey)) ?? DateTime.MinValue).ToList();
            Check(Rows(w).Count == all && dates.SequenceEqual(dates.OrderByDescending(d => d)), "All: every entry again, newest first");
            Check(Rows(w).All(r => !r.IsOpen), "none open");
            // A row with something more to show (its other fields) opens on a click and closes on the next; one with nothing
            // more has nothing to open, and a click on it closes the one that is open: one open at a time.
            RecordRow withMore = Rows(w).First(r => Descendants(r).OfType<CellPanel>().Any());
            RecordRow plain = Rows(w).First(r => !Descendants(r).OfType<CellPanel>().Any());
            ClickNow(Descendants(withMore).OfType<Button>().First());
            await Wait(200);
            Check(withMore.IsOpen && Rows(w).Count(r => r.IsOpen) == 1, "a click opens a row with more to show");
            ClickNow(Descendants(plain).OfType<Button>().First());
            await Wait(200);
            Check(!plain.IsOpen && Rows(w).All(r => !r.IsOpen), "a row with nothing more has nothing to open, and the click closes the open one");
            ClickNow(Descendants(withMore).OfType<Button>().First());
            await Wait(200);
            ClickNow(Descendants(withMore).OfType<Button>().First());
            await Wait(200);
            Check(!withMore.IsOpen, "and the same row closes on the next click");
            RenderWindow(w, "vehicle-05-service-en");
        });
        Invoke(main, "NavigateHome");
    }

    // ---- The outline's moves -------------------------------------------------------------------------

    static void RecordTreeMoves()
    {
        Console.WriteLine("The outline's moves and its depth limit");
        EntityRecord c = Node("C"), b = Node("B", c), a = Node("A", b), d = Node("D");
        List<EntityRecord> list = new List<EntityRecord> { a, d };
        Check(RecordTree.Move(list, d.Id, -1) && list[0] == d && !RecordTree.Move(list, d.Id, -1), "Up, and not past the top");
        Check(RecordTree.Indent(list, a.Id) && list.Count == 1 && d.Children.Single() == a && RecordTree.Depth(list, c.Id) == 3, "In: under the one above, its own parts with it");
        Check(RecordTree.Outdent(list, a.Id) && list.SequenceEqual(new[] { d, a }) && d.Children == null, "Out: right after its parent, which holds nothing now");
        Check(!RecordTree.Outdent(list, a.Id) && RecordTree.CountUnder(a) == 2, "not out of the top; two under A");

        // Eight levels deep is the most: nothing goes deeper, and such a vehicle reads back.
        EntityRecord deepest = Node("L7"), chain = deepest;
        for (int level = 6; level >= 0; level--)
            chain = Node("L" + level, chain);
        EntityRecord beside = Node("Beside", Node("Under"));
        List<EntityRecord> deep = new List<EntityRecord> { chain, beside };
        Check(RecordTree.Depth(deep, deepest.Id) == RecordTree.MaxDepth - 1 && RecordTree.CanIndent(deep, beside.Id), "a chain of eight levels; a short one can go in beside it");
        EntityRecord seventh = RecordTree.Walk(deep).First(x => x.Record.Get("Name") == "L6").Record;
        seventh.Children.Add(Node("L7b", Node("L8")));
        Check(!RecordTree.CanIndent(deep, RecordTree.Walk(deep).First(x => x.Record.Get("Name") == "L7b").Record.Id),
            "one that would put a part on a ninth level can't go in");
        Entity car = Vehicle("Mély");
        VehicleSchema.Parts.Write(car, deep);
        string json = JsonSerializer.Serialize(new List<Entity> { car });
        Check(RecordTree.Walk(VehicleSchema.Parts.Read(JsonSerializer.Deserialize<List<Entity>>(json)[0])).Count() == 12,
            "the deepest tree allowed reads back from the file");
    }

    // ---- Languages, narrow size, remembered size -------------------------------------------------------

    static async Task VehicleLanguagesAndSize(MainWindow main)
    {
        Console.WriteLine("Three languages at a narrow size; the size remembered");
        CollectionView view = OpenCollectionView(main, vehicleGarage);
        await Wait(400);
        Entity full = StoredVehicle(vehicleGarage, octavia);
        full.Details["Make"] = "Škoda";
        full.Details["Model"] = "Octavia";
        full.Details["Edition"] = "III RS facelift";
        VehicleSchema.Documents.Write(full, new List<EntityRecord>
        {
            Document("Inspection", DateTime.Today.AddDays(12), 7),
            Document("Vignette", DateTime.Today.AddDays(-2), null, "Ausztria 2026"),
            Document("Road tax", DateTime.Today.AddDays(300), 30)
        });
        Invoke(view, "Replace", full);

        foreach (string language in new[] { Loc.English, Loc.Hungarian, Loc.SerbianLatin })
        {
            Loc.Switch(language);
            await WithVehicle(main, view, octavia, async w =>
            {
                w.Width = 660;
                w.Height = 560;
                await Wait(300);
                List<string> clipped = new List<string>();
                foreach (string tab in new[] { "Overview", "Specs", "Wheels", "Parts", "Documents", "Service" })
                {
                    w.SelectPage(tab);
                    await Wait(250);
                    if (tab == "Specs" || tab == "Parts")
                        foreach (FieldsSection card in Sections(w))
                            card.BeginEdit(); // one at a time: each new one stops the last
                    await Wait(150);
                    clipped.AddRange(Clipped(w).Select(c => tab + ": " + c));
                }
                // The service entry's dialog, every field in it.
                _ = main.Dispatcher.BeginInvoke(new Action(async () =>
                {
                    await Wait(500);
                    EditDialog dialog = Application.Current.Windows.OfType<EditDialog>().First();
                    clipped.AddRange(Clipped(dialog).Select(c => "service dialog: " + c));
                    dialog.Close();
                }), DispatcherPriority.Background);
                Invoke(w, "OpenRecord", VehicleSchema.ServiceLog, null, null);
                Check(clipped.Count == 0, language + ": nothing clipped at 660 px" + (clipped.Count > 0 ? ": " + string.Join("; ", clipped.Take(6)) : ""));
                Check((string)((ListBoxItem)Field<ListBox>(w, "SectionList").Items[0]).Content == Loc.T("Entity.Tab.Overview"), language + ": the pages in the language");
                if (language == Loc.Hungarian)
                {
                    w.SelectPage("Documents");
                    await Wait(250);
                    RenderWindow(w, "vehicle-02-documents-hu-narrow");
                }
                if (language == Loc.SerbianLatin)
                {
                    w.Width = 1100;
                    w.Height = 800;
                    w.SelectPage("Overview");
                    await Wait(400);
                    RenderWindow(w, "vehicle-03-overview-sr-wide");
                }
            });
        }
        Loc.Switch(Loc.English);

        AppSettings settings = AppSettings.Load();
        Check(settings.ItemWindowWidth == 1100 && settings.ItemWindowHeight == 800, "its size is remembered when it closes");
        await WithVehicle(main, view, octavia, w =>
        {
            Check(w.Width == Math.Min(1100, SystemParameters.WorkArea.Width - 40), "...and used the next time");
            return Task.CompletedTask;
        });
        Invoke(main, "NavigateHome");
    }

    // ---- Transfer --------------------------------------------------------------------------------------

    static async Task VehicleTransfer(MainWindow main)
    {
        Console.WriteLine("Transfer packages");
        Profile source = AddMemory(main, "Átadó", ProfileType.Collection);
        Profile target = AddMemory(main, "Átvevő", ProfileType.Collection);
        string scan = Path.Combine(vehicleWork, "forgalmi.jpg");
        File.WriteAllBytes(scan, new byte[] { 1, 2, 3, 4 });
        Attachment copied = await AttachmentStore.ImportAsync(scan, null, CancellationToken.None);
        Entity car = Vehicle("Fabia");
        car.Details = new Dictionary<string, string> { ["Vin"] = "TMBJJ7NE0F0123456" };
        EntityRecord registration = Document("Registration", DateTime.Today.AddDays(60), 14);
        registration.File = copied;
        VehicleSchema.Documents.Write(car, new List<EntityRecord> { registration });
        string partPhoto = Path.Combine(vehicleWork, "betét.png");
        ColorPicture(partPhoto, 5);
        EntityRecord pads = Node("Fékbetét");
        pads.Pictures = new List<string> { ImageStore.Items.Import(partPhoto) };
        pads.Specs = new List<SpecLine> { new SpecLine { Name = "Vastagság", Value = "17 mm" } };
        VehicleSchema.Parts.Write(car, new List<EntityRecord> { Node("Fékek", pads) });
        EntityRecord padsFitted = Entry("Service", DateTime.Today, "30000", "Betétcsere");
        padsFitted.Set(VehicleService.PartsKey, pads.Id.ToString());
        VehicleSchema.ServiceLog.Write(car, new List<EntityRecord> { padsFitted });
        EntityRecord winterSet = VehicleSchema.WheelSets.New(car);
        VehicleSchema.WheelSets.Write(car, new List<EntityRecord> { winterSet });
        VehicleSchema.WheelSets.SetCurrent(car, winterSet.Id);
        Entity old = Vehicle("Régi Lada");
        old.InspectionExpires = DateTime.Today.AddDays(30);
        EntityStore.SaveOrWarn(source, new List<Entity> { car, old });

        string package = Path.Combine(vehicleWork, "fabia" + TransferPackage.Extension);
        TransferPackage.ExportItem(car, package, null, CancellationToken.None);
        using (ZipArchive zip = ZipFile.OpenRead(package))
        {
            JsonObject manifest = (JsonObject)JsonNode.Parse(new StreamReader(zip.GetEntry("manifest.json").Open()).ReadToEnd());
            Check((int)manifest["Format"] == 3 && zip.GetEntry("files/documents/" + copied.StoredFileName) != null,
                "a vehicle with documents: format 3 (older versions refuse it), its document file in files/documents/");
            Check(zip.GetEntry("files/items/" + pads.Pictures[0]) != null, "...and its part's photo in files/items/");
        }
        string oldPackage = Path.Combine(vehicleWork, "lada" + TransferPackage.Extension);
        TransferPackage.ExportItem(old, oldPackage, null, CancellationToken.None);
        using (ZipArchive zip = ZipFile.OpenRead(oldPackage))
            Check((int)JsonNode.Parse(new StreamReader(zip.GetEntry("manifest.json").Open()).ReadToEnd())["Format"] == 2,
                "a vehicle with only the old fields: format 2 as before");
        string collection = Path.Combine(vehicleWork, "atado" + TransferPackage.Extension);
        TransferPackage.ExportCollection(source, new List<MemoryTab>(), EntityStore.Load(source), collection, null, CancellationToken.None);
        using (ZipArchive zip = ZipFile.OpenRead(collection))
            Check((int)JsonNode.Parse(new StreamReader(zip.GetEntry("manifest.json").Open()).ReadToEnd())["Format"] == 3, "a collection holding it: format 3");

        Entity imported;
        using (StagedImport staged = TransferPackage.Stage(TransferPackage.Open(package), null, CancellationToken.None))
        {
            TransferPackage.CommitItem(staged, target, Profiles(main), "#3498DB", "x");
            imported = staged.Added.Single();
            Check(VehicleReminders.Follow(null, imported) == null, "reminders made for what came in");
        }
        EntityRecord copy = VehicleDocuments.Of(imported).Single();
        Check(imported.Id != car.Id && copy.Id != registration.Id && copy.File.StoredFileName != copied.StoredFileName
              && File.ReadAllBytes(AttachmentStore.PathOf(copy.File.StoredFileName)).SequenceEqual(new byte[] { 1, 2, 3, 4 })
              && copy.File.DisplayName == "forgalmi.jpg" && imported.Details["Vin"] == "TMBJJ7NE0F0123456",
            "imported: new Ids for the item and its document, the file under a new name, the fields as they were");
        Check(ReminderOf(copy.Id)?.NextFireTime == DateTime.Today.AddDays(60 - 14).AddHours(9) && ReminderOf(registration.Id) == null,
            "the imported document has its reminder; the source's (never set on this PC) none");
        EntityRecord padsCopy = PartNamed(imported, "Fékbetét");
        Check(padsCopy.Id != pads.Id && PartNamed(imported, "Fékek").Id != VehicleSchema.Parts.Read(car)[0].Id && padsCopy.Pictures.Single() != pads.Pictures[0]
              && SameBytes(ImageStore.Items.PathOf(padsCopy.Pictures[0]), partPhoto) && padsCopy.Specs.Single().Value == "17 mm",
            "the part under its system: new Ids at every level, its photo under a new name, its specs");
        Check(VehicleSchema.ServiceLog.Read(imported).Single().Get(VehicleService.PartsKey) == padsCopy.Id.ToString()
              && imported.Details[VehicleSchema.MountedWheelSetKey] == VehicleSchema.WheelSets.Read(imported).Single().Id.ToString()
              && VehicleSchema.WheelSets.Read(imported).Single().Id != winterSet.Id, "what points at a record follows it: the service's part, the mounted wheel set");
        Check(EntityStore.Load(target).Single().InspectionExpires == null && EntityStore.Load(target).Single().Name == "Fabia", "stored in the target");
    }

    // ---- Exports, search ---------------------------------------------------------------------------------

    static void VehicleExportsAndSearch(MainWindow main)
    {
        Console.WriteLine("Exports and search");
        Entity car = StoredVehicle(vehicleGarage, octavia);
        foreach (ExportFormat format in new[] { ExportFormat.Pdf, ExportFormat.Docx, ExportFormat.Excel })
        {
            string path = Path.Combine(vehicleWork, "octavia." + (format == ExportFormat.Excel ? "xlsx" : format.ToString().ToLowerInvariant()));
            ExportService.ExportEntity(car, path, format);
            Check(File.Exists(path) && new FileInfo(path).Length > 1000, "exported as " + format);
        }
        using (ClosedXML.Excel.XLWorkbook book = new ClosedXML.Excel.XLWorkbook(Path.Combine(vehicleWork, "octavia.xlsx")))
        {
            Check(book.Worksheet(VehicleSchema.Parts.Label).CellsUsed().Any(c => c.GetString() == "Brakes › Front brakes › Brake disc"),
                "Excel: a Parts sheet, a nested part named with the ones above it");
            ClosedXML.Excel.IXLWorksheet documents = book.Worksheet(VehicleSchema.Documents.Label);
            Check(documents.CellsUsed().Any(c => c.DataType == ClosedXML.Excel.XLDataType.DateTime && c.GetDateTime() == DateTime.Today.AddDays(12)),
                "Excel: a Documents sheet, its dates as date cells");
        }
        FieldDefinition year = VehicleSchema.Identity.Fields.First(f => f.Key == "Year");
        Check(year.Show("2017", car) == "2017" && VehicleSchema.Ownership.Fields.First(f => f.Key == "PurchasePrice").Show("4500000", car) == "4,500,000 " + Currency.Symbol,
            "a year is written as it is, an amount grouped with the currency");
        List<(string Label, string Value)> lines = EntityTypes.Describe(car);
        Check(lines.Any(l => l.Label == VehicleSchema.Parts.Label && l.Value.Contains("Brakes › Front brakes › Brake disc | Brand: ATE") && l.Value.Contains("Size: M10x1.25x40")),
            "parts are listed with the ones above them, their specs too");
        Check(lines.Any(l => l.Label == Loc.T("Entity.Field.Power") && l.Value == "110 kW (150 hp)")
              && lines.Any(l => l.Label == VehicleSchema.Documents.Label && l.Value.Contains("Vignette · Ausztria 2026")), "PDF and Word list the fields and documents (Describe)");
        List<SearchResult> index = SearchIndexBuilder.Build(Profiles(main));
        Check(SearchIndexBuilder.Filter(index, "ausztria", 10).Any(r => r.ItemId == octavia)
              && SearchIndexBuilder.Filter(index, "III RS", 10).Any(r => r.ItemId == octavia), "search finds a document's name and the edition");
    }

    // ---- Deleting ------------------------------------------------------------------------------------------

    static async Task VehicleDeletes(MainWindow main)
    {
        Console.WriteLine("Deleting a vehicle, a selection, a memory: files and reminders go");
        Profile garage = AddMemory(main, "Törlendő", ProfileType.Collection);
        string sharedPhoto = Path.Combine(vehicleWork, "közös.png");
        ColorPicture(sharedPhoto, 1);
        string shared = ImageStore.Items.Import(sharedPhoto); // the same part photo in the first and the last vehicle
        List<Entity> cars = new List<Entity>();
        for (int i = 0; i < 4; i++)
        {
            Entity car = Vehicle("Autó " + i, i);
            string scan = Path.Combine(vehicleWork, "scan" + i + ".pdf");
            File.WriteAllText(scan, "scan " + i);
            EntityRecord insurance = Document("Insurance", DateTime.Today.AddDays(100 + i), 30);
            insurance.File = await AttachmentStore.ImportAsync(scan, null, CancellationToken.None);
            VehicleSchema.Documents.Write(car, new List<EntityRecord> { insurance });
            if (i == 0 || i == 3)
            {
                EntityRecord bulb = Node("Izzó");
                bulb.Pictures = new List<string> { shared };
                VehicleSchema.Parts.Write(car, new List<EntityRecord> { bulb });
            }
            cars.Add(car);
        }
        EntityStore.SaveOrWarn(garage, cars);
        foreach (Entity car in cars)
            VehicleReminders.Follow(null, car);
        Guid DocumentOf(Entity car) => VehicleDocuments.Of(car).Single().Id;
        string FileOf(Entity car) => AttachmentStore.PathOf(VehicleDocuments.Of(car).Single().File.StoredFileName);
        Check(cars.All(c => ReminderOf(DocumentOf(c)) != null), "four vehicles with a reminder each");

        CollectionView view = OpenCollectionView(main, garage);
        await Wait(400);
        Check((bool)view.GetType().GetMethod("DeleteEntity", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(view, new object[] { cars[0] }),
            "one deleted (asked first)");
        Check(ReminderOf(DocumentOf(cars[0])) == null && File.Exists(FileOf(cars[0])) && ReminderOf(DocumentOf(cars[1])) != null,
            "its reminder is gone, the others' stay; its file is kept while the delete can be undone");
        UndoService.EndAll();
        Check(!File.Exists(FileOf(cars[0])), "then its file is gone");
        Check(Stored(shared), "a part photo another vehicle's part still shows stays");

        List<Entity> loaded = Field<List<Entity>>(view, "entities");
        Invoke(view, "RemoveEntities", new List<Entity> { loaded.First(e => e.Id == cars[1].Id), loaded.First(e => e.Id == cars[2].Id) });
        UndoService.EndAll();
        Check(ReminderOf(DocumentOf(cars[1])) == null && ReminderOf(DocumentOf(cars[2])) == null && !File.Exists(FileOf(cars[2]))
              && ReminderOf(DocumentOf(cars[3])) != null, "a selection of two: both reminders and files gone, the last one kept");

        Invoke(main, "NavigateHome");
        Invoke(main, "RemoveMemories", new List<Profile> { garage });
        Check(ReminderOf(DocumentOf(cars[3])) == null && File.Exists(FileOf(cars[3])), "the memory deleted: its last vehicle's reminder too, its file kept while it can be undone");
        UndoService.EndAll();
        Check(!File.Exists(FileOf(cars[3])), "then the file");
        Check(!Stored(shared), "...and the part photo nobody shows now");
        await Wait(200);
    }
}
