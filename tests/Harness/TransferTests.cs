using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TidyMind;

// Harness transfer <data> <shotsFolder>: .tidymind packages (TransferPackage, TransferUI). A project and a collection
// (pictures, tabs, logs, fields from a newer version) out to a package and back in as new copies in another memory,
// compared field by field and byte by byte; twice and with a name clash; special characters and Hungarian/Serbian
// text; a large attachment with progress; every bad package (zip slip, absolute path, too many or too large entries,
// corrupt, truncated, empty, foreign ZIP, not a ZIP, broken JSON, newer format) refused with nothing added; a newer
// app in the same format accepted with its unknown fields kept; a failure part-way adding nothing; reminders
// untouched; the export actions and the import preview in three languages, with screenshots.
static partial class Program
{
    static string transferWork;

    static async Task TransferScenarios(MainWindow main)
    {
        await Wait(1200);
        GuidedTour.Current?.Stop(TourEnd.Skipped);
        StartSelectWatcher(); // answers and logs message boxes
        transferWork = Path.Combine(Path.GetDirectoryName(AppPaths.DataFolder)!, "transfer-work");
        if (Directory.Exists(transferWork))
            Directory.Delete(transferWork, true);
        Directory.CreateDirectory(transferWork);
        string remindersBefore = File.Exists(AppPaths.Data("reminders.json")) ? File.ReadAllText(AppPaths.Data("reminders.json")) : null;
        int tasksBefore = ReminderTaskNames().Count;

        await ProjectRoundTrip(main);
        CollectionRoundTrip(main);
        await ItemTransfer(main);
        LargeAttachment(main);
        BadPackages(main);
        UntrustedContent(main);
        Versions(main);
        FailurePartWay(main);
        await TransferUi(main);

        string remindersAfter = File.Exists(AppPaths.Data("reminders.json")) ? File.ReadAllText(AppPaths.Data("reminders.json")) : null;
        Check(remindersAfter == remindersBefore && ReminderTaskNames().Count == tasksBefore,
            "reminders: none in a package, none made or changed by an import (reminders.json and the scheduled tasks as before)");
        Check(!Directory.EnumerateDirectories(AppPaths.DataFolder, "Import-*").Any(), "no staging folder left in the data folder");
        Console.WriteLine("message boxes: " + selectBoxes.Count);
        lock (selectBoxes)
            foreach (string box in selectBoxes)
                Console.WriteLine("  " + box);
    }

    // ---- A project ------------------------------------------------------------------

    const string OddName = "Konyha – felújítás / Q1: „ÁÉŐŰ” đČćŠž <*?> 🙂";

    static Project SampleTransferProject(string name)
    {
        string a = StoreFile(AttachmentStore.Folder, "számla.pdf");
        string b = StoreFile(AttachmentStore.Folder, "terv.docx");
        File.WriteAllText(b, "Đorđe čašu ćevapa — second file");
        string card = Path.Combine(ImageStore.Cards.Folder, Guid.NewGuid().ToString("N") + ".png");
        Directory.CreateDirectory(ImageStore.Cards.Folder);
        SavePicture(card);
        return new Project
        {
            Id = Guid.NewGuid(),
            Name = name,
            Description = "Leírás: csempe, festék. Opis: pločice, boja.",
            Tasks = new List<TaskItem>
            {
                new TaskItem { Title = "Csempe kiválasztása", IsDone = true },
                new TaskItem { Title = "Izabrati boju (đ, č, ć, š, ž)", IsDone = false,
                    ExtensionData = new Dictionary<string, JsonElement> { ["Priority"] = JsonDocument.Parse("2").RootElement } },
                new TaskItem { Title = "Third", IsDone = false }
            },
            NotePages = new List<NotePage>
            {
                new NotePage { Body = FormattedText() },
                new NotePage { Body = "" },
                new NotePage { Body = "Druga stranica", ExtensionData = new Dictionary<string, JsonElement> { ["Pinned"] = JsonDocument.Parse("true").RootElement } }
            },
            Attachments = new List<Attachment>
            {
                new Attachment { Id = Guid.NewGuid(), DisplayName = "Számla 2026.pdf", StoredFileName = Path.GetFileName(a), SizeBytes = new FileInfo(a).Length, AddedDate = new DateTime(2026, 3, 1) },
                new Attachment { Id = Guid.NewGuid(), DisplayName = "Terv – végleges.docx", StoredFileName = Path.GetFileName(b), SizeBytes = new FileInfo(b).Length, AddedDate = new DateTime(2026, 4, 2) },
                new Attachment { Id = Guid.NewGuid(), DisplayName = "Gone.txt", StoredFileName = "0123456789abcdef0123456789abcdef.txt", SizeBytes = 5, AddedDate = new DateTime(2026, 5, 3) }
            },
            CardBackgroundType = BackgroundFill.Image,
            CardBackgroundImagePath = Path.GetFileName(card),
            LastModified = new DateTime(2026, 6, 1, 10, 0, 0),
            ExtensionData = new Dictionary<string, JsonElement> { ["FutureField"] = JsonDocument.Parse("{\"x\":[1,2]}").RootElement }
        };
    }

    // A page as the editor stores it: formatted XAML with bold, italic, underline, a color and a list.
    static string FormattedText()
    {
        System.Windows.Documents.FlowDocument document = new System.Windows.Documents.FlowDocument();
        System.Windows.Documents.Paragraph paragraph = new System.Windows.Documents.Paragraph();
        paragraph.Inlines.Add(new System.Windows.Documents.Bold(new System.Windows.Documents.Run("Első oldal: ")));
        paragraph.Inlines.Add(new System.Windows.Documents.Italic(new System.Windows.Documents.Run("dőlt, ")));
        paragraph.Inlines.Add(new System.Windows.Documents.Underline(new System.Windows.Documents.Run("aláhúzott, ")));
        paragraph.Inlines.Add(new System.Windows.Documents.Run("piros đČć") { Foreground = System.Windows.Media.Brushes.Firebrick });
        document.Blocks.Add(paragraph);
        System.Windows.Documents.List list = new System.Windows.Documents.List();
        list.ListItems.Add(new System.Windows.Documents.ListItem(new System.Windows.Documents.Paragraph(new System.Windows.Documents.Run("pont"))));
        document.Blocks.Add(list);
        return RichTextHelper.ToStorableString(document);
    }

