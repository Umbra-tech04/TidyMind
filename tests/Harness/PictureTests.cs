using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DocumentFormat.OpenXml.Packaging;
using TidyMind;

// Harness pictures <data> <shotsFolder>: several pictures per collection item (Entity.ImagePath is the cover,
// Entity.MoreImages the rest). The data (an old single-picture item untouched by a read, the field kept by a version
// that doesn't know it), the item form (several at once, cover, reorder, remove, the limit, unreadable files), editing
// and deleting (only files no item uses are removed, also for a selection and a whole memory, a failed save changes
// nothing), the card badge and the detail card's strip, transfer packages (item, collection, shared pictures, a crafted
// package over the limit, a failure part-way) and the exports (the cover only), with a screenshot of the form.
static partial class Program
{
    static string picWork;

    static async Task PictureScenarios(MainWindow main)
    {
        await Wait(1200);
        GuidedTour.Current?.Stop(TourEnd.Skipped);
        StartSelectWatcher(); // answers and logs message boxes
        picWork = Path.Combine(Path.GetDirectoryName(AppPaths.DataFolder), "pictures-work");
        if (Directory.Exists(picWork))
            Directory.Delete(picWork, true);
        Directory.CreateDirectory(picWork);

        PictureData(main);
        await PictureForm(main);
        await PictureEditAndDelete(main);
        await PictureCardAndDetail(main);
        PictureTransfer(main);
        PictureExports();

        Console.WriteLine("message boxes: " + selectBoxes.Count);
        lock (selectBoxes)
            foreach (string box in selectBoxes)
                Console.WriteLine("  " + box);
    }

    // ---- Helpers ------------------------------------------------------------------

    static readonly Color[] PicturePalette =
    {
        Color.FromRgb(0xC0, 0x39, 0x2B), Color.FromRgb(0x27, 0xAE, 0x60), Color.FromRgb(0x2E, 0x7B, 0xC4),
        Color.FromRgb(0xE6, 0x7E, 0x22), Color.FromRgb(0x8E, 0x44, 0xAD), Color.FromRgb(0x16, 0xA0, 0x85)
    };

