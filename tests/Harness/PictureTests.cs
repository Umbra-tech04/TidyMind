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
// nothing), the card badge and the item window's pictures, transfer packages (item, collection, shared pictures, a crafted
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
        await PictureArrowsBrowse(main);
        await PictureBrowsing(main);
        await PictureForm(main);
        await PictureEditAndDelete(main);
        await PictureCardAndDetail(main);
        await PictureReviewFixes(main);
        PictureTransfer(main);
        PictureExports();

        Console.WriteLine("message boxes: " + selectBoxes.Count);
        lock (selectBoxes)
            foreach (string box in selectBoxes)
                Console.WriteLine("  " + box);
    }

    // ---- Helpers ------------------------------------------------------------------

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

    static PictureBrowser FormBrowser(EntityForm form) => Field<PictureBrowser>(form, "pictureBrowser");
    static WrapPanel StripOf(PictureBrowser browser) => Field<WrapPanel>(browser, "strip");
    static Image PreviewOf(PictureBrowser browser) => Field<Image>(browser, "preview");
    static ScrollViewer StripScrollOf(PictureBrowser browser) => Field<ScrollViewer>(browser, "stripScroll");
    static Button ArrowOf(PictureBrowser browser, bool forward) => Field<Button>(browser, forward ? "next" : "previous");

    // The one source of truth and everything drawn from it agree: the browser's selection is `selected`, the large
    // picture is that one, exactly that thumbnail is framed, the first and only the first says Cover, and the arrows
    // are off exactly at the ends.
    static bool InSync(PictureBrowser browser, IReadOnlyList<ImageSource> pictures, int selected)
    {
        WrapPanel strip = StripOf(browser);
        if (browser.Selected != selected || strip.Children.Count != pictures.Count || !ReferenceEquals(PreviewOf(browser).Source, pictures[selected]))
            return false;
        for (int i = 0; i < pictures.Count; i++)
        {
            Grid thumb = (Grid)((Button)strip.Children[i]).Content;
            Border frame = (Border)thumb.Children[0];
            if ((frame.BorderThickness.Left == 2) != (i == selected) || !ReferenceEquals(((Image)frame.Child).Source, pictures[i])
                || (thumb.Children.Count == 2) != (i == 0))
                return false;
        }
        return ArrowOf(browser, false).IsEnabled == (selected > 0) && ArrowOf(browser, true).IsEnabled == (selected < pictures.Count - 1);
    }

    static bool FormInSync(EntityForm form) =>
        FormSelected(form) == FormBrowser(form).Selected && InSync(FormBrowser(form), form.Pictures.Select(p => p.Preview).ToList(), FormSelected(form));

    static void FormSelect(EntityForm form, int index) => ClickNow((Button)StripOf(FormBrowser(form)).Children[index]);
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
        EntityImages.Apply(target, own.Select(f => new PictureSlot { PickedFile = f }).ToList(), out List<string> imported, out List<string> failed);
        Check(failed.Count == 0 && imported.Count == 3 && imported.Distinct().Count() == 3 && EntityImages.Names(target).SequenceEqual(imported)
              && imported.All(Stored) && imported.All(n => n.EndsWith(".png")), "Apply: picked files copied in under new names, in order");
        for (int i = 0; i < 3; i++)
            Check(SameBytes(ImageStore.Items.PathOf(imported[i]), own[i]), "picture " + (i + 1) + " is a byte-for-byte copy");
        Check(ItemFiles().Count == before.Count + 3 && !Directory.GetFiles(ImageStore.Items.Folder, "*.tmp").Any(), "three files added, no temporary file");

        before = ItemFiles();
        string gone = Path.Combine(picWork, "nincs.png");
        Entity partly = new Entity { Name = "Részben", ImagePath = names[0], MoreImages = new List<string> { names[1] } };
        EntityImages.Apply(partly, new[] { new PictureSlot { PickedFile = gone }, new PictureSlot { Stored = names[1] }, new PictureSlot { PickedFile = own[0] } },
            out List<string> made, out List<string> notCopied);
        Check(notCopied.SequenceEqual(new[] { gone }) && made.Count == 1 && EntityImages.Names(partly).SequenceEqual(new[] { names[1], made[0] })
              && ItemFiles().Count == before.Count + 1,
            "a picked file that can't be copied is left out and named; the rest of the list (names[0] removed, the order) still applies");

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

    // ---- The reported bug: the arrows next to the large picture --------------------------------

    // The user's steps: select a picture, make it the cover, then press the right arrow. The arrows must browse
    // (the selection moves on, the order stays); they must never carry the selected picture along.
    static async Task PictureArrowsBrowse(MainWindow main)
    {
        Console.WriteLine("The arrows by the large picture browse; they don't reorder");
        string[] files = OwnPictures(3, 50);
        EntityForm form = new EntityForm(EntityTypes.Vehicle);
        Field<TextBox>(form, "nameBox").Text = "Nyilak";
        DriveEntityForm(main, async f =>
        {
            f.AddPictures(files);
            await Wait(300);
            FormSelect(f, 1);
            FormTool(f, "coverBtn");
            string[] order = FormPicked(f);
            Check(order.SequenceEqual(new[] { files[1], files[0], files[2] }) && FormSelected(f) == 0, "picture 2 made the cover: first, selected");
            Button right = Descendants(f).OfType<Button>().First(b => b.Content as string == "▶" || (b.Content as TextBlock)?.Text == "\uE76C");
            ClickNow(right);
            Check(FormPicked(f).SequenceEqual(order), "the right arrow leaves the order alone (it was: " + string.Join(", ", FormPicked(f).Select(Path.GetFileName)) + ")");
            Check(FormSelected(f) == 1, "...and selects the next picture (selected: " + FormSelected(f) + ")");
        }, save: false);
        form.ShowDialog();
    }

    // Browsing and reordering are separate: the arrows and Left / Right move the selection only; Move earlier /
    // later, a drag in the strip and Make cover change the order and keep the moved picture selected; Remove selects
    // a neighbour. After every step the selection, the large picture and the framed thumbnail agree.
    static async Task PictureBrowsing(MainWindow main)
    {
        Console.WriteLine("Browsing and reordering in the item form");
        string[] f = OwnPictures(4, 60);
        EntityForm form = new EntityForm(EntityTypes.Clothing);
        Field<TextBox>(form, "nameBox").Text = "Böngészés";
        DriveEntityForm(main, async e =>
        {
            PictureBrowser browser = FormBrowser(e);
            Check(browser.Visibility == Visibility.Collapsed && Field<WrapPanel>(e, "pictureTools").Visibility == Visibility.Collapsed,
                "empty: no picture area, no tools");
            e.AddPictures(f.Take(1));
            await Wait(200);
            Check(FormInSync(e) && !ArrowOf(browser, true).IsVisible && !ArrowOf(browser, false).IsVisible && StripScrollOf(browser).IsVisible
                  && !Field<Button>(e, "coverBtn").IsVisible && !Field<Button>(e, "laterBtn").IsVisible && Field<Button>(e, "removePictureBtn").IsVisible,
                "one picture: framed with its Cover badge, no arrows, only Remove");
            Check(!browser.Browse(1) && !browser.Browse(-1), "one picture: nothing to browse");
            e.AddPictures(f.Skip(1));
            await Wait(200);
            Check(FormSelected(e) == 1 && FormInSync(e) && ArrowOf(browser, true).IsVisible, "three more added: the first new one selected, arrows shown");
            string[] order = FormPicked(e);

            ClickNow(ArrowOf(browser, true));
            Check(FormSelected(e) == 2 && FormInSync(e) && FormPicked(e).SequenceEqual(order), "next arrow: the next picture, the order unchanged");
            ClickNow(ArrowOf(browser, true));
            Check(FormSelected(e) == 3 && FormInSync(e) && !ArrowOf(browser, true).IsEnabled && !browser.Browse(1), "at the last: next is off");
            ClickNow(ArrowOf(browser, false));
            ClickNow(ArrowOf(browser, false));
            ClickNow(ArrowOf(browser, false));
            Check(FormSelected(e) == 0 && FormInSync(e) && !ArrowOf(browser, false).IsEnabled && FormPicked(e).SequenceEqual(order),
                "back to the first with the previous arrow: back is off, the order unchanged");
            Button Thumb(int i) => (Button)StripOf(browser).Children[i]; // drawn anew after every change
            PressKey(Thumb(0), Key.Right);
            PressKey(Thumb(1), Key.Right);
            Check(FormSelected(e) == 2 && FormInSync(e) && FormPicked(e).SequenceEqual(order), "Right key twice: the third, the order unchanged");
            PressKey(Thumb(2), Key.Left);
            Check(FormSelected(e) == 1 && FormInSync(e) && FormPicked(e).SequenceEqual(order), "Left key: back one");
            PressKey(Field<TextBox>(e, "nameBox"), Key.Left);
            Check(FormSelected(e) == 1, "Left in a text box is the text box's, not the pictures'");

            FormTool(e, "laterBtn");
            Check(FormPicked(e).SequenceEqual(new[] { order[0], order[2], order[1], order[3] }) && FormSelected(e) == 2 && FormInSync(e),
                "Move later: the selected picture moves one place, and stays selected (framed) there");
            FormTool(e, "earlierBtn");
            Check(FormPicked(e).SequenceEqual(order) && FormSelected(e) == 1 && FormInSync(e), "Move earlier: back, still selected");

            // A drag in the strip: the fourth dropped before the first is the cover now, and selected.
            WrapPanel strip = StripOf(browser);
            Field<Action<Button, Button, bool>>(Field<object>(browser, "drag"), "onDrop")((Button)strip.Children[3], (Button)strip.Children[0], false);
            Check(FormPicked(e).SequenceEqual(new[] { order[3], order[0], order[1], order[2] }) && FormSelected(e) == 0 && FormInSync(e),
                "dragged before the first: it is the cover now, and selected");
            strip = StripOf(browser);
            Field<Action<Button, Button, bool>>(Field<object>(browser, "drag"), "onDrop")((Button)strip.Children[0], (Button)strip.Children[3], true);
            Check(FormPicked(e).SequenceEqual(order) && FormSelected(e) == 3 && FormInSync(e), "dragged after the last: last, and selected");

            FormSelect(e, 2);
            FormTool(e, "coverBtn");
            Check(FormPicked(e).SequenceEqual(new[] { order[2], order[0], order[1], order[3] }) && FormSelected(e) == 0 && FormInSync(e)
                  && !Field<Button>(e, "coverBtn").IsEnabled, "Make cover: first in the list, selected, with the Cover badge");

            FormSelect(e, 1);
            FormTool(e, "removePictureBtn");
            Check(FormPicked(e).SequenceEqual(new[] { order[2], order[1], order[3] }) && FormSelected(e) == 1 && FormInSync(e),
                "Remove in the middle: the next one is selected");
            FormSelect(e, 2);
            FormTool(e, "removePictureBtn");
            Check(FormPicked(e).SequenceEqual(new[] { order[2], order[1] }) && FormSelected(e) == 1 && FormInSync(e), "Remove the last: the one before it");
            FormTool(e, "removePictureBtn");
            Check(FormPicked(e).Length == 1 && FormSelected(e) == 0 && FormInSync(e) && !ArrowOf(browser, true).IsVisible, "down to one: no arrows");
            FormTool(e, "removePictureBtn");
            Check(e.Pictures.Count == 0 && FormSelected(e) == -1 && browser.Visibility == Visibility.Collapsed
                  && Field<WrapPanel>(e, "pictureTools").Visibility == Visibility.Collapsed && Field<TextBlock>(e, "imagePathText").Text == Loc.T("Entity.NoPictures"),
                "all removed: empty, no dead controls");
        }, save: false);
        form.ShowDialog();

        // Reopening an item: its cover selected, everything in sync.
        string[] stored = { StoredPicture(1), StoredPicture(2), StoredPicture(3) };
        EntityForm reopen = new EntityForm(PictureItem("Újra", 0, stored));
        DriveEntityForm(main, async e =>
        {
            await Wait(200);
            Check(FormPicked(e).SequenceEqual(stored) && FormSelected(e) == 0 && FormInSync(e) && ArrowOf(FormBrowser(e), true).IsVisible,
                "reopened: the cover selected and shown, the arrows there");
            RenderWindow(e, "pictures-02-editor-reopened-en");
        }, save: false);
        reopen.ShowDialog();
        foreach (string name in stored)
            EntityImages.DeleteCopy(name);
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
                  && Field<WrapPanel>(f, "pictureTools").Visibility == Visibility.Collapsed, "a new item: no pictures, no tools");
            f.AddPictures(files);
            await Wait(300);
            WrapPanel strip = StripOf(FormBrowser(f));
            Check(f.Pictures.Count == 3 && strip.Children.Count == 3 && FormSelected(f) == 0
                  && Field<TextBlock>(f, "imagePathText").Text == "3 pictures", "three picked at once: three thumbnails, the first selected");
            Check(f.Pictures.All(p => p.Preview != null && p.PickedFile != null && p.Stored == null), "each is previewed and kept as a picked file");
            Check(!Field<Button>(f, "coverBtn").IsEnabled && !Field<Button>(f, "earlierBtn").IsEnabled && Field<Button>(f, "laterBtn").IsEnabled,
                "the cover can't be made cover or moved earlier");
            RenderWindow(f, "pictures-01-editor-3-en");

            FormSelect(f, 2);
            Check(FormSelected(f) == 2 && FormInSync(f), "a thumbnail clicked: shown larger");
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
        EntityImages.Apply(result, form.Pictures, out List<string> imported, out List<string> failed);
        Check(failed.Count == 0 && imported.Count == 5 && ItemFiles().Count == before.Count + 5,
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
        Check(EntityImages.Names(Current("A")).SequenceEqual(new[] { p[0] }) && Stored(p[1]) && Stored(p[2]) && Stored(p[0]),
            "edit, remove two: both kept while the edit can be undone");
        UndoService.EndAll();
        Check(!Stored(p[1]) && Stored(p[2]) && Stored(p[0]), "then the one nobody else holds is deleted, B's shared one stays");
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
        Check(Stored(q[0]) && Stored(q[1]) && Stored(q[2]) && Stored(q[3]), "their pictures kept while the delete can be undone");
        UndoService.EndAll();
        Check(!Stored(q[0]) && !Stored(q[1]) && !Stored(q[2]) && !Stored(q[3]), "then every picture of the deleted items is deleted");
        Check(Stored(q[8]) && Stored(q[4]), "the one a remaining item holds stays, and the other items' own");
        Invoke(bulkView, "DeleteEntity", Field<List<Entity>>(bulkView, "entities").First(x => x.Name == "F"));
        await Wait(300);
        UndoService.EndAll();
        Check(!Stored(q[8]) && Stored(q[4]), "deleting the last holder (one item): the shared picture goes too");

        // A whole memory.
        Invoke(main, "NavigateHome");
        Profile gone = AddMemory(main, "Pictures gone", ProfileType.Collection), stays = AddMemory(main, "Pictures stays", ProfileType.Collection);
        string own = StoredPicture(5), shared = StoredPicture(6);
        EntityStore.SaveOrWarn(gone, new List<Entity> { PictureItem("H", 0, own, shared) });
        EntityStore.SaveOrWarn(stays, new List<Entity> { PictureItem("I", 0, shared) });
        Invoke(main, "RemoveMemories", new List<Profile> { gone });
        await Wait(300);
        Check(Stored(own) && Stored(shared), "deleting a memory: its pictures kept while it can be undone");
        UndoService.EndAll();
        Check(!Stored(own) && Stored(shared), "then its pictures go, the one another collection holds stays");
        Invoke(main, "RemoveMemories", new List<Profile> { stays });
        await Wait(300);
        UndoService.EndAll();
        Check(!Stored(shared), "...and goes with the last memory that held it");
        Invoke(main, "NavigateHome");
    }

    // ---- Found in review -------------------------------------------------------------------------

    static async Task PictureReviewFixes(MainWindow main)
    {
        Console.WriteLine("Review findings: a failed copy, legacy paths anywhere, a cover in MoreImages only, the item window's decoding");
        Profile memory = AddMemory(main, "Pictures review", ProfileType.Collection);
        string[] p = Enumerable.Range(0, 5).Select(StoredPicture).ToArray();
        string ownCover = Path.Combine(picWork, "saját borító.png"), ownMore = Path.Combine(picWork, "saját másik.png");
        ColorPicture(ownCover, 3);
        ColorPicture(ownMore, 4);
        string lost = Path.Combine(picWork, "elveszett.png");
        Entity legacy = PictureItem("Régi utakkal", 1, ownCover, ownMore, lost);
        Entity coverless = PictureItem("Borító nélkül", 2);
        coverless.MoreImages = new List<string> { p[3], p[4] }; // from elsewhere: no ImagePath, two further pictures
        EntityStore.SaveOrWarn(memory, new List<Entity> { PictureItem("Hármas", 0, p[0], p[1], p[2]), legacy, coverless });
        CollectionView view = OpenCollectionView(main, memory);
        await Wait(600);

        // Legacy full paths are copied in wherever they are in the list; one whose file is gone keeps its path.
        List<string> names = EntityImages.Names(EntityStore.Load(memory).Single(e => e.Name == legacy.Name));
        Check(names.Count == 3 && !EntityImages.IsLegacyPath(names[0]) && !EntityImages.IsLegacyPath(names[1]) && names[2] == lost
              && SameBytes(ImageStore.Items.PathOf(names[0]), ownCover) && SameBytes(ImageStore.Items.PathOf(names[1]), ownMore)
              && File.Exists(ownCover) && File.Exists(ownMore), "user's own files at any place in the list are copied in (originals untouched); a lost one keeps its path");

        // A cover only in MoreImages (ImagePath empty): the card, the badge and exports use the first picture.
        Check(EntityImages.Cover(coverless) == p[3] && EntityImages.FullPath(coverless) == ImageStore.Items.PathOf(p[3]), "no ImagePath: the first further picture is the cover");
        Check(Descendants(view).OfType<TextBlock>().Count(t => t.Text == "") == 3, "its card shows the picture and a badge, as do the item with three and the one with own files");
        string docx = Path.Combine(picWork, "coverless.docx");
        ExportService.ExportEntity(coverless, docx, ExportFormat.Docx);
        using (WordprocessingDocument document = WordprocessingDocument.Open(docx, false))
            Check(document.MainDocumentPart.ImageParts.Count() == 1, "and Word shows that cover");

        // The item window draws at once and decodes the rest off the UI thread.
        Entity three = Field<List<Entity>>(view, "entities").First(e => e.Name == "Hármas");
        ItemWindow window = ShowItem(main, three, 1000, 800);
        PictureHero hero = Field<PictureHero>(window, "hero");
        // The decode runs on a worker thread and may already be done; what matters is that the UI has not drawn it yet.
        Check(!(ThumbOf(hero, 1).Background is ImageBrush), "opening the window doesn't draw the other pictures on the spot: they are decoded off the UI thread");
        await Wait(800);
        Check(HeroInSync(hero, p.Take(3).Select(n => (ImageSource)EntityImages.Loaded(n)).ToList(), 0) && EntityImages.Loaded(p[2]) != null,
            "a moment later they are all there");
        ClickNow(Field<Button>(hero, "next"));
        PressKey(Field<TextBox>(window, "NameText"), Key.Right);
        Check(hero.Selected == 1, "Right in the name (a text box) moves its caret, not the picture");
        window.Close();

        // A picked file that can't be copied at Save: left out and named, the rest of the edit kept.
        string fleeting = OwnPictures(1, 70)[0];
        int boxes = selectBoxes.Count;
        DriveEntityForm(main, async f =>
        {
            await Wait(200);
            f.AddPictures(new[] { fleeting });       // [p0, p1, p2, x]
            FormSelect(f, 2);
            FormTool(f, "coverBtn");                // [p2, p0, p1, x]
            FormSelect(f, 2);
            FormTool(f, "removePictureBtn");        // [p2, p0, x]
            File.Delete(fleeting);                  // gone before Save
        });
        Invoke(view, "EditEntity", Field<List<Entity>>(view, "entities").First(e => e.Name == "Hármas"));
        await Wait(300);
        UndoService.EndAll(); // the edit can't be undone any more: what it removed goes
        Check(EntityImages.Names(EntityStore.Load(memory).Single(e => e.Name == "Hármas")).SequenceEqual(new[] { p[2], p[0] }) && !Stored(p[1]),
            "the copy failed: the new cover and the removal are still saved, and the removed picture's file is gone");
        Check(selectBoxes.Skip(boxes).Any(b => b.Contains(Path.GetFileName(fleeting))), "the message names the file: " + selectBoxes.LastOrDefault());
        Invoke(main, "NavigateHome");
    }

    // ---- The card and the item window's pictures ---------------------------------------------

    static async Task PictureCardAndDetail(MainWindow main)
    {
        Console.WriteLine("The card badge and the item window's pictures");
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

        ItemWindow window = ShowItem(main, three, 1000, 800);
        PictureHero hero = Field<PictureHero>(window, "hero");
        for (int i = 0; i < 30 && p.Take(3).Any(x => EntityImages.Loaded(x) == null); i++) // decoded off the UI thread
            await Wait(100);
        await Wait(200);
        Button previous = Field<Button>(hero, "previous"), next = Field<Button>(hero, "next");
        List<ImageSource> shown = p.Take(3).Select(n => (ImageSource)EntityImages.Load(n)).ToList();
        Check(Field<Border>(hero, "stripPlate").Visibility == Visibility.Visible && HeroInSync(hero, shown, 0) && !previous.IsEnabled,
            "the window shows the cover large, a strip of three and 1 / 3 (back is off on the first)");
        Check(previous.Visibility == Visibility.Collapsed && next.Visibility == Visibility.Collapsed, "the arrows stay hidden while the mouse is elsewhere");
        Check(hero.ActualWidth >= window.ActualWidth - 40 && hero.ActualHeight >= 200 && hero.ActualHeight <= 380, "the pictures fill the window's width, 200 to 380 high");
        ClickNow((Button)Field<StackPanel>(hero, "strip").Children[1]);
        Check(HeroInSync(hero, shown, 1), "a thumbnail clicked: that picture, large, framed");
        ClickNow(next);
        Check(HeroInSync(hero, shown, 2) && !next.IsEnabled, "the next arrow: the third, and next is off at the end");
        PressKey(window, Key.Left);
        Check(HeroInSync(hero, shown, 1), "the Left key browses back");
        PressKey(window, Key.Right);
        Check(HeroInSync(hero, shown, 2) && EntityImages.Names(EntityStore.Load(memory)[0]).SequenceEqual(p.Take(3)), "the Right key browses on; the item's order is untouched");

        Loc.Switch(Loc.Hungarian);
        await Wait(400);
        Check(HeroInSync(hero, shown, 2) && (string)next.ToolTip == "Következő kép", "a language switch keeps the picture shown, the arrows in Hungarian");
        ToolTipText(view, "hu: 3 kép");
        Loc.Switch(Loc.SerbianLatin);
        await Wait(300);
        ToolTipText(view, "sr: 3 slike");
        Loc.Switch(Loc.English);
        await Wait(300);
        window.Close();

        window = ShowItem(main, one, 1000, 800);
        hero = Field<PictureHero>(window, "hero");
        await Wait(500);
        Check(Field<Border>(hero, "stripPlate").Visibility == Visibility.Collapsed && Field<Border>(hero, "countPlate").Visibility == Visibility.Collapsed
              && Field<Image>(hero, "picture").Source == EntityImages.Load(p[3]) && !hero.Browse(1), "one picture: large, no strip, no count, nothing to browse");
        PressKey(window, Key.Right);
        Check(hero.Selected == 0, "...and Right does nothing");
        window.Close();
        window = ShowItem(main, none, 1000, 800);
        await Wait(300);
        Check(Field<ContentControl>(window, "Hero").Visibility == Visibility.Collapsed, "no picture: no picture area at all");
        window.Close();
        Invoke(main, "NavigateHome");
    }

    static Border ThumbOf(PictureHero hero, int index) => (Border)((Button)Field<StackPanel>(hero, "strip").Children[index]).Content;

    // What the hero draws agrees with `selected`: that picture large (and as the backdrop), exactly that thumbnail
    // framed, the count, the arrows off exactly at the ends.
    static bool HeroInSync(PictureHero hero, IReadOnlyList<ImageSource> pictures, int selected)
    {
        StackPanel strip = Field<StackPanel>(hero, "strip");
        if (hero.Selected != selected || strip.Children.Count != pictures.Count || !ReferenceEquals(Field<Image>(hero, "picture").Source, pictures[selected])
            || !ReferenceEquals(Field<Image>(hero, "backdrop").Source, pictures[selected]))
            return false;
        for (int i = 0; i < pictures.Count; i++)
        {
            Border thumb = ThumbOf(hero, i);
            if ((thumb.BorderThickness.Left == 2) != (i == selected) || !(thumb.Background is ImageBrush brush) || !ReferenceEquals(brush.ImageSource, pictures[i]))
                return false;
        }
        return Field<TextBlock>(hero, "count").Text == Loc.F("Entity.Picture.Count", selected + 1, pictures.Count)
            && Field<Button>(hero, "previous").IsEnabled == (selected > 0) && Field<Button>(hero, "next").IsEnabled == (selected < pictures.Count - 1);
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
        TransferPackage.ExportCollection(closet, tabs, new List<Entity> { first, second, third }, null, 0, collectionPackage, null, CancellationToken.None);
        TransferPackage.ExportCollection(plain, plainTabs, new List<Entity> { lone }, null, 0, plainPackage, null, CancellationToken.None);
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