    static async Task ProjectRoundTrip(MainWindow main)
    {
        Console.WriteLine("A project: out and back in, into another memory");
        Profile home = AddProjectMemory(main, "Otthon");
        Profile work = AddProjectMemory(main, "Munka");
        Project original = SampleTransferProject(OddName);
        ProjectStore.SaveOrWarn(home, new List<Project> { original });

        string package = Path.Combine(transferWork, ExportMenu.SafeFileName(original.Name) + TransferPackage.Extension);
        TransferPackage.ExportProject(original, TransferPackage.WrittenPages(original), package, null, CancellationToken.None);
        Check(File.Exists(package) && Path.GetFileName(package).EndsWith(".tidymind"), "exported to " + Path.GetFileName(package));
        Check(!Directory.EnumerateFiles(transferWork, "*.tmp-*").Any(), "no temporary file left next to it");
        using (ZipArchive zip = ZipFile.OpenRead(package))
        {
            List<string> names = zip.Entries.Select(e => e.FullName).OrderBy(n => n).ToList();
            Check(names.Count == 5 && names.Contains("manifest.json") && names.Contains("project.json")
                  && names.Count(n => n.StartsWith("files/attachments/")) == 2 && names.Count(n => n.StartsWith("files/card/")) == 1,
                "entries: manifest, project, the two files there are and the card picture: " + string.Join(", ", names));
            string all = string.Concat(zip.Entries.Where(e => e.FullName.EndsWith(".json")).Select(e => new StreamReader(e.Open()).ReadToEnd()));
            Check(!all.Contains(AppPaths.DataFolder.Replace("\\", "\\\\")) && !all.Contains(":\\\\") && !all.Contains("C:/"),
                "no absolute path in the package's JSON");
            JsonObject manifest = (JsonObject)JsonNode.Parse(new StreamReader(zip.GetEntry("manifest.json")!.Open()).ReadToEnd())!;
            Check((string)manifest["Type"]! == "project" && (int)manifest["Format"]! == 1 && (string)manifest["Name"]! == OddName
                  && (string)manifest["AppVersion"]! == Updates.CurrentVersion.ToString(3) && (int)manifest["Counts"]!["Tasks"]! == 3
                  && (int)manifest["Counts"]!["NotePages"]! == 2 && (int)manifest["Counts"]!["Files"]! == 3,
                "manifest: type, format 1, name, app version, counts: " + manifest.ToJsonString());
        }

        OpenedPackage opened = TransferPackage.Open(package);
        Check(!opened.IsCollection && opened.Name == OddName && opened.Tasks == 3 && opened.NotePages == 2 && opened.Files == 3,
            "the preview's numbers: 3 tasks, 2 written pages, 3 files");
        Project imported = ImportProject(opened, work);
        Project stored = ProjectStore.Load(work).Single();
        Check(stored.Id == imported.Id && stored.Id != original.Id && stored.Name == OddName, "one new project in Munka, a new Id, the same name");
        Check(Normalized(stored) == Normalized(original), "the same content as the original (tasks, pages, description, unknown fields)");
        Check(stored.NotePages![0].Body == original.NotePages![0].Body && stored.NotePages[0].Body.StartsWith("<Section"),
            "a formatted page comes back as the same formatted XAML");
        Check(stored.Attachments.Count == 3 && stored.Attachments.All(a => original.Attachments.All(o => o.Id != a.Id && o.StoredFileName != a.StoredFileName)),
            "files: new Ids and new stored names");
        for (int i = 0; i < 2; i++)
            Check(SameBytes(AttachmentStore.PathOf(stored.Attachments[i].StoredFileName), AttachmentStore.PathOf(original.Attachments[i].StoredFileName)),
                "file " + (i + 1) + " copied byte for byte (" + stored.Attachments[i].DisplayName + ")");
        Check(!AttachmentStore.Exists(stored.Attachments[2]), "the file that was already missing stays listed, still missing");
        Check(stored.CardBackgroundType == BackgroundFill.Image && stored.CardBackgroundImagePath != original.CardBackgroundImagePath
              && SameBytes(ImageStore.Cards.PathOf(stored.CardBackgroundImagePath), ImageStore.Cards.PathOf(original.CardBackgroundImagePath)),
            "the card picture: a copy of its own");
        Check(TabStore.TryLoadOrCreate(work, new[] { stored.TabId }, out List<MemoryTab> tabs, out _) && tabs[0].Id == stored.TabId,
            "on Munka's first tab");

        // The original goes, the copy stays whole: they share no file.
        ProjectMenu.Delete(new[] { original }, p => { }, () => ProjectStore.SaveOrWarn(home, new List<Project>()));
        Check(stored.Attachments.Take(2).All(AttachmentStore.Exists) && ImageStore.Cards.Exists(stored.CardBackgroundImagePath),
            "deleting the original leaves the imported copy's files");

        // Twice, and a name already there: two copies, never one replaced.
        string before = File.ReadAllText(MemoryFiles.Items(work));
        Project second = ImportProject(TransferPackage.Open(package), work);
        List<Project> both = ProjectStore.Load(work);
        Check(both.Count == 2 && second.Name == OddName + " (2)" && both[0].Id != both[1].Id
              && JsonSerializer.Serialize(both[0]) == JsonSerializer.Serialize(JsonSerializer.Deserialize<List<Project>>(before)![0]),
            "imported again: a second copy as '… (2)', the first unchanged");
        Check(second.Attachments.Take(2).All(a => both[0].Attachments.All(f => f.StoredFileName != a.StoredFileName)), "the second copy has files of its own");
        Check(TransferPackage.UniqueName("Kert", new[] { "kert", "Kert (2)" }) == "Kert (3)", "a name clash ignores case and counts on");

        // Project Details exports what it shows, unsaved edits included, and leaves the project alone until it closes.
        Project open = ProjectStore.Load(work)[0];
        ProjectWindow window = await OpenProject(main, open);
        ((TextBox)window.FindName("NameBox")).Text = "Átnevezve";
        Project shown = window.ProjectAsShown();
        Check(shown.Name == "Átnevezve" && open.Name == OddName && shown.Attachments.Count == 3, "Project Details: the package gets the name shown, the project keeps its own until saved");
        Check(((Button)window.FindName("TransferButton")).IsVisible, "Project Details has Export for transfer in its footer");
        RenderWindow(window, "transfer-01-details-en");
        ((TextBox)window.FindName("NameBox")).Text = OddName;
        window.Close();
        await Wait(200);
    }

    static Project ImportProject(OpenedPackage package, Profile target)
    {
        using (StagedImport staged = TransferPackage.Stage(package, null, CancellationToken.None))
            return TransferPackage.CommitProject(staged, target);
    }

    // What must survive a transfer: everything but the Ids, file names and place, which a copy gets new.
    static string Normalized(Project project)
    {
        JsonObject json = (JsonObject)JsonNode.Parse(JsonSerializer.Serialize(project))!;
        foreach (string key in new[] { "Id", "TabId", "Order", "Name", "CardBackgroundImagePath" })
            json.Remove(key);
        foreach (JsonObject attachment in json["Attachments"]!.AsArray().Cast<JsonObject>())
        {
            attachment.Remove("Id");
            attachment.Remove("StoredFileName");
        }
        return json.ToJsonString();
    }

    static bool SameBytes(string a, string b) =>
        File.Exists(a) && File.Exists(b) && SHA256.HashData(File.ReadAllBytes(a)).SequenceEqual(SHA256.HashData(File.ReadAllBytes(b)));

    // ---- A collection -----------------------------------------------------------------