    static void ColorPicture(string path, int number)
    {
        DrawingVisual visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(PicturePalette[number % PicturePalette.Length]), null, new Rect(0, 0, 240, 160));
            dc.DrawEllipse(Brushes.White, null, new Point(120, 80), 40 + number % 5 * 4, 40 + number % 5 * 4);
        }
        RenderTargetBitmap bitmap = new RenderTargetBitmap(240, 160, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        PngBitmapEncoder encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (FileStream output = File.Create(path))
            encoder.Save(output);
    }

    // The user's own picture files, in the work folder (not yet copied into the app).
    static string[] OwnPictures(int count, int first = 0) => Enumerable.Range(first, count).Select(i =>
    {
        string path = Path.Combine(picWork, "kép " + i + ".png");
        ColorPicture(path, i);
        return path;
    }).ToArray();

    // A picture already stored in EntityImages, as an item holds it: its file name.
    static string StoredPicture(int number)
    {
        Directory.CreateDirectory(ImageStore.Items.Folder);
        string name = Guid.NewGuid().ToString("N") + ".png";
        ColorPicture(Path.Combine(ImageStore.Items.Folder, name), number);
        return name;
    }

    static Entity PictureItem(string name, int order, params string[] pictures)
    {
        Entity item = new Entity { Id = Guid.NewGuid(), EntityType = "Clothing", Name = name, Order = order, CreatedDate = DateTime.Now };
        EntityImages.SetNames(item, pictures);
        return item;
    }

    static HashSet<string> ItemFiles() => Directory.Exists(ImageStore.Items.Folder)
        ? Directory.GetFiles(ImageStore.Items.Folder).Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase)
        : new HashSet<string>();

    static bool Stored(string name) => File.Exists(ImageStore.Items.PathOf(name));

    static CollectionView OpenCollectionView(MainWindow main, Profile memory)
    {
        Invoke(main, "NavigateTo", memory);
        return (CollectionView)Content(main);
    }

    // Runs `act` on the item form once it is up (the caller's ShowDialog is open meanwhile), then presses Save, or
    // closes the form without saving.
    static void DriveEntityForm(MainWindow main, Func<EntityForm, Task> act, bool save = true)
    {
        _ = main.Dispatcher.BeginInvoke(new Action(async () =>
        {
            EntityForm form = null;
            for (int i = 0; i < 50 && form == null; i++)
            {
                await Wait(100);
                form = Application.Current.Windows.OfType<EntityForm>().FirstOrDefault();
            }
            try
            {
                await act(form);
                if (save)
                    Click((Button)form.FindName("SaveButton"));
                else
                    form.Close();
            }
            catch (Exception e)
            {
                Check(false, "the form driver threw: " + e);
                form?.Close();
            }
        }), DispatcherPriority.Background);
    }

    // A click handled on the spot (Click goes through the automation peer, which runs it a moment later).
    static void ClickNow(Button button)
    {
        if (!button.IsEnabled)
            throw new InvalidOperationException("the button is disabled: " + (button.Content ?? button.Name));
        button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
    }

    static void FormSelect(EntityForm form, int index) => ClickNow((Button)Field<WrapPanel>(form, "pictureStrip").Children[index]);
    static void FormTool(EntityForm form, string button) => ClickNow(Field<Button>(form, button));
    static int FormSelected(EntityForm form) => Field<int>(form, "selectedPicture");
    static string[] FormPicked(EntityForm form) => form.Pictures.Select(p => p.Stored ?? p.PickedFile).ToArray();

    // An older TidyMind's view of an item: it knows ImagePath, and keeps whatever else the file holds.
    class OlderEntity
    {
        public string Name { get; set; }
        public string ImagePath { get; set; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement> ExtensionData { get; set; }
    }

    // ---- The data ------------------------------------------------------------------

    static void PictureData(MainWindow main)
    {
        Console.WriteLine("The data: an old single-picture item, the list, a version that doesn't know it");
        Profile memory = AddMemory(main, "Pictures data", ProfileType.Collection);
        string old = StoredPicture(0);
        string json = "[{\"Id\":\"" + Guid.NewGuid() + "\",\"EntityType\":\"Clothing\",\"Name\":\"Régi\",\"ImagePath\":\"" + old
                      + "\",\"Notes\":null,\"TabId\":\"00000000-0000-0000-0000-000000000000\",\"Order\":0,\"CreatedDate\":\"2026-01-01T00:00:00\",\"Sizes\":[]}]";
        File.WriteAllText(MemoryFiles.Items(memory), json);
        List<Entity> loaded = EntityStore.Load(memory);
        Check(File.ReadAllText(MemoryFiles.Items(memory)) == json, "loading an item from before changes nothing on disk (no migration, no rewrite)");
        Check(loaded[0].ImagePath == old && loaded[0].MoreImages == null && EntityImages.Count(loaded[0]) == 1
              && EntityImages.Names(loaded[0]).SequenceEqual(new[] { old }), "it has one picture, its cover");
        EntityStore.SaveOrWarn(memory, loaded);
        Check(!File.ReadAllText(MemoryFiles.Items(memory)).Contains("MoreImages"), "saved again, an item with one picture is stored exactly as before (no new field)");

        string[] names = { StoredPicture(1), StoredPicture(2), StoredPicture(3) };
        Entity multi = PictureItem("Három", 0, names);
        Check(multi.ImagePath == names[0] && multi.MoreImages.SequenceEqual(names.Skip(1)) && EntityImages.Names(multi).SequenceEqual(names),
            "SetNames: the first is the cover, the rest follow it");
        string text = JsonSerializer.Serialize(new List<Entity> { multi });
        Check(text.Contains("\"MoreImages\":[\"" + names[1] + "\",\"" + names[2] + "\"]"), "stored as ImagePath plus MoreImages");
        Entity back = JsonSerializer.Deserialize<List<Entity>>(text)[0];
        Check(EntityImages.Names(back).SequenceEqual(names), "read back in the same order");
        EntityImages.SetNames(back, new string[0]);
        Check(back.ImagePath == null && back.MoreImages == null, "no pictures: neither field");

        // A version that doesn't know MoreImages keeps it as an unknown field, and writes it back.
        List<OlderEntity> older = JsonSerializer.Deserialize<List<OlderEntity>>(text);
        older[0].Name = "Átnevezve régi verzióval";
        Entity again = JsonSerializer.Deserialize<List<Entity>>(JsonSerializer.Serialize(older))[0];
        Check(again.Name == "Átnevezve régi verzióval" && EntityImages.Names(again).SequenceEqual(names),
            "an older version saving the item keeps the further pictures (unknown field round trip)");
        Check(typeof(Entity).GetProperty(nameof(Entity.ExtensionData)).GetCustomAttributes(typeof(JsonExtensionDataAttribute), false).Length == 1,
            "Entity still keeps unknown fields");
        EntityImages.SetNames(multi, new[] { names[0], "", null, names[1] });
        Check(EntityImages.Names(multi).Count == 2, "...but empty names in a stored list are skipped (Names)");

        // Copying picked files in.
        string[] own = OwnPictures(3);
        Entity target = new Entity { Name = "Cél" };
        HashSet<string> before = ItemFiles();
        Check(EntityImages.TryApply(target, own.Select(f => new PictureSlot { PickedFile = f }).ToList(), out List<string> imported)
              && imported.Count == 3 && imported.Distinct().Count() == 3 && EntityImages.Names(target).SequenceEqual(imported)
              && imported.All(Stored) && imported.All(n => n.EndsWith(".png")), "TryApply: picked files copied in under new names, in order");
        for (int i = 0; i < 3; i++)
            Check(SameBytes(ImageStore.Items.PathOf(imported[i]), own[i]), "picture " + (i + 1) + " is a byte-for-byte copy");
        Check(ItemFiles().Count == before.Count + 3 && !Directory.GetFiles(ImageStore.Items.Folder, "*.tmp").Any(), "three files added, no temporary file");

        before = ItemFiles();
        Entity untouched = new Entity { Name = "Marad", ImagePath = names[0] };
        Check(!EntityImages.TryApply(untouched, new[] { new PictureSlot { PickedFile = own[0] }, new PictureSlot { PickedFile = Path.Combine(picWork, "nincs.png") } },
                  out List<string> none) && none.Count == 0 && untouched.ImagePath == names[0] && ItemFiles().SetEquals(before),
            "a file that can't be copied: the whole change is refused, the copies made so far removed, the item as it was");

        // Only files no item uses.
        Profile other = AddMemory(main, "Pictures other", ProfileType.Collection);
        string a = StoredPicture(1), b = StoredPicture(2), c = StoredPicture(3), d = StoredPicture(4);
        Entity x = PictureItem("X", 0, a, b), y = PictureItem("Y", 1, b, c);
        EntityStore.SaveOrWarn(memory, new List<Entity> { y });
        EntityStore.SaveOrWarn(other, new List<Entity> { PictureItem("Z", 0, c) });
        EntityImages.DeleteUnreferenced(EntityImages.Names(x), new List<Entity> { y });
        Check(!Stored(a) && Stored(b), "X removed: its own picture goes, the one Y also holds stays");
        EntityStore.SaveOrWarn(memory, new List<Entity>());
        EntityImages.DeleteUnreferenced(new[] { b, c });
        Check(!Stored(b) && Stored(c), "Y removed: b goes, c stays (an item in another collection holds it)");
        File.WriteAllText(MemoryFiles.Items(other), "{ unreadable");
        EntityImages.DeleteUnreferenced(new[] { c });
        Check(Stored(c), "a collection's file that can't be read: nothing is removed (can't tell)");
        EntityStore.SaveOrWarn(other, new List<Entity>());
        EntityImages.DeleteUnreferenced(new[] { c });
        Check(!Stored(c), "...and once it's readable and nothing holds c, it goes");
        string own0 = own[0];
        EntityImages.DeleteUnreferenced(new[] { own0, d }, new List<Entity> { PictureItem("W", 0, d) });
        Check(File.Exists(own0) && Stored(d), "the user's own file (a full path) is never deleted; a picture in use stays");
        EntityImages.DeleteUnreferenced(new[] { d });
        Check(!Stored(d), "unused: removed");
    }

    // ---- The item form --------------------------------------------------------------

    static async Task PictureForm(MainWindow main)
    {
        Console.WriteLine("The item form: several at once, cover, reorder, remove, the limit");
        string[] files = OwnPictures(3);
        string[] many = OwnPictures(25, 10);
        string text = Path.Combine(picWork, "jegyzet.txt");
        File.WriteAllText(text, "nem kép");
        string[] picked = null;

        EntityForm form = new EntityForm(EntityTypes.Vehicle);
        Field<TextBox>(form, "nameBox").Text = "Škoda Fabia";
        DriveEntityForm(main, async f =>
        {
            Check(f.Pictures.Count == 0 && Field<TextBlock>(f, "imagePathText").Text == Loc.T("Entity.NoPictures")
                  && Field<StackPanel>(f, "pictureTools").Visibility == Visibility.Collapsed, "a new item: no pictures, no tools");
            f.AddPictures(files);
            await Wait(300);
            WrapPanel strip = Field<WrapPanel>(f, "pictureStrip");
            Check(f.Pictures.Count == 3 && strip.Children.Count == 3 && FormSelected(f) == 0
                  && Field<TextBlock>(f, "imagePathText").Text == "3 pictures", "three picked at once: three thumbnails, the first selected");
            Check(f.Pictures.All(p => p.Preview != null && p.PickedFile != null && p.Stored == null), "each is previewed and kept as a picked file");
            Check(!Field<Button>(f, "coverBtn").IsEnabled && !Field<Button>(f, "earlierBtn").IsEnabled && Field<Button>(f, "laterBtn").IsEnabled,
                "the cover can't be made cover or moved earlier");
            RenderWindow(f, "pictures-01-editor-3-en");

            FormSelect(f, 2);
            Check(FormSelected(f) == 2 && ReferenceEquals(Field<Image>(f, "imagePreview").Source, f.Pictures[2].Preview), "a thumbnail clicked: shown larger");
            FormTool(f, "coverBtn");
            Check(FormPicked(f).SequenceEqual(new[] { files[2], files[0], files[1] }) && FormSelected(f) == 0, "Make cover: it moves to the front");
            FormTool(f, "laterBtn");
            Check(FormPicked(f).SequenceEqual(new[] { files[0], files[2], files[1] }) && FormSelected(f) == 1, "Move later: one step, still selected");
            FormTool(f, "earlierBtn");
            FormTool(f, "laterBtn");
            FormSelect(f, 2);
            FormTool(f, "removePictureBtn");
            Check(FormPicked(f).SequenceEqual(new[] { files[0], files[2] }) && FormSelected(f) == 1, "Remove: the picture goes, the next one is selected");
            FormTool(f, "earlierBtn");
            Check(FormPicked(f).SequenceEqual(new[] { files[2], files[0] }), "Move earlier");

            int boxes = selectBoxes.Count;
            f.AddPictures(many);
            await Wait(300);
            Check(f.Pictures.Count == EntityImages.MaxPictures && FormSelected(f) == 2, "25 more: it stops at " + EntityImages.MaxPictures + ", the first new one selected");
            Check(selectBoxes.Skip(boxes).Any(b => b.Contains(Loc.F("Entity.Pictures.Limit", EntityImages.MaxPictures))), "the limit is said: " + selectBoxes.LastOrDefault());
            boxes = selectBoxes.Count;
            f.AddPictures(new[] { files[1] });
            await Wait(200);
            Check(f.Pictures.Count == EntityImages.MaxPictures && selectBoxes.Skip(boxes).Any(b => b.Contains(Loc.F("Entity.Pictures.Limit", EntityImages.MaxPictures))),
                "full: one more is refused with the same message");
            while (f.Pictures.Count > 4)
            {
                FormSelect(f, f.Pictures.Count - 1);
                FormTool(f, "removePictureBtn");
            }
            boxes = selectBoxes.Count;
            f.AddPictures(new[] { text, files[1] });
            await Wait(300);
            Check(f.Pictures.Count == 5 && selectBoxes.Skip(boxes).Any(b => b.Contains("jegyzet.txt")),
                "a file that isn't a picture is left out and named; the picture next to it is added");
            picked = FormPicked(f);
        });
        bool? ok = form.ShowDialog();
        Check(ok == true && form.ResultEntity != null && picked != null, "Save closes the form with the result");

        Entity result = form.ResultEntity;
        HashSet<string> before = ItemFiles();
        Check(EntityImages.TryApply(result, form.Pictures, out List<string> imported) && imported.Count == 5 && ItemFiles().Count == before.Count + 5,
            "the caller copies the five picked files in");
        Check(Enumerable.Range(0, 5).All(i => SameBytes(ImageStore.Items.PathOf(EntityImages.Names(result)[i]), picked[i])), "in the order the form left them");
        foreach (string name in imported)
            EntityImages.DeleteCopy(name);

        // An item that has pictures: the form starts with them, the cover first.
        string[] stored = { StoredPicture(1), StoredPicture(2), StoredPicture(3) };
        Entity existing = PictureItem("Van", 0, stored);
        EntityForm edit = new EntityForm(existing);
        DriveEntityForm(main, async f =>
        {
            await Wait(200);
            Check(FormPicked(f).SequenceEqual(stored) && f.Pictures.All(p => p.Preview != null), "editing: the item's pictures are listed, cover first");
            Check(Field<Button>(f, "removePictureBtn").IsEnabled, "and can be removed");
        }, save: false);
        edit.ShowDialog();
        Check(EntityImages.Names(existing).SequenceEqual(stored), "closed without saving: the item is untouched");
        foreach (string name in stored)
            EntityImages.DeleteCopy(name);
    }

    // ---- Editing and deleting ----------------------------------------------------------

    static async Task PictureEditAndDelete(MainWindow main)
    {
        Console.WriteLine("Editing and deleting: only files nothing else uses");
        Profile memory = AddMemory(main, "Pictures edit", ProfileType.Collection);
        string[] p = Enumerable.Range(0, 8).Select(StoredPicture).ToArray();
        Entity a = PictureItem("A", 0, p[0], p[1], p[2]), b = PictureItem("B", 1, p[2], p[3]); // B shares p[2] with A
        EntityStore.SaveOrWarn(memory, new List<Entity> { a, b });
        CollectionView view = OpenCollectionView(main, memory);
        await Wait(600);

        Entity Current(string name) => EntityStore.Load(memory).First(e => e.Name == name);
        DriveEntityForm(main, async f =>
        {
            await Wait(200);
            FormSelect(f, 1);
            FormTool(f, "removePictureBtn"); // p[1]
            FormSelect(f, 1);
            FormTool(f, "removePictureBtn"); // p[2], which B also holds
        });
        Invoke(view, "EditEntity", Field<List<Entity>>(view, "entities").First(e => e.Name == "A"));
        await Wait(300);
        Check(EntityImages.Names(Current("A")).SequenceEqual(new[] { p[0] }) && !Stored(p[1]) && Stored(p[2]) && Stored(p[0]),
            "edit, remove two: the one nobody else holds is deleted, B's shared one stays");
        Check(EntityImages.Names(Current("B")).SequenceEqual(new[] { p[2], p[3] }), "B untouched");

        string[] fresh = OwnPictures(2, 30);
        HashSet<string> before = ItemFiles();
        DriveEntityForm(main, async f =>
        {
            await Wait(200);
            f.AddPictures(fresh);
            FormSelect(f, 2);
            FormTool(f, "coverBtn");
        });
        Invoke(view, "EditEntity", Field<List<Entity>>(view, "entities").First(e => e.Name == "A"));
        await Wait(300);
        List<string> names = EntityImages.Names(Current("A"));
        Check(names.Count == 3 && SameBytes(ImageStore.Items.PathOf(names[0]), fresh[1]) && names[1] == p[0]
              && SameBytes(ImageStore.Items.PathOf(names[2]), fresh[0]) && names.All(Stored) && ItemFiles().Count == before.Count + 2,
            "edit, add two and make the last the cover: copies made, order kept, the old picture still there");

        // A save that fails changes nothing: the new copy is taken out again, the removed picture is not deleted.
        before = ItemFiles();
        string entitiesBefore = File.ReadAllText(MemoryFiles.Items(memory));
        string[] more = OwnPictures(1, 40);
        DriveEntityForm(main, async f =>
        {
            await Wait(200);
            f.AddPictures(more);
            FormSelect(f, 0);
            FormTool(f, "removePictureBtn");
        });
        int boxes = selectBoxes.Count;
        using (new FileStream(MemoryFiles.Items(memory), FileMode.Open, FileAccess.Read, FileShare.Read))
            Invoke(view, "EditEntity", Field<List<Entity>>(view, "entities").First(e => e.Name == "A"));
        await Wait(300);
        Check(File.ReadAllText(MemoryFiles.Items(memory)) == entitiesBefore && ItemFiles().SetEquals(before) && selectBoxes.Count > boxes,
            "a save that can't be written: said, the file and the pictures exactly as before (the new copy removed, nothing deleted)");
        Invoke(view, "ReloadEntities");

        // A selection of items: all their pictures go, except what a remaining item holds.
        Profile bulk = AddMemory(main, "Pictures bulk", ProfileType.Collection);
        string[] q = Enumerable.Range(0, 9).Select(StoredPicture).ToArray();
        Entity d = PictureItem("D", 0, q[0], q[1]), e = PictureItem("E", 1, q[2], q[3], q[8]), f2 = PictureItem("F", 2, q[8]), g = PictureItem("G", 3, q[4]);
        EntityStore.SaveOrWarn(bulk, new List<Entity> { d, e, f2, g });
        CollectionView bulkView = OpenCollectionView(main, bulk);
        await Wait(600);
        CardSelectionController<Entity> selection = Field<CardSelectionController<Entity>>(bulkView, "selection");
        List<Entity> shown = Field<List<Entity>>(bulkView, "entities");
        selection.Click(shown[0], ModifierKeys.Control);
        selection.Click(shown[1], ModifierKeys.Control);
        Check(selection.Count == 2, "Ctrl-click: two items selected");
        selection.DeleteSelected();
        await Wait(400);
        Check(EntityStore.Load(bulk).Select(x => x.Name).SequenceEqual(new[] { "F", "G" }), "both deleted");
        Check(!Stored(q[0]) && !Stored(q[1]) && !Stored(q[2]) && !Stored(q[3]), "every picture of the deleted items is deleted");
        Check(Stored(q[8]) && Stored(q[4]), "the one a remaining item holds stays, and the other items' own");
        Invoke(bulkView, "DeleteEntity", Field<List<Entity>>(bulkView, "entities").First(x => x.Name == "F"));
        await Wait(300);
        Check(!Stored(q[8]) && Stored(q[4]), "deleting the last holder (one item): the shared picture goes too");

        // A whole memory.
        Invoke(main, "NavigateHome");
        Profile gone = AddMemory(main, "Pictures gone", ProfileType.Collection), stays = AddMemory(main, "Pictures stays", ProfileType.Collection);
        string own = StoredPicture(5), shared = StoredPicture(6);
        EntityStore.SaveOrWarn(gone, new List<Entity> { PictureItem("H", 0, own, shared) });
        EntityStore.SaveOrWarn(stays, new List<Entity> { PictureItem("I", 0, shared) });
        Invoke(main, "RemoveMemories", new List<Profile> { gone });
        await Wait(300);
        Check(!Stored(own) && Stored(shared), "deleting a memory: its pictures go, the one another collection holds stays");
        Invoke(main, "RemoveMemories", new List<Profile> { stays });
        await Wait(300);
        Check(!Stored(shared), "...and goes with the last memory that held it");
        Invoke(main, "NavigateHome");
    }

    // ---- The card and the detail card ----------------------------------------------------

    static async Task PictureCardAndDetail(MainWindow main)
    {
        Console.WriteLine("The card badge and the detail card");
        Profile memory = AddMemory(main, "Pictures cards", ProfileType.Collection);
        string[] p = Enumerable.Range(0, 5).Select(StoredPicture).ToArray();
        Entity three = PictureItem("Három", 0, p[0], p[1], p[2]), one = PictureItem("Egy", 1, p[3]), none = PictureItem("Nulla", 2);
        EntityStore.SaveOrWarn(memory, new List<Entity> { three, one, none });
        CollectionView view = OpenCollectionView(main, memory);
        await Wait(700);

        List<TextBlock> badges = Descendants(view).OfType<TextBlock>().Where(t => t.Text == "").ToList();
        Check(badges.Count == 1, "only the item with several pictures has a badge");
        TextBlock count = ((StackPanel)badges[0].Parent).Children.OfType<TextBlock>().Last();
        Check(count.Text == "3" && ((FrameworkElement)((FrameworkElement)badges[0].Parent).Parent).ToolTip.ToString() == "3 pictures", "it counts three: " + count.Text);

        view.Reveal(three.Id);
        await Wait(500);
        WrapPanel strip = (WrapPanel)view.FindName("DetailStrip");
        Image big = (Image)view.FindName("DetailImage");
        Check(((FrameworkElement)view.FindName("DetailStripScroll")).Visibility == Visibility.Visible && strip.Children.Count == 3
              && big.Source == EntityImages.Load(p[0]), "the detail card shows the cover and a strip of three");
        ClickNow((Button)strip.Children[1]);
        Check(Field<int>(view, "detailPicture") == 1 && big.Source == EntityImages.Load(p[1]) && strip.Children.Count == 3, "a thumbnail clicked: that picture, larger");
        ClickNow((Button)strip.Children[2]);
        Check(big.Source == EntityImages.Load(p[2]), "and the next");

        Loc.Switch(Loc.Hungarian);
        await Wait(400);
        Check(big.Source == EntityImages.Load(p[2]) && strip.Children.Count == 3 && Field<int>(view, "detailPicture") == 2,
            "a language switch keeps the picture shown");
        ToolTipText(view, "hu: 3 kép");
        Loc.Switch(Loc.SerbianLatin);
        await Wait(300);
        ToolTipText(view, "sr: 3 slike");
        Loc.Switch(Loc.English);
        await Wait(300);

        view.Reveal(one.Id);
        await Wait(500);
        Check(((FrameworkElement)view.FindName("DetailStripScroll")).Visibility != Visibility.Visible && big.Source == EntityImages.Load(p[3]),
            "one picture: no strip");
        view.Reveal(none.Id);
        await Wait(500);
        Check(big.Source == null && ((FrameworkElement)view.FindName("DetailStripScroll")).Visibility != Visibility.Visible, "no picture: none, no strip");
        Invoke(main, "NavigateHome");
    }

    static void ToolTipText(CollectionView view, string expected)
    {
        TextBlock badge = Descendants(view).OfType<TextBlock>().First(t => t.Text == "");
        string tip = ((FrameworkElement)((FrameworkElement)badge.Parent).Parent).ToolTip?.ToString();
        Check(tip == expected.Substring(expected.IndexOf(' ') + 1) || tip == expected, "the badge's tip in the card still says " + expected + " (" + tip + ")");
    }

    // ---- Transfer ---------------------------------------------------------------------------

    static void PictureTransfer(MainWindow main)
    {
        Console.WriteLine("Transfer packages with several pictures");
        Profile source = AddMemory(main, "Pictures source", ProfileType.Collection);
        Profile target = AddMemory(main, "Pictures target", ProfileType.Collection);
        string[] p = Enumerable.Range(0, 6).Select(StoredPicture).ToArray();
        Entity car = PictureItem("Škoda – „Đurđa”", 0, p[0], p[1], p[2]);
        car.EntityType = "Vehicle";
        string package = Path.Combine(picWork, "car" + TransferPackage.Extension);
        TransferPackage.ExportItem(car, package, null, CancellationToken.None);
        using (ZipArchive zip = ZipFile.OpenRead(package))
        {
            JsonObject manifest = (JsonObject)JsonNode.Parse(new StreamReader(zip.GetEntry("manifest.json").Open()).ReadToEnd());
            string itemJson = new StreamReader(zip.GetEntry("item.json").Open()).ReadToEnd();
            Check(zip.Entries.Count(e => e.FullName.StartsWith("files/items/")) == 3 && (int)manifest["Format"] == 2 && (int)manifest["Counts"]["Pictures"] == 3,
                "an item package holds all three pictures, format 2, 3 pictures counted");
            Check(itemJson.Contains("MoreImages") && !itemJson.Contains(":\\\\"), "item.json lists them, with no path");
        }
        OpenedPackage opened = TransferPackage.Open(package);
        Check(opened.IsItem && opened.Pictures == 3, "the preview counts 3 pictures");
        Profile into = ImportItem(opened, target, main);
        Entity copy = EntityStore.Load(target).Single();
        List<string> names = EntityImages.Names(copy);
        Check(into == target && copy.Id != car.Id && names.Count == 3 && names.Intersect(EntityImages.Names(car)).Count() == 0 && names.All(Stored),
            "imported: three pictures under new names");
        for (int i = 0; i < 3; i++)
            Check(SameBytes(ImageStore.Items.PathOf(names[i]), ImageStore.Items.PathOf(p[i])), "picture " + (i + 1) + " is a copy of the original, in the same place");

        // A picture the item lost: the next one is the cover.
        File.Delete(ImageStore.Items.PathOf(p[0]));
        string gapPackage = Path.Combine(picWork, "gap" + TransferPackage.Extension);
        TransferPackage.ExportItem(car, gapPackage, null, CancellationToken.None);
        Check(TransferPackage.Open(gapPackage).Pictures == 2 && EntityImages.Names(car).Count == 3, "a missing file is left out of the package, the item itself untouched");
        Entity gap = EntityStore.Load(ImportItem(TransferPackage.Open(gapPackage), target, main)).Last();
        Check(EntityImages.Names(gap).Count == 2 && SameBytes(ImageStore.Items.PathOf(gap.ImagePath), ImageStore.Items.PathOf(p[1])), "...and the next picture is the cover");

        // An item with one picture is as before: no new field.
        Entity single = PictureItem("Egy", 1, p[3]);
        string singlePackage = Path.Combine(picWork, "single" + TransferPackage.Extension);
        TransferPackage.ExportItem(single, singlePackage, null, CancellationToken.None);
        using (ZipArchive zip = ZipFile.OpenRead(singlePackage))
            Check(!new StreamReader(zip.GetEntry("item.json").Open()).ReadToEnd().Contains("MoreImages"), "one picture: item.json as before, without the new field");
        Check(EntityImages.Names(EntityStore.Load(ImportItem(TransferPackage.Open(singlePackage), target, main)).Last()).Count == 1, "...and it imports");

        // A collection: the format is the oldest that holds it; shared pictures get a copy for each item.
        Profile closet = AddMemory(main, "Pictures closet", ProfileType.Collection), plain = AddMemory(main, "Pictures plain", ProfileType.Collection);
        TabStore.TryLoadOrCreate(closet, new[] { Guid.NewGuid() }, out List<MemoryTab> tabs, out _);
        TabStore.TryLoadOrCreate(plain, new[] { Guid.NewGuid() }, out List<MemoryTab> plainTabs, out _);
        Entity first = PictureItem("Első", 0, p[1], p[2], p[4]), second = PictureItem("Második", 1, p[4], p[5]), third = PictureItem("Harmadik", 2);
        first.TabId = second.TabId = third.TabId = tabs[0].Id;
        Entity lone = PictureItem("Magányos", 0, p[5]);
        lone.TabId = plainTabs[0].Id;
        string collectionPackage = Path.Combine(picWork, "closet" + TransferPackage.Extension), plainPackage = Path.Combine(picWork, "plain" + TransferPackage.Extension);
        TransferPackage.ExportCollection(closet, tabs, new List<Entity> { first, second, third }, collectionPackage, null, CancellationToken.None);
        TransferPackage.ExportCollection(plain, plainTabs, new List<Entity> { lone }, plainPackage, null, CancellationToken.None);
        Check(ManifestFormat(collectionPackage) == 2 && ManifestFormat(plainPackage) == 1,
            "a collection with an item of several pictures is format 2 (older versions refuse it whole); with single pictures, format 1 as before");
        OpenedPackage collection = TransferPackage.Open(collectionPackage);
        Check(collection.Items == 3 && collection.Pictures == 5, "the preview: 3 items, 5 pictures");
        Profile copied;
        using (StagedImport staged = TransferPackage.Stage(collection, null, CancellationToken.None))
            copied = TransferPackage.CommitCollection(staged, Profiles(main), "#2D7DD2");
        List<Entity> back = EntityStore.Load(copied);
        Entity c1 = back.Single(e => e.Name == "Első"), c2 = back.Single(e => e.Name == "Második");
        Check(EntityImages.Names(c1).Count == 3 && EntityImages.Names(c2).Count == 2 && EntityImages.Names(back.Single(e => e.Name == "Harmadik")).Count == 0,
            "the collection comes back with 3, 2 and 0 pictures");
        Check(EntityImages.Names(c1).Concat(EntityImages.Names(c2)).Distinct().Count() == 5 && EntityImages.Names(c1).Concat(EntityImages.Names(c2)).All(Stored),
            "the picture two items shared is two files now, one for each");
        Check(SameBytes(ImageStore.Items.PathOf(EntityImages.Names(c1)[2]), ImageStore.Items.PathOf(EntityImages.Names(c2)[0])), "...with the same content");
        List<Entity> without = back.Where(e => e != c1).ToList();
        EntityStore.SaveOrWarn(copied, without);
        EntityImages.DeleteUnreferenced(EntityImages.Names(c1), without);
        Check(EntityImages.Names(c2).All(Stored) && !EntityImages.Names(c1).Any(Stored), "deleting one item leaves the other's pictures, even those it was copied with");
        using (StagedImport staged = TransferPackage.Stage(TransferPackage.Open(plainPackage), null, CancellationToken.None))
            Check(EntityImages.Count(EntityStore.Load(TransferPackage.CommitCollection(staged, Profiles(main), "#2D7DD2")).Single()) == 1, "an older-style collection package still imports");

        // A package from elsewhere with more pictures than an item can hold: the rest are dropped.
        string crafted = Path.Combine(picWork, "crafted" + TransferPackage.Extension);
        File.Copy(package, crafted, true);
        byte[] bytes = File.ReadAllBytes(ImageStore.Items.PathOf(p[1]));
        using (ZipArchive zip = ZipFile.Open(crafted, ZipArchiveMode.Update))
        {
            string itemText;
            using (StreamReader reader = new StreamReader(zip.GetEntry("item.json").Open()))
                itemText = reader.ReadToEnd();
            JsonObject item = (JsonObject)JsonNode.Parse(itemText);
            JsonArray extra = new JsonArray();
            for (int i = 0; i < 30; i++)
            {
                string name = "x" + i.ToString("D2") + ".png";
                extra.Add(name);
                using (Stream s = zip.CreateEntry("files/items/" + name).Open())
                    s.Write(bytes, 0, bytes.Length);
            }
            item["MoreImages"] = extra;
            zip.GetEntry("item.json").Delete();
            using (StreamWriter w = new StreamWriter(zip.CreateEntry("item.json").Open()))
                w.Write(item.ToJsonString());
        }
        OpenedPackage big = TransferPackage.Open(crafted);
        Check(big.Pictures == EntityImages.MaxPictures && big.Entities[0].MoreImages.Count == EntityImages.MaxPictures - 1, "30 pictures in a package: the preview counts " + big.Pictures);
        Entity capped = EntityStore.Load(ImportItem(big, target, main)).Last();
        Check(EntityImages.Count(capped) == EntityImages.MaxPictures && EntityImages.Names(capped).All(Stored), "...and the import keeps " + EntityImages.MaxPictures);

        // A failure part-way leaves the data folder as it was.
        Profile locked = AddMemory(main, "Pictures locked", ProfileType.Collection);
        File.WriteAllText(MemoryFiles.Items(locked), "[]");
        List<string> filesBefore = DataFiles();
        string message = null;
        using (new FileStream(MemoryFiles.Items(locked), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try
            {
                ImportItem(TransferPackage.Open(package), locked, main);
            }
            catch (TransferException e)
            {
                message = e.Message;
            }
        }
        Check(message != null && DataFiles().SequenceEqual(filesBefore), "an import that can't be written: refused, all three pictures taken out again, the data folder exactly as before");
        File.WriteAllText(MemoryFiles.Items(locked), "{ broken");
        filesBefore = DataFiles();
        message = null;
        try
        {
            ImportItem(TransferPackage.Open(package), locked, main);
        }
        catch (TransferException e)
        {
            message = e.Message;
        }
        Check(message != null && DataFiles().SequenceEqual(filesBefore) && !Directory.EnumerateDirectories(AppPaths.DataFolder, "Import-*").Any(),
            "an unreadable target: refused, nothing added, no staging folder left");
    }

    static int ManifestFormat(string package)
    {
        using (ZipArchive zip = ZipFile.OpenRead(package))
            return (int)((JsonObject)JsonNode.Parse(new StreamReader(zip.GetEntry("manifest.json").Open()).ReadToEnd()))["Format"];
    }

    // ---- Exports ---------------------------------------------------------------------------------

    static void PictureExports()
    {
        Console.WriteLine("Exports: the cover only");
        string[] p = Enumerable.Range(0, 3).Select(StoredPicture).ToArray();
        Entity item = PictureItem("Három kép", 0, p);
        foreach (ExportFormat format in new[] { ExportFormat.Excel, ExportFormat.Docx, ExportFormat.Pdf })
        {
            string path = Path.Combine(picWork, "export-" + format + (format == ExportFormat.Excel ? ".xlsx" : format == ExportFormat.Docx ? ".docx" : ".pdf"));
            try
            {
                ExportService.ExportEntity(item, path, format);
                Check(File.Exists(path) && new FileInfo(path).Length > 0, format + ": exported");
            }
            catch (Exception e)
            {
                Check(false, format + ": export threw " + e.Message);
            }
        }
        string docx = Path.Combine(picWork, "export-Docx.docx");
        using (WordprocessingDocument document = WordprocessingDocument.Open(docx, false))
            Check(document.MainDocumentPart.ImageParts.Count() == 1, "Word: one picture, the cover, as before");
    }
}
