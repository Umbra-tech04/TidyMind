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
// dates show as documents; an older version keeps the new fields and its date edits are picked up. The window: header,
// tabs, editing a section in place (numbers checked, kW and hp), documents with a file and their reminders (set, moved,
// removed, retitled, after a delete of the document, the vehicle, a selection and the memory), the odometer update,
// Esc, picture keys, its remembered size, three languages at a narrow size without clipped text. Transfer packages
// (format 3, document files, new Ids, reminders on import), exports, search and Home. A few screenshots.
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
            await VehicleLanguagesAndSize(main);
            await VehicleTransfer(main);
            VehicleExportsAndSearch(main);
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

    static Entity Vehicle(string name, int order = 0) => new Entity
    {
        Id = Guid.NewGuid(), EntityType = EntityTypes.Vehicle, Name = name, Order = order, CreatedDate = DateTime.Now
    };

    static EntityRecord Document(string type, DateTime? validUntil, int? remindDays, string name = null)
    {
        EntityRecord document = new EntityRecord { Id = Guid.NewGuid() };
        document.Set(VehicleDocuments.TypeKey, type);
        document.Set(VehicleDocuments.NameKey, name);
        document.Set(VehicleDocuments.ValidUntilKey, Values.FromDate(validUntil));
        document.Set(VehicleDocuments.RemindKey, remindDays?.ToString());
        return document;
    }

    // Runs `act` on the vehicle window once it is up (the caller's ShowDialog is open meanwhile), then closes it.
    static void DriveVehicle(MainWindow main, Func<VehicleWindow, Task> act)
    {
        _ = main.Dispatcher.BeginInvoke(new Action(async () =>
        {
            VehicleWindow window = null;
            for (int i = 0; i < 50 && window == null; i++)
            {
                await Wait(100);
                window = Application.Current.Windows.OfType<VehicleWindow>().FirstOrDefault();
            }
            try
            {
                await Wait(300);
                await act(window);
            }
            catch (Exception e)
            {
                Check(false, "the vehicle window driver threw: " + e);
            }
            window?.Close();
        }), DispatcherPriority.Background);
    }

    // A vehicle with something on every tab, for pages and screenshots (not stored).
    static Entity SampleVehicle()
    {
        Entity car = Vehicle("Škoda Octavia – „Đurđa”");
        car.VehicleKind = "Car";
        car.Plate = "NS-123-ĆŽ";
        car.Year = 2017;
        car.OdometerKm = 152300;
        car.Details = new Dictionary<string, string> { ["Make"] = "Škoda", ["Model"] = "Octavia", ["Edition"] = "III RS", ["Fuel"] = "Diesel", ["Power"] = "135", ["Gearbox"] = "Automatic" };
        VehicleSchema.Documents.Write(car, new List<EntityRecord>
        {
            Document("Inspection", DateTime.Today.AddDays(12), 7),
            Document("Vignette", DateTime.Today.AddDays(-2), null, "Ausztria"),
            Document("Insurance", DateTime.Today.AddDays(200), 30, "Allianz")
        });
        car.Logs = new Dictionary<string, List<Dictionary<string, string>>>
        {
            ["Service"] = new List<Dictionary<string, string>> { new Dictionary<string, string> { ["Date"] = "2026-09-01", ["Work"] = "Olajcsere", ["Km"] = "150000" } }
        };
        return car;
    }

    // Opens the item from the collection page (its window, as a click on the card does) and drives it.
    static async Task WithVehicle(MainWindow main, CollectionView view, Guid id, Func<VehicleWindow, Task> act)
    {
        DriveVehicle(main, act);
        view.Reveal(id); // returns once the window is closed
        await Wait(200);
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

    static List<UIElement> Cards(VehicleWindow window) => Field<CellPanel>(window, "Cards").Children.Cast<UIElement>().ToList();

    static SectionCard CardTitled(VehicleWindow window, string title) =>
        Cards(window).OfType<SectionCard>().First(c => Descendants(c).OfType<TextBlock>().First().Text == title);

    static List<FieldEditor> Editors(SectionCard card) => Field<List<FieldEditor>>(card, "editors");

    static FieldEditor EditorOf(SectionCard card, string key) => Editors(card).First(e => e.Field.Key == key);

    static void Choose(FieldEditor editor, string value) =>
        ((ComboBox)editor.Element).SelectedItem = ((ComboBox)editor.Element).Items.Cast<ComboBoxItem>().First(i => (string)i.Tag == value);

    static TextBox BoxOf(FieldEditor editor, int index = 0) =>
        editor.Element as TextBox ?? ((Panel)editor.Element).Children.OfType<TextBox>().ElementAt(index);

    static async Task SaveCard(VehicleCard card)
    {
        ClickNow(Descendants(card).OfType<Button>().First(b => b.Content as string == Loc.T("Common.Save")));
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
        Check(VehicleReminders.FireTime(Document("Insurance", new DateTime(2026, 12, 1), 30), now) == new DateTime(2026, 11, 1, 9, 0, 0),
            "30 days before Dec 1: Nov 1, 09:00");
        Check(VehicleReminders.FireTime(Document("Insurance", new DateTime(2026, 10, 25), 30), now) == new DateTime(2026, 10, 20, 9, 0, 0),
            "that day already past (10:00): the next 09:00, tomorrow");
        Check(VehicleReminders.FireTime(Document("Insurance", new DateTime(2026, 10, 25), 30), now.AddHours(-2)) == new DateTime(2026, 10, 19, 9, 0, 0),
            "...or today at 09:00 when it's still before");
        Check(VehicleReminders.FireTime(Document("Insurance", now.Date, 7), now) == null, "runs out today, after 09:00: none (tomorrow is too late)");
        Check(VehicleReminders.FireTime(Document("Insurance", now.Date.AddDays(-1), 7), now) == null, "already run out: none");
        Check(VehicleReminders.FireTime(Document("Insurance", now.Date.AddDays(90), null), now) == null
              && VehicleReminders.FireTime(Document("Insurance", null, 30), now) == null, "no days or no date: none");
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
            Check(Field<TextBox>(w, "NameText").Text == "Golf" && Field<TextBox>(w, "PlateText").Text == "ABC-123"
                  && Field<TextBlock>(w, "OdometerText").Text == Units.Format(120500, Units.Distance, golf), "the header: name, plate, odometer");
            Check(Field<WrapPanel>(w, "Badges").Children.Count == 2, "two deadline badges");
            Check(Field<WrapPanel>(w, "TabsPanel").Children.Count == 4, "four tabs: Overview, Specs, Documents, Service & costs");
            foreach (string tab in new[] { "Specs", "Documents", "Service", "Overview" })
            {
                w.SelectTab(tab);
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
            PictureBrowser gallery = Field<PictureBrowser>(w, "gallery");
            await Wait(500);
            PressKey(w, Key.Right);
            Check(gallery.Selected == 1, "Right shows the next picture");
            PressKey(w, Key.Left);
            Check(gallery.Selected == 0, "Left goes back");

            w.SelectTab("Specs");
            await Wait(200);
            Check(Cards(w).OfType<SectionCard>().Count() == 5, "Specs: identity, engine, body, ownership, odometer");
            SectionCard engine = CardTitled(w, VehicleSchema.Engine.Label);
            Check(Descendants(engine).OfType<TextBlock>().Any(t => t.Text == Loc.T("Entity.Card.Empty")), "an empty section says so");
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
            await SaveCard(engine);
            Check(selectBoxes.Count == boxes + 1 && StoredVehicle(vehicleGarage, octavia).Details == null, "a number field with words: said, nothing saved");
            BoxOf(EditorOf(engine, "Displacement")).Text = "1 968";
            await SaveCard(engine);
            Entity saved = StoredVehicle(vehicleGarage, octavia);
            Check(saved.Details?["Power"] == "110" && saved.Details["Displacement"] == "1968" && saved.Details["Fuel"] == "Diesel"
                  && saved.Plate == "SU-123-AB" && EntityImages.Names(saved).SequenceEqual(pictures), "saved at once: invariant numbers, the choice's English value, the rest kept");
            engine = CardTitled(w, VehicleSchema.Engine.Label);
            Check(Descendants(engine).OfType<TextBox>().Any(t => t.Text == "110 kW (150 hp)"), "shown as 110 kW (150 hp)");

            SectionCard ownership = CardTitled(w, VehicleSchema.Ownership.Label);
            ownership.BeginEdit();
            await Wait(200);
            WrapPanel checks = Descendants(EditorOf(ownership, "Equipment").Element).OfType<WrapPanel>().First();
            ((CheckBox)checks.Children[0]).IsChecked = true;
            TextBox own = Descendants(EditorOf(ownership, "Equipment").Element).OfType<TextBox>().First();
            own.Text = "Állófűtés";
            ClickNow(Descendants(EditorOf(ownership, "Equipment").Element).OfType<Button>().First());
            ((CheckBox)EditorOf(ownership, "ServiceBook").Element).IsChecked = true;
            BoxOf(EditorOf(ownership, "PurchasePrice")).Text = "4500000";
            await SaveCard(ownership);
            saved = StoredVehicle(vehicleGarage, octavia);
            Check(saved.Details["Equipment"] == "Air conditioning\nÁllófűtés" && saved.Details["ServiceBook"] == "true" && saved.Details["PurchasePrice"] == "4500000",
                "equipment: an offered one and the user's own, one per line; the flag; the price as a plain number");

            // Esc while editing stops the edit (asking first: the watcher says Yes), and a second Esc closes.
            CardTitled(w, VehicleSchema.Body.Label).BeginEdit();
            await Wait(200);
            BoxOf(EditorOf(CardTitled(w, VehicleSchema.Body.Label), "Doors")).Text = "5";
            boxes = selectBoxes.Count;
            PressKey(w, Key.Escape);
            await Wait(300);
            Check(selectBoxes.Count == boxes + 1 && !CardTitled(w, VehicleSchema.Body.Label).IsEditing && w.IsVisible,
                "Esc during an edit with changes asks, then stops it; the window stays");
            RenderWindow(w, "vehicle-01-specs-en");
        });
        Check(Application.Current.Windows.OfType<VehicleWindow>().Count() == 0, "closed");
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
            w.SelectTab("Documents");
            await Wait(200);
            Check(Cards(w).OfType<VehicleCard>().Any(c => Descendants(c).OfType<TextBlock>().Any(t => t.Text == Loc.T("Entity.Records.Documents.Empty"))),
                "no documents yet: the list says what it is for");
            Invoke(w, "StartAdd", VehicleSchema.Documents);
            await Wait(300);
            SectionCard card = Cards(w).OfType<SectionCard>().Single(c => c.IsEditing);
            Check((string)((ComboBoxItem)((ComboBox)EditorOf(card, "Type").Element).SelectedItem).Tag == "Insurance"
                  && (string)((ComboBoxItem)((ComboBox)EditorOf(card, "RemindDays").Element).SelectedItem).Tag == "30", "a new document: Insurance, 30 days before");
            BoxOf(EditorOf(card, "Name")).Text = "Allianz casco";
            ((DatePicker)EditorOf(card, "ValidUntil").Element).SelectedDate = due;
            typeof(FieldEditor).GetProperty("PickedFile").GetSetMethod(true).Invoke(EditorOf(card, "File"), new object[] { scan });
            await SaveCard(card);
            await Wait(400);
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
        Check(VehicleReminders.ShownMessage(reminder, VehicleReminders.DocumentIndex())
              == Loc.F("Entity.Reminder.Message", "Insurance · Allianz casco", due.ToString(Loc.T("Format.LongDate"), Loc.Culture)),
            "what it says is made when shown: " + VehicleReminders.ShownMessage(reminder, VehicleReminders.DocumentIndex()));
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
            w.SelectTab("Documents");
            await Wait(200);
            SectionCard card = Cards(w).OfType<SectionCard>().Single();
            Check(Descendants(card).OfType<Button>().Any(b => b.Content as string == "kötvény.pdf"), "the document's card links its file");
            card.Delete();
            await Wait(400);
        });
        Check(VehicleDocuments.Of(StoredVehicle(vehicleGarage, octavia)).Count == 0 && !File.Exists(stored) && ReminderOf(documentId) == null,
            "deleted (asked first): the file and the reminder are gone");
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
            Check(Field<TextBlock>(w, "OdometerText").Text == Units.Format(153450, Units.Distance, saved), "the header shows it");

            int boxes = selectBoxes.Count;
            AnswerPrompt(main, "150000");
            Invoke(w, "UpdateOdometer");
            await Wait(300);
            Check(selectBoxes.Count == boxes + 1 && StoredVehicle(vehicleGarage, octavia).OdometerKm == 150000, "lower than before: asked (Yes here), then saved");

            w.SelectTab("Specs");
            await Wait(200);
            SectionCard odometer = CardTitled(w, VehicleSchema.OdometerSection.Label);
            odometer.BeginEdit();
            await Wait(200);
            Choose(EditorOf(odometer, VehicleSchema.OdometerUnitKey), Units.Mi);
            await SaveCard(odometer);
            Check(Field<TextBlock>(w, "OdometerText").Text == "150,000 mi", "miles chosen: every distance says mi (" + Field<TextBlock>(w, "OdometerText").Text + ")");
        });
        Invoke(main, "NavigateHome");
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
                foreach (string tab in new[] { "Overview", "Specs", "Documents", "Service" })
                {
                    w.SelectTab(tab);
                    await Wait(250);
                    if (tab == "Specs")
                        foreach (SectionCard card in Cards(w).OfType<SectionCard>())
                            card.BeginEdit(); // one at a time: each new one stops the last
                    await Wait(150);
                    clipped.AddRange(Clipped(w).Select(c => tab + ": " + c));
                }
                Check(clipped.Count == 0, language + ": nothing clipped at 660 px" + (clipped.Count > 0 ? ": " + string.Join("; ", clipped.Take(6)) : ""));
                Check(((RadioButton)Field<WrapPanel>(w, "TabsPanel").Children[0]).Content as string == Loc.T("Entity.Tab.Overview"), language + ": the tabs in the language");
                if (language == Loc.Hungarian)
                {
                    w.SelectTab("Documents");
                    await Wait(250);
                    RenderWindow(w, "vehicle-02-documents-hu-narrow");
                }
                if (language == Loc.SerbianLatin)
                {
                    w.Width = 1100;
                    w.Height = 800;
                    w.SelectTab("Overview");
                    await Wait(400);
                    RenderWindow(w, "vehicle-03-overview-sr-wide");
                }
            });
        }
        Loc.Switch(Loc.English);

        AppSettings settings = AppSettings.Load();
        Check(settings.VehicleWindowWidth == 1100 && settings.VehicleWindowHeight == 800, "its size is remembered when it closes");
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
            ClosedXML.Excel.IXLWorksheet documents = book.Worksheet(VehicleSchema.Documents.Label);
            Check(documents.CellsUsed().Any(c => c.DataType == ClosedXML.Excel.XLDataType.DateTime && c.GetDateTime() == DateTime.Today.AddDays(12)),
                "Excel: a Documents sheet, its dates as date cells");
        }
        FieldDefinition year = VehicleSchema.Identity.Fields.First(f => f.Key == "Year");
        Check(year.Show("2017", car) == "2017" && VehicleSchema.Ownership.Fields.First(f => f.Key == "PurchasePrice").Show("4500000", car) == "4,500,000 " + Currency.Symbol,
            "a year is written as it is, an amount grouped with the currency");
        List<(string Label, string Value)> lines = EntityTypes.Describe(car);
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
        List<Entity> cars = new List<Entity>();
        for (int i = 0; i < 4; i++)
        {
            Entity car = Vehicle("Autó " + i, i);
            string scan = Path.Combine(vehicleWork, "scan" + i + ".pdf");
            File.WriteAllText(scan, "scan " + i);
            EntityRecord insurance = Document("Insurance", DateTime.Today.AddDays(100 + i), 30);
            insurance.File = await AttachmentStore.ImportAsync(scan, null, CancellationToken.None);
            VehicleSchema.Documents.Write(car, new List<EntityRecord> { insurance });
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
        Check(ReminderOf(DocumentOf(cars[0])) == null && !File.Exists(FileOf(cars[0])) && ReminderOf(DocumentOf(cars[1])) != null,
            "its reminder and file are gone, the others' stay");

        List<Entity> loaded = Field<List<Entity>>(view, "entities");
        Invoke(view, "RemoveEntities", new List<Entity> { loaded.First(e => e.Id == cars[1].Id), loaded.First(e => e.Id == cars[2].Id) });
        Check(ReminderOf(DocumentOf(cars[1])) == null && ReminderOf(DocumentOf(cars[2])) == null && !File.Exists(FileOf(cars[2]))
              && ReminderOf(DocumentOf(cars[3])) != null, "a selection of two: both reminders and files gone, the last one kept");

        Invoke(main, "NavigateHome");
        Invoke(main, "RemoveMemories", new List<Profile> { garage });
        Check(ReminderOf(DocumentOf(cars[3])) == null && !File.Exists(FileOf(cars[3])), "the memory deleted: its last vehicle's reminder and file too");
        await Wait(200);
    }
}