    static void CollectionRoundTrip(MainWindow main)
    {
        Console.WriteLine("A collection with pictures: out and back in, as a new collection");
        List<Profile> profiles = Profiles(main);
        Profile closet = new Profile { Id = Guid.NewGuid(), Name = "Ruhák – Odeća", Type = ProfileType.Collection, Color = "#27AE60", Order = profiles.Count };
        string background = Path.Combine(ImageStore.Backgrounds.Folder, Guid.NewGuid().ToString("N") + ".png");
        Directory.CreateDirectory(ImageStore.Backgrounds.Folder);
        SavePicture(background);
        closet.SetBackground(new BackgroundSetting(BackgroundFill.Image, null, Path.GetFileName(background)));
        profiles.Add(closet);
        ProfileManager.TrySaveProfiles(profiles);

        List<MemoryTab> tabs = new List<MemoryTab>
        {
            new MemoryTab { Id = Guid.NewGuid(), Name = "Nyár", Order = 0 },
            new MemoryTab { Id = Guid.NewGuid(), Name = "Zima", Order = 1 }
        };
        TabStore.TrySave(closet, tabs);
        Directory.CreateDirectory(ImageStore.Items.Folder);
        string coatPicture = Path.Combine(ImageStore.Items.Folder, Guid.NewGuid().ToString("N") + ".png");
        SavePicture(coatPicture);
        string ownPicture = Path.Combine(transferWork, "saját kép.png"); // the user's own file, from before pictures were copied in
        SavePicture(ownPicture);
        List<Entity> items = new List<Entity>
        {
            new Entity { Id = Guid.NewGuid(), EntityType = "Clothing", Name = "Kabát", TabId = tabs[1].Id, ImagePath = Path.GetFileName(coatPicture), Brand = "Đak", Material = "Wool", Order = 0,
                ExtensionData = new Dictionary<string, JsonElement> { ["Rating"] = JsonDocument.Parse("5").RootElement } },
            new Entity { Id = Guid.NewGuid(), EntityType = "Clothing", Name = "Ing", TabId = tabs[0].Id, ImagePath = ownPicture, Order = 1 },
            new Entity { Id = Guid.NewGuid(), EntityType = "Vehicle", Name = "Škoda Fabia", TabId = tabs[0].Id, Plate = "NS-123-ĆŽ", Year = 2015, Order = 2,
                InsuranceExpires = new DateTime(2027, 1, 31),
                Logs = new Dictionary<string, List<Dictionary<string, string>>> { ["Fuel"] = new List<Dictionary<string, string>> { new Dictionary<string, string> { ["Date"] = "2026-05-01", ["Liters"] = "40.5" } } } }
        };
        EntityStore.SaveOrWarn(closet, items);

        string package = Path.Combine(transferWork, "closet" + TransferPackage.Extension);
        TransferPackage.ExportCollection(closet, tabs, items, package, null, CancellationToken.None);
        using (ZipArchive zip = ZipFile.OpenRead(package))
        {
            string entities = new StreamReader(zip.GetEntry("entities.json")!.Open()).ReadToEnd();
            Check(zip.Entries.Count(e => e.FullName.StartsWith("files/items/")) == 2 && zip.GetEntry("tabs.json") != null
                  && zip.Entries.Count(e => e.FullName.StartsWith("files/background/")) == 1,
                "entries: both item pictures, the tabs, the page background");
            Check(!entities.Contains(transferWork.Replace("\\", "\\\\")) && !entities.Contains(":\\\\"),
                "the picture at the user's own path goes in under a new name, without the path");
        }
        Check(EntityStore.Load(closet).Single(e => e.Name == "Ing").ImagePath == ownPicture, "exporting changes nothing in the collection itself");

        OpenedPackage opened = TransferPackage.Open(package);
        Check(opened.IsCollection && opened.Items == 3 && opened.Pictures == 2, "the preview's numbers: 3 items, 2 pictures");
        Profile copy;
        using (StagedImport staged = TransferPackage.Stage(opened, null, CancellationToken.None))
            copy = TransferPackage.CommitCollection(staged, profiles, "#2D7DD2");
        Check(copy.Id != closet.Id && copy.Name == "Ruhák – Odeća (2)" && copy.Type == ProfileType.Collection && copy.Color == "#27AE60"
              && profiles.Contains(copy) && ProfileManager.TryLoadProfiles(out List<Profile> saved, out _) && saved.Any(p => p.Id == copy.Id),
            "a new collection, '(2)' after the name already there, saved in profiles.json");
        Check(copy.BackgroundType == BackgroundFill.Image && copy.BackgroundImagePath != closet.BackgroundImagePath
              && SameBytes(ImageStore.Backgrounds.PathOf(copy.BackgroundImagePath), background), "the page background: a copy of its own");

        List<Entity> back = EntityStore.Load(copy);
        TabStore.TryLoadOrCreate(copy, back.Select(e => e.TabId), out List<MemoryTab> copyTabs, out _);
        Check(back.Count == 3 && back.All(e => items.All(o => o.Id != e.Id)), "three items, new Ids");
        Check(copyTabs.Select(t => t.Name).SequenceEqual(new[] { "Nyár", "Zima" }) && copyTabs.All(t => tabs.All(o => o.Id != t.Id)),
            "the tabs, new Ids, names and order kept");
        Check(back.All(e => copyTabs.Single(t => t.Id == e.TabId).Name == tabs.Single(t => t.Id == items.Single(o => o.Name == e.Name).TabId).Name),
            "each item on its own tab again");
        Entity coat = back.Single(e => e.Name == "Kabát"), shirt = back.Single(e => e.Name == "Ing"), car = back.Single(e => e.Name == "Škoda Fabia");
        Check(SameBytes(EntityImages.FullPath(coat)!, coatPicture) && SameBytes(EntityImages.FullPath(shirt)!, ownPicture)
              && !EntityImages.IsLegacyPath(shirt.ImagePath), "both pictures copied into EntityImages, byte for byte");
        Check(coat.Brand == "Đak" && coat.ExtensionData?["Rating"].GetInt32() == 5 && car.Plate == "NS-123-ĆŽ"
              && car.InsuranceExpires == new DateTime(2027, 1, 31) && car.Logs!["Fuel"][0]["Liters"] == "40.5",
            "fields, logs and unknown fields as they were");
    }

    // ---- One collection item ---------------------------------------------------------------

    static Profile AddCollection(MainWindow main, string name)
    {
        List<Profile> profiles = Profiles(main);
        Profile memory = new Profile { Id = Guid.NewGuid(), Name = name, Type = ProfileType.Collection, Color = "#27AE60", Order = profiles.Count };
        profiles.Add(memory);
        ProfileManager.TrySaveProfiles(profiles);
        Invoke(main, "BuildNav");
        return memory;
    }

    static Profile ImportItem(OpenedPackage package, Profile target, MainWindow main)
    {
        using (StagedImport staged = TransferPackage.Stage(package, null, CancellationToken.None))
            return TransferPackage.CommitItem(staged, target, Profiles(main), "#2D7DD2", Loc.T("Transfer.Import.NewCollectionName"));
    }

    static async Task ItemTransfer(MainWindow main)
    {
        Console.WriteLine("One collection item: out and back in, into a collection picked in the preview");
        Profile garage = AddCollection(main, "Garázs");
        Profile target = AddCollection(main, "Cél – Odredište");
        Directory.CreateDirectory(ImageStore.Items.Folder);
        string shared = Path.Combine(ImageStore.Items.Folder, Guid.NewGuid().ToString("N") + ".png");
        SavePicture(shared);
        Entity car = new Entity
        {
            Id = Guid.NewGuid(), EntityType = "Vehicle", Name = "Škoda Octavia – „Đurđa”", ImagePath = Path.GetFileName(shared), Plate = "SU-777-ČĆ",
            Year = 2019, Order = 0, InsuranceExpires = new DateTime(2027, 3, 1),
            Logs = new Dictionary<string, List<Dictionary<string, string>>> { ["Fuel"] = new List<Dictionary<string, string>> { new Dictionary<string, string> { ["Date"] = "2026-09-01", ["Liters"] = "45.2" } } },
            ExtensionData = new Dictionary<string, JsonElement> { ["Vin"] = JsonDocument.Parse("\"TMB123\"").RootElement }
        };
        Entity bike = new Entity { Id = Guid.NewGuid(), EntityType = "Clothing", Name = "Bicikl", ImagePath = Path.GetFileName(shared), Order = 1 };
        EntityStore.SaveOrWarn(garage, new List<Entity> { car, bike });
        TabStore.TryLoadOrCreate(garage, new[] { car.TabId }, out _, out _);
        TabStore.TryLoadOrCreate(target, Array.Empty<Guid>(), out _, out _);

        string carPackage = Path.Combine(transferWork, "car" + TransferPackage.Extension);
        string bikePackage = Path.Combine(transferWork, "bike" + TransferPackage.Extension);
        TransferPackage.ExportItem(car, carPackage, null, CancellationToken.None);
        TransferPackage.ExportItem(bike, bikePackage, null, CancellationToken.None);
        using (ZipArchive zip = ZipFile.OpenRead(carPackage))
        {
            List<string> names = zip.Entries.Select(e => e.FullName).OrderBy(n => n).ToList();
            JsonObject manifest = (JsonObject)JsonNode.Parse(new StreamReader(zip.GetEntry("manifest.json")!.Open()).ReadToEnd())!;
            Check(names.Count == 3 && names.Contains("item.json") && names.Count(n => n.StartsWith("files/items/")) == 1,
                "an item package: manifest, item.json and its picture only: " + string.Join(", ", names));
            Check((string)manifest["Type"]! == "item" && (int)manifest["Format"]! == 2 && (int)manifest["Counts"]!["Pictures"]! == 1,
                "manifest: type item, format 2 (older versions refuse it as newer), 1 picture");
            string json = new StreamReader(zip.GetEntry("item.json")!.Open()).ReadToEnd();
            Check(!json.Contains(":\\\\") && !json.Contains(AppPaths.DataFolder.Replace("\\", "\\\\")), "no absolute path in item.json");
        }
        Check(EntityStore.Load(garage).Count == 2, "exporting changes nothing in the collection");

        OpenedPackage opened = TransferPackage.Open(carPackage);
        Check(opened.IsItem && !opened.IsCollection && opened.Name == car.Name && opened.Pictures == 1, "the preview's numbers: the item, 1 picture");

        // The preview: collections only, the open one by default, the first one otherwise.
        Profile work = Profiles(main).First(p => p.Name == "Munka");
        ImportDialog preview = new ImportDialog(opened, Profiles(main), target) { Owner = main };
        preview.Show();
        await Wait(200);
        ComboBox box = (ComboBox)preview.FindName("TargetBox");
        Check(preview.Target == target && box.Items.Cast<ComboBoxItem>().All(i => ((Profile)i.Tag).Type == ProfileType.Collection)
              && ((TextBlock)preview.FindName("KindText")).Text == Loc.F("Transfer.Import.Item", car.Name)
              && ((TextBlock)preview.FindName("WhereText")).Text == Loc.T("Transfer.Import.NewItem"),
            "the preview lists collections only, the open one chosen: " + ((TextBlock)preview.FindName("WhereText")).Text);
        box.SelectedIndex = box.Items.Cast<ComboBoxItem>().ToList().FindIndex(i => i.Tag == garage);
        Check(preview.Target == garage && ((TextBlock)preview.FindName("WhereText")).Text == Loc.F("Transfer.Import.NewItemRenamed", car.Name + " (2)"),
            "another collection picked: the name it gets there is said");
        RenderWindow(preview, "transfer-05-preview-item-en");
        preview.Close();
        ImportDialog fromProject = new ImportDialog(opened, Profiles(main), work) { Owner = main };
        Check(fromProject.Target == Profiles(main).Where(p => p.Type == ProfileType.Collection).OrderBy(p => p.Order).First(),
            "opened from a project memory: the first collection");
        fromProject.Close();
        ImportDialog noCollection = new ImportDialog(opened, Profiles(main).Where(p => p.Type == ProfileType.Project).ToList(), work) { Owner = main };
        noCollection.Show();
        await Wait(200);
        Check(noCollection.Target == null && ((Button)noCollection.FindName("ImportButton")).IsEnabled
              && ((TextBlock)noCollection.FindName("WhereText")).Text == Loc.F("Transfer.Import.NewItemNewCollection", Loc.T("Transfer.Import.NewCollectionName")),
            "no collection yet: a new one is offered, Import on");
        noCollection.Close();

        // Into the picked collection: a new copy, new Id and picture, on its first tab, unknown fields kept.
        ImportItem(opened, target, main);
        Entity copy = EntityStore.Load(target).Single();
        TabStore.TryLoadOrCreate(target, new[] { copy.TabId }, out List<MemoryTab> tabs, out _);
        Check(copy.Id != car.Id && copy.Name == car.Name && copy.TabId == tabs[0].Id && copy.Plate == car.Plate && copy.Year == 2019
              && copy.Logs!["Fuel"][0]["Liters"] == "45.2" && copy.ExtensionData?["Vin"].GetString() == "TMB123",
            "into the picked collection: a new item, its fields, logs and unknown fields as they were");
        Check(copy.ImagePath != car.ImagePath && SameBytes(EntityImages.FullPath(copy)!, shared), "its picture: a copy of its own");

        ImportItem(TransferPackage.Open(carPackage), target, main);
        ImportItem(TransferPackage.Open(bikePackage), target, main);
        List<Entity> inTarget = EntityStore.Load(target);
        Check(inTarget.Count == 3 && inTarget[1].Name == car.Name + " (2)" && inTarget.Select(e => e.Id).Distinct().Count() == 3,
            "imported again: a second copy as '… (2)', never one replaced");
        Check(inTarget.Select(e => e.ImagePath).Distinct().Count() == 3 && inTarget.All(e => SameBytes(EntityImages.FullPath(e)!, shared)),
            "two items that shared one picture: every copy has a picture of its own");
        EntityImages.DeleteCopy(inTarget[0].ImagePath);
        Check(inTarget.Skip(1).All(e => File.Exists(EntityImages.FullPath(e)!)) && File.Exists(shared),
            "deleting one copy's picture leaves the others and the original");

        ImportItem(TransferPackage.Open(carPackage), garage, main);
        Check(EntityStore.Load(garage).Count(e => e.Name.StartsWith("Škoda")) == 2, "back into its own collection: a second copy beside the first");

        // No collection to go into: a new one, saved in profiles.json, its tab made when it opens.
        int collections = Profiles(main).Count(p => p.Type == ProfileType.Collection);
        Profile made;
        using (StagedImport staged = TransferPackage.Stage(TransferPackage.Open(carPackage), null, CancellationToken.None))
            made = TransferPackage.CommitItem(staged, null, Profiles(main), "#2D7DD2", Loc.T("Transfer.Import.NewCollectionName"));
        Check(made.Type == ProfileType.Collection && made.Name == Loc.T("Transfer.Import.NewCollectionName")
              && Profiles(main).Count(p => p.Type == ProfileType.Collection) == collections + 1
              && ProfileManager.TryLoadProfiles(out List<Profile> saved, out _) && saved.Any(p => p.Id == made.Id)
              && EntityStore.Load(made).Single().Name == car.Name,
            "no collection picked: a new collection '" + made.Name + "' with the item, saved");

        // A failure adds nothing: an unreadable target, and one that can't be written.
        File.WriteAllText(MemoryFiles.Items(made), "{ broken");
        List<string> before = DataFiles();
        string message = null;
        try
        {
            ImportItem(TransferPackage.Open(carPackage), made, main);
        }
        catch (TransferException e)
        {
            message = e.Message;
        }
        Check(message != null && message.Contains(made.Name) && DataFiles().SequenceEqual(before),
            "an unreadable target collection: refused, the data folder exactly as before (" + message + ")");
        File.WriteAllText(MemoryFiles.Items(made), "[]");
        before = DataFiles();
        message = null;
        using (FileStream locked = new FileStream(MemoryFiles.Items(made), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try
            {
                ImportItem(TransferPackage.Open(carPackage), made, main);
            }
            catch (TransferException e)
            {
                message = e.Message;
            }
        }
        Check(message != null && DataFiles().SequenceEqual(before),
            "a target that can't be written: refused, the picture taken out again, the data folder exactly as before (" + message + ")");
        Check(!Directory.EnumerateDirectories(AppPaths.DataFolder, "Import-*").Any(), "no staging folder left");

        // The item card's right-click menu.
        Invoke(main, "NavigateTo", garage);
        await Wait(400);
        List<FrameworkElement> cards = Descendants(main).OfType<FrameworkElement>()
            .Where(e => e.ContextMenu != null && e.ContextMenu.Items.OfType<MenuItem>().Any(m => (string)m.Header == Loc.T("Common.Edit"))).ToList();
        Check(cards.Count >= 2 && cards.All(c => c.ContextMenu.Items.OfType<MenuItem>().Any(m => (string)m.Header == Loc.T("Transfer.ExportMenu"))),
            "every item card's right-click menu has Export for transfer (" + cards.Count + " cards)");
        if (cards.Count > 0)
        {
            cards[0].ContextMenu.PlacementTarget = cards[0];
            cards[0].ContextMenu.IsOpen = true;
            await Wait(300);
            RenderPopup(cards[0].ContextMenu, "transfer-06-item-menu-en");
            cards[0].ContextMenu.IsOpen = false;
        }
        Invoke(main, "NavigateHome");
    }

    // ---- A large attachment ---------------------------------------------------------------

    static void LargeAttachment(MainWindow main)
    {
        Console.WriteLine("A large attachment, with progress");
        Profile memory = AddProjectMemory(main, "Nagy fájlok");
        string big = Path.Combine(AttachmentStore.Folder, Guid.NewGuid().ToString("N") + ".bin");
        byte[] chunk = new byte[1 << 20];
        new Random(7).NextBytes(chunk);
        using (FileStream file = File.Create(big))
            for (int i = 0; i < 160; i++)
            {
                chunk[0] = (byte)i;
                file.Write(chunk, 0, chunk.Length);
            }
        Project project = new Project
        {
            Id = Guid.NewGuid(), Name = "Videó", Tasks = new List<TaskItem>(),
            Attachments = new List<Attachment> { new Attachment { Id = Guid.NewGuid(), DisplayName = "felvétel.bin", StoredFileName = Path.GetFileName(big), SizeBytes = 160L << 20, AddedDate = DateTime.Today } }
        };
        string package = Path.Combine(transferWork, "large" + TransferPackage.Extension);
        List<double> exportSteps = new List<double>(), importSteps = new List<double>();
        TransferPackage.ExportProject(project, 0, package, new StepLog(exportSteps), CancellationToken.None);
        Project copy;
        using (StagedImport staged = TransferPackage.Stage(TransferPackage.Open(package), new StepLog(importSteps), CancellationToken.None))
            copy = TransferPackage.CommitProject(staged, memory);
        Check(exportSteps.Count > 1 && importSteps.Count > 1 && exportSteps.Zip(exportSteps.Skip(1)).All(p => p.First <= p.Second)
              && exportSteps.Last() <= 1 && importSteps.Last() <= 1,
            "progress reported while packing (" + exportSteps.Count + " steps) and unpacking (" + importSteps.Count + ")");
        Check(SameBytes(AttachmentStore.PathOf(copy.Attachments[0].StoredFileName), big), "160 MB attachment: the same bytes after the round trip");

        // Cancelled half-way: no package left, nothing imported.
        string cancelled = Path.Combine(transferWork, "cancelled" + TransferPackage.Extension);
        CancellationTokenSource stop = new CancellationTokenSource();
        bool threw = false;
        try
        {
            TransferPackage.ExportProject(project, 0, cancelled, new StepLog(new List<double>(), stop), stop.Token);
        }
        catch (OperationCanceledException)
        {
            threw = true;
        }
        Check(threw && !File.Exists(cancelled) && !Directory.EnumerateFiles(transferWork, "*.tmp-*").Any(), "cancelled export: no file left");
        int before = Directory.GetFiles(AttachmentStore.Folder).Length;
        stop = new CancellationTokenSource();
        threw = false;
        try
        {
            TransferPackage.Stage(TransferPackage.Open(package), new StepLog(new List<double>(), stop), stop.Token).Dispose();
        }
        catch (OperationCanceledException)
        {
            threw = true;
        }
        Check(threw && Directory.GetFiles(AttachmentStore.Folder).Length == before && !Directory.EnumerateDirectories(AppPaths.DataFolder, "Import-*").Any(),
            "cancelled import: nothing added, the staging folder gone");
        File.Delete(big);
        AttachmentStore.Delete(copy.Attachments);
    }

    // Reports on the calling thread (Progress<T> would post to the dispatcher); `stop` cancels at the first report.
    sealed class StepLog : IProgress<double>
    {
        private readonly List<double> steps;
        private readonly CancellationTokenSource stop;
        public StepLog(List<double> steps, CancellationTokenSource stop = null) { this.steps = steps; this.stop = stop; }
        public void Report(double value) { steps.Add(value); stop?.Cancel(); }
    }

    // ---- Bad packages ----------------------------------------------------------------------

    static void BadPackages(MainWindow main)
    {
        Console.WriteLine("Bad packages: refused, nothing added");
        string good = Path.Combine(transferWork, ExportMenu.SafeFileName(OddName) + TransferPackage.Extension);
        string manifest;
        using (ZipArchive zip = ZipFile.OpenRead(good))
            manifest = new StreamReader(zip.GetEntry("manifest.json")!.Open()).ReadToEnd();
        string projectJson = JsonSerializer.Serialize(new Project { Id = Guid.NewGuid(), Name = "x", Tasks = new List<TaskItem>() });
        List<string> before = DataFiles();

        Refused(MakeZip("slip", ("manifest.json", manifest), ("project.json", projectJson), ("../evil.txt", "x")),
            Loc.F("Transfer.Error.Unsafe", "../evil.txt"), "zip slip: '../evil.txt'");
        Refused(MakeZip("slip2", ("manifest.json", manifest), ("project.json", projectJson), ("files/attachments/../../../evil.txt", "x")),
            Loc.F("Transfer.Error.Unsafe", "files/attachments/../../../evil.txt"), "zip slip inside files/");
        Refused(MakeZip("absolute", ("manifest.json", manifest), ("project.json", projectJson), ("C:/Windows/evil.txt", "x")),
            Loc.F("Transfer.Error.Unsafe", "C:/Windows/evil.txt"), "an absolute path");
        Refused(MakeZip("rooted", ("manifest.json", manifest), ("project.json", projectJson), ("/evil.txt", "x")),
            Loc.F("Transfer.Error.Unsafe", "/evil.txt"), "a path from the root");
        Refused(MakeZip("backslash", ("manifest.json", manifest), ("project.json", projectJson), ("files\\..\\evil.txt", "x")),
            Loc.F("Transfer.Error.Unsafe", "files\\..\\evil.txt"), "backslashes");
        Check(!File.Exists(Path.Combine(Path.GetDirectoryName(AppPaths.DataFolder)!, "evil.txt")) && !File.Exists(AppPaths.Data("evil.txt")),
            "no evil.txt written anywhere");

        string huge = "{\"Name\":\"" + new string('a', (int)TransferPackage.MaxJsonBytes + 10) + "\"}";
        Refused(MakeZip("oversized", ("manifest.json", manifest), ("project.json", huge)),
            Loc.F("Transfer.Error.PartTooLarge", "project.json", AttachmentStore.FormatSize(TransferPackage.MaxJsonBytes)), "an oversized JSON entry");
        string many = Path.Combine(transferWork, "many.tidymind");
        using (ZipArchive zip = ZipFile.Open(many, ZipArchiveMode.Create))
        {
            WriteEntry(zip, "manifest.json", manifest);
            for (int i = 0; i <= TransferPackage.MaxEntries; i++)
                zip.CreateEntry("files/attachments/" + i + ".txt");
        }
        Refused(many, Loc.F("Transfer.Error.TooManyFiles", TransferPackage.MaxEntries + 2), "too many entries");

        string empty = Path.Combine(transferWork, "empty.tidymind");
        File.WriteAllBytes(empty, Array.Empty<byte>());
        Refused(empty, Loc.T("Transfer.Error.NotPackage"), "an empty file");
        string emptyZip = Path.Combine(transferWork, "emptyzip.tidymind");
        using (ZipFile.Open(emptyZip, ZipArchiveMode.Create)) { }
        Refused(emptyZip, Loc.T("Transfer.Error.NotPackage"), "an empty ZIP");
        Refused(MakeZip("foreign", ("readme.txt", "hello"), ("photo.jpg", "jpeg")), Loc.T("Transfer.Error.NotPackage"), "a foreign ZIP");
        Refused(MakeZip("backup", ("tidymind-backup.json", "{\"App\":\"TidyMind\",\"Format\":1}"), ("profiles.json", "[]")),
            Loc.T("Transfer.Error.NotPackage"), "a full backup ZIP");
        string text = Path.Combine(transferWork, "notes.tidymind");
        File.WriteAllText(text, "just some text, not a ZIP");
        Refused(text, Loc.T("Transfer.Error.NotPackage"), "a text file named .tidymind");
        Refused(MakeZip("othertype", ("manifest.json", manifest.Replace("\"project\"", "\"spreadsheet\"")), ("project.json", projectJson)),
            Loc.T("Transfer.Error.NotPackage"), "an unknown type");
        Refused(MakeZip("otherapp", ("manifest.json", manifest.Replace("\"TidyMind\"", "\"OtherApp\"")), ("project.json", projectJson)),
            Loc.T("Transfer.Error.NotPackage"), "another app's manifest");
        Refused(MakeZip("nojson", ("manifest.json", manifest)), Loc.F("Transfer.Error.Missing", "project.json"), "the project's JSON missing");
        Refused(MakeZip("badjson", ("manifest.json", manifest), ("project.json", "{\"Name\": [unclosed")), "project.json", "broken JSON");
        Refused(MakeZip("twice", ("manifest.json", manifest), ("project.json", projectJson), ("project.json", projectJson)),
            Loc.F("Transfer.Error.Twice", "project.json"), "an entry twice");

        string truncated = Path.Combine(transferWork, "truncated.tidymind");
        byte[] bytes = File.ReadAllBytes(good);
        File.WriteAllBytes(truncated, bytes.Take(bytes.Length / 2).ToArray());
        Refused(truncated, null, "a truncated package");

        // Damaged inside a file's data (the table of contents intact): the preview opens, the import stops, nothing added.
        string corrupt = Path.Combine(transferWork, "corrupt.tidymind");
        File.Copy(good, corrupt, true);
        using (ZipArchive zip = ZipFile.Open(corrupt, ZipArchiveMode.Update))
        {
            ZipArchiveEntry entry = zip.GetEntry(zip.Entries.First(e => e.FullName.StartsWith("files/attachments/")).FullName)!;
            entry.Delete();
            ZipArchiveEntry replaced = zip.CreateEntry(entry.FullName, CompressionLevel.Optimal);
            using (Stream s = replaced.Open())
                s.Write(Encoding.UTF8.GetBytes(new string('z', 4000)));
        }
        byte[] raw = File.ReadAllBytes(corrupt);
        int at = IndexOf(raw, Encoding.ASCII.GetBytes("files/attachments/")) + 200; // inside the first file's compressed data
        for (int i = 0; i < 16; i++)
            raw[at + i] ^= 0x5A;
        File.WriteAllBytes(corrupt, raw);
        Profile target = Profiles(main).First(p => p.Name == "Munka");
        int projectsBefore = ProjectStore.Load(target).Count;
        string message = null;
        try
        {
            using (StagedImport staged = TransferPackage.Stage(TransferPackage.Open(corrupt), null, CancellationToken.None))
                TransferPackage.CommitProject(staged, target);
        }
        catch (Exception e) when (e is TransferException || e is InvalidDataException || e is IOException)
        {
            message = e.Message;
        }
        Check(message != null && ProjectStore.Load(target).Count == projectsBefore, "corrupt file data: refused while unpacking (" + message + ")");

        Check(DataFiles().SequenceEqual(before), "after every bad package: the data folder exactly as before");
    }

    static void Refused(string path, string expected, string what)
    {
        string message = null;
        try
        {
            TransferPackage.Open(path);
        }
        catch (TransferException e)
        {
            message = e.Message;
        }
        Check(message != null && (expected == null || message.Contains(expected)), what + ": " + (message ?? "NOT refused"));
    }

    static string MakeZip(string name, params (string Entry, string Text)[] entries)
    {
        string path = Path.Combine(transferWork, name + TransferPackage.Extension);
        using (ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Create))
            foreach ((string entry, string text) in entries)
                WriteEntry(zip, entry, text);
        return path;
    }

    static void WriteEntry(ZipArchive zip, string name, string text)
    {
        using (StreamWriter writer = new StreamWriter(zip.CreateEntry(name).Open()))
            writer.Write(text);
    }

    static int IndexOf(byte[] data, byte[] pattern)
    {
        for (int i = 0; i + pattern.Length <= data.Length; i++)
            if (data.AsSpan(i, pattern.Length).SequenceEqual(pattern))
                return i;
        return -1;
    }

    // Every file in the data folder with its size and time, logs left out.
    static List<string> DataFiles() => Directory.EnumerateFiles(AppPaths.DataFolder, "*", SearchOption.AllDirectories)
        .Where(f => !f.EndsWith(".log"))
        .Select(f => f + "|" + new FileInfo(f).Length + "|" + File.GetLastWriteTimeUtc(f).Ticks).OrderBy(f => f).ToList();

    // ---- What the JSON holds ------------------------------------------------------------------

    // XAML that would make objects, a file two items share, and nulls inside lists: a package that opens, whose
    // content is made harmless before anything parses or stores it.
    static void UntrustedContent(MainWindow main)
    {
        Console.WriteLine("Untrusted content inside a well-formed package");
        string good = Path.Combine(transferWork, ExportMenu.SafeFileName(OddName) + TransferPackage.Extension);
        string manifest;
        using (ZipArchive zip = ZipFile.OpenRead(good))
            manifest = new StreamReader(zip.GetEntry("manifest.json")!.Open()).ReadToEnd();

        string marker = Path.Combine(transferWork, "xaml-ran");
        string presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        string gadget = "<Section xmlns=\"" + presentation + "\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" "
            + "xmlns:io=\"clr-namespace:System.IO;assembly=System.Runtime\"><Section.Resources>"
            + "<ObjectDataProvider x:Key=\"p\" ObjectType=\"{x:Type io:Directory}\" MethodName=\"CreateDirectory\">"
            + "<ObjectDataProvider.MethodParameters><x:String>" + marker + "</x:String></ObjectDataProvider.MethodParameters>"
            + "</ObjectDataProvider></Section.Resources><Paragraph>Hello <Run>world</Run></Paragraph></Section>";
        string extension = "<Section xmlns=\"" + presentation + "\"><Paragraph Foreground=\"{StaticResource Accent}\">Szia</Paragraph></Section>";
        Project crafted = new Project
        {
            Id = Guid.NewGuid(), Name = "Crafted", Description = extension,
            Tasks = new List<TaskItem> { null, new TaskItem { Title = "kept" } },
            NotePages = new List<NotePage> { new NotePage { Body = gadget }, null, new NotePage { Body = FormattedText() } }
        };
        OpenedPackage opened = TransferPackage.Open(MakeZip("gadget", ("manifest.json", manifest), ("project.json", JsonSerializer.Serialize(crafted))));
        int pages = opened.NotePages; // the preview reads the pages
        Profile target = AddProjectMemory(main, "Gyanús");
        Project imported = ImportProject(opened, target);
        Check(!Directory.Exists(marker) && !File.Exists(marker), "XAML that would make an object never runs (no " + Path.GetFileName(marker) + ")");
        Check(imported.NotePages![0].Body == "Hello world" && imported.Description == "Szia",
            "XAML with other objects or a markup extension keeps its words, as plain text: '" + imported.NotePages[0].Body + "', '" + imported.Description + "'");
        Check(imported.NotePages.Count == 2 && imported.NotePages[1].Body == FormattedText() && pages == 2,
            "plain formatted text stays formatted; a null page is dropped");
        Check(imported.Tasks.Count == 1 && imported.Tasks[0].Title == "kept", "a null task is dropped");

        // Two items with one picture, nulls in sizes and log rows.
        string picture = Path.Combine(transferWork, "shared.png");
        SavePicture(picture);
        string entities = "[{\"Id\":\"" + Guid.NewGuid() + "\",\"EntityType\":\"Clothing\",\"Name\":\"A\",\"ImagePath\":\"shared.png\",\"Sizes\":[null,{\"Size\":\"M\",\"Quantity\":1}]},"
            + "{\"Id\":\"" + Guid.NewGuid() + "\",\"EntityType\":\"Vehicle\",\"Name\":\"B\",\"ImagePath\":\"shared.png\",\"Logs\":{\"Fuel\":[null,{\"Date\":\"2026-01-02\",\"Liters\":null}],\"Service\":null}}]";
        JsonObject collectionManifest = (JsonObject)JsonNode.Parse(manifest)!;
        collectionManifest["Type"] = "collection";
        string path = Path.Combine(transferWork, "shared" + TransferPackage.Extension);
        using (ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            WriteEntry(zip, "manifest.json", collectionManifest.ToJsonString());
            WriteEntry(zip, "collection.json", "{\"Name\":\"Megosztott\",\"Type\":1,\"Color\":\"not a color\",\"BackgroundType\":77}");
            WriteEntry(zip, "entities.json", entities);
            zip.CreateEntryFromFile(picture, "files/items/shared.png");
        }
        Profile copy;
        using (StagedImport staged = TransferPackage.Stage(TransferPackage.Open(path), null, CancellationToken.None))
            copy = TransferPackage.CommitCollection(staged, Profiles(main), "#2D7DD2");
        List<Entity> items = EntityStore.Load(copy);
        Check(items.Count == 2 && items[0].ImagePath != items[1].ImagePath && items.All(i => SameBytes(EntityImages.FullPath(i)!, picture)),
            "one picture used by two items: each gets a copy of its own");
        Check(items[0].Sizes.Count == 1 && items[0].Sizes[0].Size == "M" && items[1].Logs!["Fuel"].Count == 1
              && !items[1].Logs["Fuel"][0].ContainsKey("Liters") && !items[1].Logs.ContainsKey("Service"),
            "nulls inside sizes, logs and log rows are dropped");
        Check(copy.Color == "#2D7DD2" && copy.BackgroundType == BackgroundFill.None, "a color and a background type it can't use: the defaults");
    }

    // ---- Versions ------------------------------------------------------------------------------

    static void Versions(MainWindow main)
    {
        Console.WriteLine("Versions");
        string good = Path.Combine(transferWork, ExportMenu.SafeFileName(OddName) + TransferPackage.Extension);
        string manifest, project;
        using (ZipArchive zip = ZipFile.OpenRead(good))
        {
            manifest = new StreamReader(zip.GetEntry("manifest.json")!.Open()).ReadToEnd();
            project = new StreamReader(zip.GetEntry("project.json")!.Open()).ReadToEnd();
        }
        JsonObject newerFormat = (JsonObject)JsonNode.Parse(manifest)!;
        newerFormat["Format"] = TransferPackage.FormatVersion + 1;
        newerFormat["AppVersion"] = "3.0.0";
        List<string> before = DataFiles();
        Refused(MakeZip("format2", ("manifest.json", newerFormat.ToJsonString()), ("project.json", project)),
            Loc.F("Transfer.Error.Newer", "3.0.0"), "a newer package format: refused politely");
        Check(DataFiles().SequenceEqual(before), "nothing added");

        JsonObject newerApp = (JsonObject)JsonNode.Parse(manifest)!;
        newerApp["AppVersion"] = "9.9.9";
        newerApp["Signature"] = "something a newer version adds";
        JsonObject data = (JsonObject)JsonNode.Parse(project)!;
        data["Colour"] = "teal";
        data["Tasks"]![0]!["DueDate"] = "2026-12-24";
        OpenedPackage opened = TransferPackage.Open(MakeZip("newerapp", ("manifest.json", newerApp.ToJsonString()), ("project.json", data.ToJsonString())));
        Check(opened.FromNewerApp && opened.Manifest.ExtensionData!.ContainsKey("Signature"), "a newer app, the same format: accepted, said in the preview");
        Profile target = AddProjectMemory(main, "Újabb");
        ImportProject(opened, target);
        string stored = File.ReadAllText(MemoryFiles.Items(target));
        Check(stored.Contains("\"Colour\":\"teal\"") && stored.Contains("\"DueDate\":\"2026-12-24\""), "its unknown fields are kept in projects.json");
    }

    // ---- A failure part-way --------------------------------------------------------------------

    static void FailurePartWay(MainWindow main)
    {
        Console.WriteLine("A failure part-way adds nothing");
        string good = Path.Combine(transferWork, ExportMenu.SafeFileName(OddName) + TransferPackage.Extension);
        Profile target = AddProjectMemory(main, "Zárolt");
        ProjectStore.SaveOrWarn(target, new List<Project>());
        string file = MemoryFiles.Items(target);
        int attachments = Directory.GetFiles(AttachmentStore.Folder).Length, cards = Directory.GetFiles(ImageStore.Cards.Folder).Length;
        string message = null;
        using (FileStream locked = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try
            {
                ImportProject(TransferPackage.Open(good), target);
            }
            catch (TransferException e)
            {
                message = e.Message;
            }
        }
        Check(message != null && ProjectStore.Load(target).Count == 0, "projects.json can't be written: refused (" + message + ")");
        Check(Directory.GetFiles(AttachmentStore.Folder).Length == attachments && Directory.GetFiles(ImageStore.Cards.Folder).Length == cards,
            "the files already moved in are taken out again");

        File.WriteAllText(file, "{ not json");
        message = null;
        try
        {
            ImportProject(TransferPackage.Open(good), target);
        }
        catch (TransferException e)
        {
            message = e.Message;
        }
        Check(message != null && message.Contains(target.Name) && File.ReadAllText(file) == "{ not json",
            "an unreadable projects.json: said, and never saved over (" + message + ")");
        Check(Directory.GetFiles(AttachmentStore.Folder).Length == attachments, "nothing added");
    }

    // ---- The UI, in three languages ----------------------------------------------------------

    static async Task TransferUi(MainWindow main)
    {
        Console.WriteLine("The actions and the preview, in three languages");
        Profile work = Profiles(main).First(p => p.Name == "Munka");
        Profile closet = Profiles(main).First(p => p.Name == "Ruhák – Odeća");
        string projectPackage = Path.Combine(transferWork, ExportMenu.SafeFileName(OddName) + TransferPackage.Extension);
        string collectionPackage = Path.Combine(transferWork, "closet" + TransferPackage.Extension);
        Project project = ProjectStore.Load(work)[0];

        foreach (string code in Codes)
        {
            Loc.Switch(code);
            await Wait(300);
            string tag = code == Loc.SerbianLatin ? "sr" : code;

            ContextMenu menu = ProjectMenu.Build(main, project, () => true, () => { }, () => { });
            menu.IsOpen = true;
            await Wait(300);
            MenuItem item = menu.Items.OfType<MenuItem>().FirstOrDefault(m => (string)m.Header == Loc.T("Transfer.ExportMenu"));
            Check(item != null, tag + ": a project card's menu has '" + Loc.T("Transfer.ExportMenu") + "'");
            RenderPopup(menu, "transfer-02-card-menu-" + tag);
            menu.IsOpen = false;

            Invoke(main, "BuildNav");
            Border nav = NavItem(main, closet);
            Check(nav.ContextMenu.Items.OfType<MenuItem>().Any(m => (string)m.Header == Loc.T("Transfer.ExportMenu")),
                tag + ": a collection's sidebar menu has it too");
            Check(!NavItem(main, work).ContextMenu.Items.OfType<MenuItem>().Any(m => (string)m.Header == Loc.T("Transfer.ExportMenu")),
                tag + ": a project memory's menu doesn't (its projects are exported one by one)");

            ImportDialog preview = new ImportDialog(TransferPackage.Open(projectPackage), Profiles(main), work) { Owner = main };
            preview.Show();
            await Wait(300);
            string kind = ((TextBlock)preview.FindName("KindText")).Text, counts = ((TextBlock)preview.FindName("CountsText")).Text;
            string exported = ((TextBlock)preview.FindName("ExportedText")).Text, where = ((TextBlock)preview.FindName("WhereText")).Text;
            Check(kind == Loc.F("Transfer.Import.Project", OddName)
                  && counts == Loc.F("Transfer.Import.ProjectCounts", Loc.Plural("Transfer.Count.Tasks", 3), Loc.Plural("Transfer.Count.Pages", 2), Loc.Plural("Transfer.Count.Files", 3))
                  && exported.Contains(Updates.CurrentVersion.ToString(3)) && preview.Target == work
                  && (string)((ComboBoxItem)((ComboBox)preview.FindName("TargetBox")).SelectedItem).Content == "Munka"
                  && where == Loc.F("Transfer.Import.NewProjectRenamed", OddName + " (3)"),
                tag + ": the project preview: " + kind + " | " + counts + " | " + exported + " | " + where);
            RenderWindow(preview, "transfer-03-preview-project-" + tag);
            preview.Close();

            preview = new ImportDialog(TransferPackage.Open(collectionPackage), Profiles(main), work) { Owner = main };
            preview.Show();
            await Wait(300);
            Check(((TextBlock)preview.FindName("KindText")).Text == Loc.F("Transfer.Import.Collection", "Ruhák – Odeća")
                  && ((FrameworkElement)preview.FindName("TargetPanel")).Visibility != Visibility.Visible
                  && ((TextBlock)preview.FindName("WhereText")).Text == Loc.F("Transfer.Import.NewCollectionRenamed", "Ruhák – Odeća (3)"),
                tag + ": the collection preview: " + ((TextBlock)preview.FindName("CountsText")).Text);
            RenderWindow(preview, "transfer-04-preview-collection-" + tag);
            preview.Close();
        }
        Loc.Switch(Loc.English);
        await Wait(200);

        // Plurals: hu has one form; sr has One/Few/Other.
        Check(Loc.Plural("Transfer.Count.Pictures", 1) == "1 picture" && Loc.Plural("Transfer.Count.Pictures", 2) == "2 pictures", "en: 1 picture, 2 pictures");
        Loc.Switch(Loc.SerbianLatin);
        Check(Loc.Plural("Transfer.Count.Tasks", 1) == "1 zadatak" && Loc.Plural("Transfer.Count.Tasks", 3) == "3 zadatka"
              && Loc.Plural("Transfer.Count.Tasks", 5) == "5 zadataka" && Loc.Plural("Transfer.Count.Tasks", 21) == "21 zadatak", "sr: 1/3/5/21 zadatak, zadatka, zadataka, zadatak");
        Loc.Switch(Loc.Hungarian);
        Check(Loc.Plural("Transfer.Count.Files", 1) == "1 fájl" && Loc.Plural("Transfer.Count.Files", 7) == "7 fájl", "hu: 1 fájl, 7 fájl");
        Loc.Switch(Loc.English);

        // No project memory to put a project in: said, and Import is off.
        ImportDialog none = new ImportDialog(TransferPackage.Open(projectPackage), Profiles(main).Where(p => p.Type == ProfileType.Collection).ToList(), null) { Owner = main };
        none.Show();
        await Wait(200);
        Check(!((Button)none.FindName("ImportButton")).IsEnabled && ((TextBlock)none.FindName("WhereText")).Text == Loc.T("Transfer.Import.NoProjectMemory"),
            "no project memory: said, Import off");
        none.Close();

        // A memory whose projects.json can't be read (FailurePartWay left one): said in the preview, Import off.
        Profile broken = Profiles(main).First(p => p.Name == "Zárolt");
        ImportDialog unreadable = new ImportDialog(TransferPackage.Open(projectPackage), Profiles(main), broken) { Owner = main };
        unreadable.Show();
        await Wait(200);
        Check(!((Button)unreadable.FindName("ImportButton")).IsEnabled
              && ((TextBlock)unreadable.FindName("WhereText")).Text.StartsWith(Loc.F("Transfer.Error.TargetUnreadable", "Zárolt", "").TrimEnd()),
            "an unreadable memory chosen: said in the preview, Import off");
        unreadable.Close();

        // The whole import as the user does it (the file already picked): the preview's Import, the progress window, the result.
        int count = ProjectStore.Load(work).Count;
        _ = main.Dispatcher.BeginInvoke(new Action(async () =>
        {
            for (int i = 0; i < 50 && !Application.Current.Windows.OfType<ImportDialog>().Any(); i++)
                await Wait(100);
            ImportDialog shown = Application.Current.Windows.OfType<ImportDialog>().FirstOrDefault();
            if (shown != null)
                shown.DialogResult = true;
        }), DispatcherPriority.Background);
        Profile result = TransferUI.ImportFile(main, projectPackage, Profiles(main), work, () => "#3498DB");
        Check(result == work && ProjectStore.Load(work).Count == count + 1 && ProjectStore.Load(work).Last().Name == OddName + " (3)",
            "TransferUI.ImportFile: imported into the open memory as '… (3)'");

        // A refused package through the UI: one message, nothing added.
        int boxes = selectBoxes.Count;
        Profile none2 = TransferUI.ImportFile(main, Path.Combine(transferWork, "slip.tidymind"), Profiles(main), work, () => "#3498DB");
        await Wait(400);
        Check(none2 == null && selectBoxes.Count == boxes + 1 && selectBoxes.Last().Contains("../evil.txt"),
            "a refused package in the app: one message (" + selectBoxes.LastOrDefault() + ")");

        SettingsWindow settings = new SettingsWindow(null, owner => false) { Owner = main };
        settings.Show();
        await Wait(300);
        Check(((Button)settings.FindName("ImportButton")).IsVisible, "Settings has Import");
        settings.Close();
    }
}
