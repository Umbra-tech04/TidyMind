using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using TidyMind;

// Harness undo <data> <shotsFolder>: Ctrl+Z / Ctrl+Y for data changes. For each kind: do it, undo it (the saved files
// byte for byte as before), redo it (as after); a selection as one step; files kept on disk while a step can bring them
// back and deleted (only what nothing uses) once it can't; reminders back once (one entry, one task) and gone again on
// redo; a text box keeps its own Ctrl+Z; a conflict and a failed write leave the data folder as it was; the 50 limit; the
// toast; the labels in three languages. Message boxes are answered by the select watcher (Delete/Yes/OK).
static partial class Program
{
    static async Task UndoScenarios(MainWindow main)
    {
        await Wait(1200);
        GuidedTour.Current?.Stop(TourEnd.Skipped);
        main.Topmost = true;
        main.Width = 1240;
        main.Height = 820;
        StartSelectWatcher();
        int tasksBefore = ReminderTaskNames().Count;
        await Foreground(main);

        try
        {
            await UndoEngine(main);
            await UndoItems(main);
            await UndoItemWindow(main);
            await UndoProjects(main);
            await UndoProjectWindow(main);
            await UndoMemories(main);
            await UndoTodos(main);
            await UndoPlan(main);
            await UndoReminders(main);
            await UndoKeys(main);
            UndoLanguages();
            await UndoReviewFixes(main);
            await UndoAfterShutdown(main); // last: nothing is recorded after it
        }
        finally
        {
            CleanUpReminders();
            Check(ReminderTaskNames().Count == tasksBefore, "no TidyMind_Reminder_* task left behind");
            Console.WriteLine("message boxes: " + selectBoxes.Count);
            lock (selectBoxes)
                foreach (string box in selectBoxes)
                    Console.WriteLine("  " + box);
        }
    }

    // ---- The engine: a property-level undo, a conflict, a failed write, several files, the limit ---------------

    static async Task UndoEngine(MainWindow main)
    {
        Console.WriteLine("The engine");
        Profile memory = AddMemory(main, "Undo engine", ProfileType.Collection);
        List<Entity> items = Enumerable.Range(1, 4).Select(i => Garment("Engine " + i, i)).ToList();
        File.WriteAllText(MemoryFiles.Items(memory), JsonSerializer.Serialize(items));
        File.WriteAllText(MemoryFiles.Tabs(memory), JsonSerializer.Serialize(new List<MemoryTab>
        {
            new MemoryTab { Id = Guid.NewGuid(), Name = "First", Order = 0 }, new MemoryTab { Id = Guid.NewGuid(), Name = "Second", Order = 1 }
        }));
        Invoke(main, "NavigateTo", memory);
        await Wait(800);
        CollectionView view = (CollectionView)Content(main);
        string file = MemoryFiles.Items(memory), tabsFile = MemoryFiles.Tabs(memory);
        Entity Shown(Guid id) => Field<List<Entity>>(view, "entities").First(e => e.Id == id);

        // An edit undone while another property was changed since: only what the edit changed goes back.
        byte[] start = Bytes(file);
        Entity edited = Copy(Shown(items[0].Id));
        edited.Notes = "Dry clean only";
        Check((bool)Call(view, "Replace", edited), "an item edited (its notes)");
        List<Entity> stored = EntityStore.Load(memory);
        stored.First(e => e.Id == items[0].Id).Color = "Navy"; // another place changed its colour since
        EntityStore.TrySave(memory, stored);
        Check(main.Undo.Undo(), "the edit undone although the colour changed since");
        Entity back = EntityStore.Load(memory).First(e => e.Id == items[0].Id);
        Check(back.Notes == null && back.Color == "Navy", "its notes are back, the later colour stays");
        stored = EntityStore.Load(memory);
        stored.First(e => e.Id == items[0].Id).Color = null;
        EntityStore.TrySave(memory, stored);
        Check(Same(Bytes(file), start), "(the colour taken out again: as at the start)");
        Invoke(view, "ReloadAfterUndo");

        // A conflict: what the step changed was changed again since.
        edited = Copy(Shown(items[1].Id));
        edited.Notes = "First";
        Call(view, "Replace", edited);
        stored = EntityStore.Load(memory);
        stored.First(e => e.Id == items[1].Id).Notes = "Changed elsewhere";
        EntityStore.TrySave(memory, stored);
        byte[] tampered = Bytes(file);
        int boxes = selectBoxes.Count;
        Check(!main.Undo.Undo(), "undo refused: the notes were changed again since");
        Check(Same(Bytes(file), tampered), "nothing written");
        Check(selectBoxes.Skip(boxes).Any(b => b.Contains(Loc.T("Undo.Conflict"))), "and it says why");
        Check(UndoService.Page.NextUndo?.Label != Loc.F("Undo.Changed", "Engine 2"), "that step is dropped");

        // A delete undone after the item came back by another way (the same Id): refused, nothing doubled.
        Invoke(view, "ReloadAfterUndo");
        Invoke(view, "DeleteEntity", Shown(items[2].Id));
        await Wait(200);
        stored = EntityStore.Load(memory);
        stored.Add(Copy(items[2]));
        EntityStore.TrySave(memory, stored);
        tampered = Bytes(file);
        Check(!main.Undo.Undo() && Same(Bytes(file), tampered) && EntityStore.Load(memory).Count(e => e.Id == items[2].Id) == 1,
            "a deleted item that is back already isn't put back twice");
        Invoke(view, "ReloadAfterUndo");

        // A failed write: nothing changes, the step stays and works once the file can be written. (Saved once by the
        // page first: the item put back above went at the end of the file, out of the order the page keeps.)
        Invoke(view, "SaveEntities");
        start = Bytes(file);
        Invoke(view, "DeleteEntity", Shown(items[3].Id));
        await Wait(200);
        byte[] deleted = Bytes(file);
        File.SetAttributes(file, FileAttributes.ReadOnly);
        boxes = selectBoxes.Count;
        bool undone = main.Undo.Undo();
        File.SetAttributes(file, FileAttributes.Normal);
        Check(!undone && Same(Bytes(file), deleted), "entities.json can't be written: undo fails, the file as it was");
        Check(selectBoxes.Skip(boxes).Any(b => b.Contains(Loc.T("Undo.Failed"))), "and it says so");
        Check(main.Undo.Undo() && Same(Bytes(file), start), "the step stayed: once it can be written, undone");

        // Two files in one step (a tab deleted, its items moved): one can't be written, so neither is.
        MemoryTabStrip strip = Field<MemoryTabStrip>(view, "tabStrip");
        List<MemoryTab> tabs = Field<List<MemoryTab>>(strip, "tabs");
        stored = EntityStore.Load(memory);
        stored[0].TabId = tabs[1].Id;
        EntityStore.TrySave(memory, stored);
        Invoke(view, "ReloadAfterUndo");
        byte[] tabsStart = Bytes(tabsFile);
        start = Bytes(file);
        Invoke(strip, "DeleteTab", Field<List<MemoryTab>>(strip, "tabs").First(t => t.Name == "Second"));
        await Wait(200);
        byte[] tabsDeleted = Bytes(tabsFile), itemsMoved = Bytes(file);
        Check(!Same(tabsDeleted, tabsStart) && !Same(itemsMoved, start), "a tab deleted, its item moved to the first tab");
        File.SetAttributes(file, FileAttributes.ReadOnly);
        undone = main.Undo.Undo();
        File.SetAttributes(file, FileAttributes.Normal);
        Check(!undone && Same(Bytes(tabsFile), tabsDeleted) && Same(Bytes(file), itemsMoved),
            "the items can't be written: the tabs written first are put back, both files as they were");
        Check(main.Undo.Undo() && Same(Bytes(tabsFile), tabsStart) && Same(Bytes(file), start), "then undone: both files byte for byte");
        Check(Field<List<MemoryTab>>(strip, "tabs").Any(t => t.Name == "Second"), "the tab is back on the page");

        // At most 50 steps.
        for (int i = 0; i < 55; i++)
        {
            List<Entity> now = Field<List<Entity>>(view, "entities");
            Invoke(view, "MoveEntity", now[0], now[now.Count - 1], true);
        }
        Check(UndoService.Page.Count == UndoHistory.Limit, "55 moves keep the last " + UndoHistory.Limit + " (" + UndoService.Page.Count + ")");
        RemoveMemory(main, memory);
    }

    // ---- Collection items ----------------------------------------------------------------------------

    static async Task UndoItems(MainWindow main)
    {
        Console.WriteLine("Collection items: one, a selection, a move, their files and reminders");
        Profile memory = AddMemory(main, "Undo garage", ProfileType.Collection);
        string picture = StoreFile(ImageStore.Items.Folder, "car.png");
        string scan = StoreFile(AttachmentStore.Folder, "insurance.pdf");
        string shared = StoreFile(ImageStore.Items.Folder, "shared.png");
        Entity car = Vehicle("Undo car");
        car.ImagePath = Path.GetFileName(picture);
        EntityRecord insurance = Document("Insurance", DateTime.Today.AddDays(60), 30, "Allianz");
        insurance.File = new Attachment { Id = Guid.NewGuid(), DisplayName = "insurance.pdf", StoredFileName = Path.GetFileName(scan), SizeBytes = 7, AddedDate = DateTime.Today };
        VehicleSchema.Documents.Write(car, new List<EntityRecord> { insurance });
        Entity coat = Garment("Undo coat", 1), scarf = Garment("Undo scarf", 2), hat = Garment("Undo hat", 3);
        coat.ImagePath = scarf.ImagePath = Path.GetFileName(shared);
        File.WriteAllText(MemoryFiles.Items(memory), JsonSerializer.Serialize(new List<Entity> { car, coat, scarf, hat }));
        testReminderIds.Add(insurance.Id);
        Check(VehicleReminders.Follow(null, car) == null, "the insurance has its reminder");
        Invoke(main, "NavigateTo", memory);
        await Wait(800);
        CollectionView view = (CollectionView)Content(main);
        string file = MemoryFiles.Items(memory);
        List<Entity> Shown() => Field<List<Entity>>(view, "entities");
        byte[] start = Bytes(file);
        Dictionary<Guid, string> reminders = RemindersNow();
        Check(reminders.ContainsKey(insurance.Id) && TaskTrigger(insurance.Id) != null, "its reminder and task are there");

        Invoke(view, "DeleteEntity", Shown().First(e => e.Id == car.Id));
        await Wait(300);
        byte[] deleted = Bytes(file);
        Check(!EntityStore.Load(memory).Any(e => e.Id == car.Id), "one item deleted");
        Check(File.Exists(picture) && File.Exists(scan), "its picture and document file stay on disk while it can be undone");
        Check(!RemindersNow().ContainsKey(insurance.Id) && TaskTrigger(insurance.Id) == null, "its reminder and task gone");
        Check(main.Undo.Shown == Loc.F("Undo.Deleted", "Undo car"), "the toast says what went: " + main.Undo.Shown);
        RenderWindow(main, "undo-01-toast");
        Check(main.Undo.Undo(), "undone");
        Check(Same(Bytes(file), start), "entities.json byte for byte as before");
        CheckReminderBack(insurance.Id, reminders[insurance.Id], "its reminder back once, as it was, with its task");
        Check(Shown().Any(e => e.Id == car.Id), "and its card on the page");
        Check(main.Undo.Shown == Loc.F("Undo.Undone", Loc.F("Undo.Deleted", "Undo car")), "the toast: undone, with Redo: " + main.Undo.Shown);
        RenderWindow(main, "undo-02-undone");
        Check(main.Undo.Redo() && Same(Bytes(file), deleted) && !RemindersNow().ContainsKey(insurance.Id) && TaskTrigger(insurance.Id) == null,
            "redone: deleted again, its reminder and task gone again");
        Check(main.Undo.Undo() && Same(Bytes(file), start), "undone again");
        CheckReminderBack(insurance.Id, reminders[insurance.Id], "the reminder once again, not twice");

        Invoke(view, "DeleteEntities", (IReadOnlyList<Entity>)Shown().Where(e => e.Id != hat.Id).ToList());
        await Wait(300);
        Check(EntityStore.Load(memory).Select(e => e.Id).SequenceEqual(new[] { hat.Id }), "a selection of three deleted at once");
        Check(main.Undo.Shown == Loc.Plural("Undo.Deleted.Items", 3), "the toast counts them: " + main.Undo.Shown);
        Check(main.Undo.Undo() && Same(Bytes(file), start), "one undo brings all three back, byte for byte");
        CheckReminderBack(insurance.Id, reminders[insurance.Id], "with the car's reminder");

        List<Entity> now = Shown();
        Invoke(view, "MoveEntity", now[3], now[0], false);
        Check(!Same(Bytes(file), start), "a card moved");
        Check(main.Undo.Undo() && Same(Bytes(file), start), "the move undone, byte for byte");
        Check(main.Undo.Redo() && main.Undo.Undo() && Same(Bytes(file), start), "redone and undone again");

        // The files once nothing can bring the items back: deleted, but not one another item still uses.
        Invoke(view, "DeleteEntities", (IReadOnlyList<Entity>)Shown().Where(e => e.Id == car.Id || e.Id == coat.Id).ToList());
        await Wait(300);
        Check(File.Exists(picture) && File.Exists(scan) && File.Exists(shared), "two deleted: every file kept while it can be undone");
        Invoke(main, "NavigateHome");
        Check(UndoService.Page.NextUndo == null, "another page: nothing of the collection left to undo");
        Check(!File.Exists(picture) && !File.Exists(scan), "the car's picture and document file deleted once the page is left");
        Check(File.Exists(shared), "the coat's picture kept: the scarf still uses it");
        Check(!RemindersNow().ContainsKey(insurance.Id), "the car's reminder stays gone");
        RemoveMemory(main, memory);
    }

    // ---- The item's window ----------------------------------------------------------------------------

    static async Task UndoItemWindow(MainWindow main)
    {
        Console.WriteLine("The item window: a record deleted, its own Ctrl+Z, a field edit");
        Profile memory = AddMemory(main, "Undo window", ProfileType.Collection);
        string scan = StoreFile(AttachmentStore.Folder, "vignette.pdf");
        Entity car = Vehicle("Window car");
        EntityRecord insurance = Document("Insurance", DateTime.Today.AddDays(90), 30, "Generali");
        EntityRecord vignette = Document("Vignette", DateTime.Today.AddDays(200), null, "Austria");
        vignette.File = new Attachment { Id = Guid.NewGuid(), DisplayName = "vignette.pdf", StoredFileName = Path.GetFileName(scan), SizeBytes = 7, AddedDate = DateTime.Today };
        VehicleSchema.Documents.Write(car, new List<EntityRecord> { insurance, vignette });
        File.WriteAllText(MemoryFiles.Items(memory), JsonSerializer.Serialize(new List<Entity> { car, Garment("Window coat", 1) }));
        testReminderIds.Add(insurance.Id);
        VehicleReminders.Follow(null, car);
        Invoke(main, "NavigateTo", memory);
        await Wait(800);
        CollectionView view = (CollectionView)Content(main);
        string file = MemoryFiles.Items(memory);
        byte[] start = Bytes(file);
        Dictionary<Guid, string> reminders = RemindersNow();

        // As the collection opens it: modal, so the main window can't take the keys or the toast meanwhile.
        ItemWindow window = new ItemWindow(Field<List<Entity>>(view, "entities").First(e => e.Id == car.Id), new ItemWindow.Host
        {
            Save = e => (bool)Call(view, "Replace", e),
            EditBasics = e => (Entity)Call(view, "EditEntity", e),
            Delete = e => (bool)Call(view, "DeleteEntity", e),
            Reread = id => (Entity)Call(view, "Reread", id)
        }) { Owner = main, Width = 1000, Height = 760 };
        main.IsEnabled = false;
        window.Show();
        await Wait(600);
        await Foreground(window);
        UndoHost host = UndoHost.Of(window);
        Entity Item() => Field<Entity>(window, "item");
        bool HasRecord(Guid id) => VehicleSchema.Documents.Read(EntityStore.Load(memory).First(e => e.Id == car.Id)).Any(r => r.Id == id);
        try
        {
            Invoke(window, "DeleteRecord", VehicleSchema.Documents, VehicleSchema.Documents.Read(Item()).First(r => r.Id == insurance.Id));
            for (int waited = 0; waited < 3000 && host.Shown == null; waited += 100)
                await Wait(100);
            await Foreground(window); // the question that was asked took the foreground with it
            byte[] deleted = Bytes(file);
            Check(!HasRecord(insurance.Id) && !RemindersNow().ContainsKey(insurance.Id), "a document deleted in the window, its reminder with it");
            Check(host.Shown == Loc.F("Undo.Deleted", VehicleDocuments.Title(insurance)), "the window's own toast: " + host.Shown);
            RenderWindow(window, "undo-03-item-window");

            TextBox search = (TextBox)window.FindName("SearchBox");
            search.Focus();
            await Held(0x11, () => PressKey(search, Key.Z));
            Check(!HasRecord(insurance.Id) && UndoHost.TextHasKeyboard(), "Ctrl+Z in the search box is the box's own: the document stays deleted"
                + (InForeground() ? "" : " (not in front!)"));
            // The header's Delete button, drawn anew with the header after each undo.
            Button Delete() => ((Panel)window.FindName("Actions")).Children.OfType<Button>().Last();
            Delete().Focus();
            await Held(0x11, () => PressKey(Delete(), Key.Z));
            Check(HasRecord(insurance.Id) && Same(Bytes(file), start), "Ctrl+Z on a button: undone, byte for byte" + (InForeground() ? "" : " (not in front!)"));
            Check(VehicleSchema.Documents.Read(Item()).Any(r => r.Id == insurance.Id), "the window shows it again");
            Check(window.IsKeyboardFocusWithin || !InForeground(), "the keyboard is still in the window although the button it was on was drawn anew");
            CheckReminderBack(insurance.Id, reminders[insurance.Id], "its reminder back once, with its task");
            UIElement keys = Keyboard.FocusedElement as UIElement ?? window;
            await Held(0x11, () => PressKey(keys, Key.Y));
            Check(!HasRecord(insurance.Id) && Same(Bytes(file), deleted), "Ctrl+Y where the keyboard is now: deleted again");
            await Held(0x11, () => PressKey(Keyboard.FocusedElement as UIElement ?? window, Key.Z));
            await CtrlShift(() => PressKey(Keyboard.FocusedElement as UIElement ?? window, Key.Z));
            Check(!HasRecord(insurance.Id), "Ctrl+Shift+Z redoes too");
            Check(host.Undo() && Same(Bytes(file), start), "and undone");

            // A step of another item isn't this window's.
            Entity coat = Copy(Field<List<Entity>>(view, "entities").First(e => e.Name == "Window coat"));
            coat.Notes = "Another item's change";
            Call(view, "Replace", coat);
            Check(!host.Undo() && EntityStore.Load(memory).First(e => e.Id == coat.Id).Notes == "Another item's change",
                "another item's newer step: the window undoes nothing");
            Check(main.Undo.Undo(), "(undone on the page)");
            start = Bytes(file);

            Invoke(window, "Commit", (Action<Entity>)(v => v.Notes = "Winter tyres in the cellar"));
            Check(EntityStore.Load(memory).First(e => e.Id == car.Id).Notes == "Winter tyres in the cellar", "a field saved");
            Check(host.Undo() && Same(Bytes(file), start) && Item().Notes == null, "the field edit undone, in the file and the window");
            Check(host.Redo() && Item().Notes == "Winter tyres in the cellar", "and redone");
        }
        finally
        {
            window.Close();
            main.IsEnabled = true;
        }
        Check(main.Undo.Undo() && Same(Bytes(file), start), "the window closed: its steps are the page's, Ctrl+Z there takes the last one back");
        RemoveMemory(main, memory);
    }

    // ---- Projects --------------------------------------------------------------------------------------

    static async Task UndoProjects(MainWindow main)
    {
        Console.WriteLine("Projects: one, a selection, a move, a tab, the project window as one step");
        Profile memory = AddMemory(main, "Undo projects", ProfileType.Project);
        string plan = StoreFile(AttachmentStore.Folder, "plan.pdf");
        string card = StoreFile(ImageStore.Cards.Folder, "card.png");
        Guid firstTab = Guid.NewGuid(), secondTab = Guid.NewGuid();
        List<Project> projects = Enumerable.Range(1, 4).Select(i => new Project
        {
            Id = Guid.NewGuid(), Name = "Project " + i, Order = i, LastModified = DateTime.Today, TabId = i == 4 ? secondTab : firstTab,
            Tasks = new List<TaskItem> { new TaskItem { Title = "Task A" + i }, new TaskItem { Title = "Task B" + i, IsDone = true } }
        }).ToList();
        projects[1].CardBackgroundType = BackgroundFill.Image;
        projects[1].CardBackgroundImagePath = Path.GetFileName(card);
        projects[1].Attachments = new List<Attachment> { new Attachment { Id = Guid.NewGuid(), DisplayName = "plan.pdf", StoredFileName = Path.GetFileName(plan), SizeBytes = 7, AddedDate = DateTime.Today } };
        File.WriteAllText(MemoryFiles.Items(memory), JsonSerializer.Serialize(projects));
        File.WriteAllText(MemoryFiles.Tabs(memory), JsonSerializer.Serialize(new List<MemoryTab>
        {
            new MemoryTab { Id = firstTab, Name = "Home", Order = 0 }, new MemoryTab { Id = secondTab, Name = "Garden", Order = 1 }
        }));
        Invoke(main, "NavigateTo", memory);
        await Wait(800);
        ProjectView view = (ProjectView)Content(main);
        string file = MemoryFiles.Items(memory), tabsFile = MemoryFiles.Tabs(memory);
        List<Project> Shown() => Field<List<Project>>(view, "allProjects");
        byte[] start = Bytes(file), tabsStart = Bytes(tabsFile);

        Invoke(view, "DeleteProjects", (IReadOnlyList<Project>)Shown().Where(p => p.Id == projects[1].Id).ToList());
        await Wait(300);
        Check(!ProjectStore.Load(memory).Any(p => p.Id == projects[1].Id) && File.Exists(plan) && File.Exists(card),
            "a project deleted; its file and card picture stay while it can be undone");
        Check(main.Undo.Shown == Loc.F("Undo.Deleted", "Project 2"), "the toast: " + main.Undo.Shown);
        Check(main.Undo.Undo() && Same(Bytes(file), start), "undone, byte for byte");
        Check(main.Undo.Redo() && main.Undo.Undo() && Same(Bytes(file), start), "redone and undone");

        Invoke(view, "DeleteProjects", (IReadOnlyList<Project>)Shown().Where(p => p.Name != "Project 2").ToList());
        await Wait(300);
        Check(ProjectStore.Load(memory).Count == 1 && main.Undo.Shown == Loc.Plural("Undo.Deleted.Projects", 3), "three at once: " + main.Undo.Shown);
        Check(main.Undo.Undo() && Same(Bytes(file), start), "one undo for the three");

        Invoke(view, "MoveProject", Shown()[2], Shown()[0], false);
        Check(main.Undo.Undo() && Same(Bytes(file), start), "a move undone");

        MemoryTabStrip strip = Field<MemoryTabStrip>(view, "tabStrip");
        Invoke(strip, "DeleteTab", Field<List<MemoryTab>>(strip, "tabs").First(t => t.Id == secondTab));
        await Wait(200);
        Check(!Same(Bytes(tabsFile), tabsStart) && ProjectStore.Load(memory).All(p => p.TabId == firstTab), "a tab deleted, its project moved");
        Check(main.Undo.Shown == Loc.F("Undo.Deleted", "Garden"), "the toast: " + main.Undo.Shown);
        Check(main.Undo.Undo() && Same(Bytes(tabsFile), tabsStart) && Same(Bytes(file), start), "undone: the tab and where its project was");

        // Project Details: what one opening changed is one step of the page (its own Ctrl+Z works inside it).
        Project inside = Shown().First(p => p.Id == projects[1].Id);
        string titleBefore = inside.Tasks[0].Title;
        Application.Current.Dispatcher.BeginInvoke(new Action(async () =>
        {
            ProjectWindow w = await WaitFor(() => Application.Current.Windows.OfType<ProjectWindow>().FirstOrDefault());
            await Wait(500);
            AttachmentsSection files = (AttachmentsSection)w.FindName("Files");
            Invoke(files, "RemoveEntries", (IReadOnlyList<Attachment>)files.Attachments.ToList());
            Invoke(w, "DeleteTask", Field<Project>(w, "currentProject").Tasks[1]);
            w.Close();
        }));
        Invoke(view, "OpenProject", inside); // shown as a dialog until the callback closes it
        await Wait(300);
        Project saved = ProjectStore.Load(memory).First(p => p.Id == projects[1].Id);
        Check(saved.Attachments.Count == 0 && saved.Tasks.Count == 1, "the window took a file and a task out, saved on closing");
        Check(File.Exists(plan), "the removed file stays on disk while the window's step can be undone");
        Check(main.Undo.Undo() && Same(Bytes(file), start), "Ctrl+Z on the page: everything the window changed, byte for byte");
        Check(main.Undo.Redo(), "and redone");
        Invoke(main, "NavigateHome");
        Check(!File.Exists(plan), "the removed file deleted once nothing can bring it back");
        Check(File.Exists(card), "the card picture, still the project's, kept");
        RemoveMemory(main, memory);
    }

    // ---- Inside Project Details --------------------------------------------------------------------------

    static async Task UndoProjectWindow(MainWindow main)
    {
        Console.WriteLine("Project Details: tasks, a tick, files, pages, in the window itself");
        Project project = SampleProject(6, files: 2);
        project.NotePages = Enumerable.Range(1, 3).Select(i => new NotePage { Body = RichTextHelper.ToStorableString(Doc("Page text " + i)) }).ToList();
        ProjectWindow window = await OpenProject(main, project);
        UndoHost host = UndoHost.Of(window);
        ListBox list = (ListBox)window.FindName("TaskList");
        string before = JsonSerializer.Serialize(project.Tasks);
        bool ListInOrder() => list.Items.Cast<TaskItem>().SequenceEqual(TaskOrder.OpenFirst(project.Tasks, t => t.IsDone));
        try
        {
            List<TaskItem> tasks = project.Tasks.ToList();
            Invoke(window, "DeleteTasks", new List<TaskItem> { tasks[1], tasks[4] });
            await Wait(200);
            Check(project.Tasks.Count == 4 && host.Shown == Loc.Plural("Undo.Deleted.Tasks", 2), "two tasks deleted: " + host.Shown);
            Check(host.Undo() && JsonSerializer.Serialize(project.Tasks) == before && ListInOrder(), "undone: in their places, the list open-first");
            Check(host.Redo() && project.Tasks.Count == 4 && host.Undo() && JsonSerializer.Serialize(project.Tasks) == before, "redone, undone");

            Invoke(window, "DeleteTask", project.Tasks[0]);
            Check(host.Undo() && JsonSerializer.Serialize(project.Tasks) == before && ListInOrder(), "one task (its ×) undone");

            list.UpdateLayout();
            TaskItem first = project.Tasks[0];
            CheckBox box = Descendants((ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(first)).OfType<CheckBox>().First();
            box.IsChecked = !first.IsDone;
            Invoke(window, "TaskCheck_Click", box, new RoutedEventArgs());
            Check(first.IsDone && ListInOrder(), "a task ticked, among the done ones");
            Check(host.Undo() && JsonSerializer.Serialize(project.Tasks) == before && ListInOrder(), "the tick undone, back among the open ones");

            AttachmentsSection files = (AttachmentsSection)window.FindName("Files");
            List<string> names = files.Attachments.Select(a => a.DisplayName).ToList();
            Invoke(files, "RemoveEntries", (IReadOnlyList<Attachment>)files.Attachments.Take(1).ToList());
            await Wait(200);
            Check(files.Attachments.Count == 1 && files.Removed.Count == 1 && host.Shown == Loc.F("Undo.Removed", names[0]), "a file taken out: " + host.Shown);
            Check(host.Undo() && files.Attachments.Select(a => a.DisplayName).SequenceEqual(names) && files.Removed.Count == 0,
                "undone: back in its place, and nothing left to delete on closing");

            List<NotePage> Pages() => Field<List<NotePage>>(window, "pages");
            List<string> Bodies() => Field<List<string>>(window, "pageBodies");
            List<string> bodies = Bodies().ToList();
            Invoke(window, "DeletePages", (IReadOnlyList<NotePage>)new[] { Pages()[1] });
            Check(Pages().Count == 2, "a page deleted");
            Check(host.Undo() && Bodies().SequenceEqual(bodies) && Pages().Count == 3, "undone: page 2 back with its text");
            Invoke(window, "DeletePages", (IReadOnlyList<NotePage>)Pages().ToList());
            Check(Pages().Count == 1 && Bodies()[0] == "", "every page deleted: page 1 stays, empty");
            Check(host.Undo() && Bodies().SequenceEqual(bodies), "undone: all three with their text");
            RenderWindow(window, "undo-04-project-window");
        }
        finally
        {
            window.Close();
        }
        await Wait(200);
        Check(UndoHost.Of(window) == null, "closed: its own history ended with it");
    }

    // ---- Memories -----------------------------------------------------------------------------------------

    static async Task UndoMemories(MainWindow main)
    {
        Console.WriteLine("Memories: deleted with everything in them, back with Ctrl+Z, gone for good when it can't be undone");
        Profile garage = AddMemory(main, "Undo memory garage", ProfileType.Collection);
        Profile house = AddMemory(main, "Undo memory house", ProfileType.Project);
        Profile kept = AddMemory(main, "Undo memory kept", ProfileType.Project);
        string picture = StoreFile(ImageStore.Items.Folder, "van.png");
        string attachment = StoreFile(AttachmentStore.Folder, "deed.pdf");
        Entity van = Vehicle("Undo van");
        van.ImagePath = Path.GetFileName(picture);
        EntityRecord inspection = Document("Inspection", DateTime.Today.AddDays(45), 30, "MOT");
        VehicleSchema.Documents.Write(van, new List<EntityRecord> { inspection });
        File.WriteAllText(MemoryFiles.Items(garage), JsonSerializer.Serialize(new List<Entity> { van }));
        File.WriteAllText(MemoryFiles.Items(house), JsonSerializer.Serialize(new List<Project>
        {
            new Project
            {
                Id = Guid.NewGuid(), Name = "Deeds", Tasks = new List<TaskItem>(),
                Attachments = new List<Attachment> { new Attachment { Id = Guid.NewGuid(), DisplayName = "deed.pdf", StoredFileName = Path.GetFileName(attachment) } }
            }
        }));
        testReminderIds.Add(inspection.Id);
        VehicleReminders.Follow(null, van);
        Invoke(main, "NavigateTo", garage);
        await Wait(600);
        string profilesFile = AppPaths.Data("profiles.json");
        byte[] start = Bytes(profilesFile);
        Dictionary<Guid, string> reminders = RemindersNow();

        Invoke(main, "RemoveMemories", (IReadOnlyList<Profile>)new List<Profile> { garage, house });
        await Wait(300);
        byte[] deleted = Bytes(profilesFile);
        Check(!Profiles(main).Any(p => p.Id == garage.Id || p.Id == house.Id) && Content(main) is HomeView, "two memories deleted, the open one among them: Home");
        Check(Directory.Exists(MemoryFiles.Folder(garage)) && Directory.Exists(MemoryFiles.Folder(house)) && File.Exists(picture) && File.Exists(attachment),
            "their folders and files stay while it can be undone");
        Check(!RemindersNow().ContainsKey(inspection.Id) && TaskTrigger(inspection.Id) == null, "the van's reminder gone");
        Check(main.Undo.Shown == Loc.Plural("Undo.Deleted.Memories", 2), "the toast: " + main.Undo.Shown);
        Check(main.Undo.Undo() && Same(Bytes(profilesFile), start), "undone: profiles.json byte for byte");
        Check(Profiles(main).Any(p => p.Id == garage.Id) && Profiles(main).Any(p => p.Id == house.Id)
              && Descendants(main).OfType<Border>().Any(b => b.Name == "Nav" + garage.Id.ToString("N")), "both back in the sidebar");
        CheckReminderBack(inspection.Id, reminders[inspection.Id], "the van's reminder back once, with its task");
        Check(EntityStore.Load(garage).Single().Id == van.Id, "and the van in its collection");
        Check(main.Undo.Redo() && Same(Bytes(profilesFile), deleted) && Directory.Exists(MemoryFiles.Folder(garage)), "redone: deleted again, the folder still kept");

        // The sidebar's history lasts the session; once it ends, the folders and their files go.
        UndoService.EndAll();
        Check(!Directory.Exists(MemoryFiles.Folder(garage)) && !Directory.Exists(MemoryFiles.Folder(house)), "it can't be undone any more: folders deleted");
        Check(!File.Exists(picture) && !File.Exists(attachment), "and the files they owned");
        Check(Directory.Exists(MemoryFiles.Folder(kept)), "another memory's folder untouched");
        Check(!RemindersNow().ContainsKey(inspection.Id) && TaskTrigger(inspection.Id) == null, "the reminder stays gone");

        Profile other = AddMemory(main, "Undo memory other", ProfileType.Project);
        kept = Profiles(main).First(p => p.Id == kept.Id); // the sidebar read profiles.json anew after the undo
        // AddMemory numbers by count, so the list is first put in the order the app keeps (by Order), as it reads it.
        List<Profile> ordered = Profiles(main).OrderBy(p => p.Order).ToList();
        Profiles(main).Clear();
        Profiles(main).AddRange(ordered);
        ProfileManager.TrySaveProfiles(Profiles(main));
        start = Bytes(profilesFile);
        Invoke(main, "MoveMemory", other, kept, false);
        Check(!Same(Bytes(profilesFile), start) && main.Undo.Undo() && Same(Bytes(profilesFile), start), "a memory moved in the sidebar, the move undone");
        RemoveMemory(main, other);
        RemoveMemory(main, kept);
    }

    // ---- To-dos ---------------------------------------------------------------------------------------------

    static async Task UndoTodos(MainWindow main)
    {
        Console.WriteLine("To-dos: Home's and a memory's, ticked (with their plan blocks), renamed, deleted");
        Invoke(main, "NavigateHome");
        await Wait(500);
        QuickTodo milk = QuickTodoService.Add("Undo milk", DateTime.Today), bread = QuickTodoService.Add("Undo bread", DateTime.Today),
            eggs = QuickTodoService.Add("Undo eggs", DateTime.Today.AddDays(1));
        DayPlanBlock linked = new DayPlanBlock { Id = Guid.NewGuid(), Date = DateTime.Today, StartMinute = 600, EndMinute = 660, Title = "Undo milk", Todo = TodoLink.ForHome(milk.Id) };
        DayPlanStore.Save(linked);
        QuickTodoListControl list = (QuickTodoListControl)((HomeView)Content(main)).FindName("QuickTodos");
        list.Refresh();
        string todosFile = QuickTodoService.FilePath, planFile = DayPlanStore.FilePath;
        byte[] todos = Bytes(todosFile), plan = Bytes(planFile);
        QuickTodo Own(Guid id) => Field<List<QuickTodo>>(list, "todos").First(t => t.Id == id);

        CheckBox tick = Field<Dictionary<Guid, CheckBox>>(list, "ticks")[milk.Id];
        tick.IsChecked = true;
        Invoke(list, "SetDone", Own(milk.Id), tick);
        Check(QuickTodoService.LoadTodos().Single(t => t.Id == milk.Id).IsDone && DayPlanStore.TryLoad(out DayPlanFile p) && p.Blocks.Single(b => b.Id == linked.Id).Done,
            "a Home to-do ticked, its plan block with it");
        Check(main.Undo.Undo() && Same(Bytes(todosFile), todos) && Same(Bytes(planFile), plan), "the tick undone: quicktodos.json and dayplan.json byte for byte");

        Invoke(list, "Remove", (IReadOnlyCollection<Guid>)new List<Guid> { milk.Id, bread.Id });
        await Wait(200);
        Check(QuickTodoService.LoadTodos().Count == 1 && main.Undo.Shown == Loc.Plural("Undo.Deleted.Todos", 2), "two deleted: " + main.Undo.Shown);
        Check(main.Undo.Undo() && Same(Bytes(todosFile), todos), "one undo for both");
        Invoke(list, "Rename", Own(eggs.Id), "Undo a dozen eggs");
        Check(main.Undo.Undo() && Same(Bytes(todosFile), todos), "a rename undone");

        Profile memory = AddMemory(main, "Undo to-dos", ProfileType.Project);
        List<TodoEntry> entries = new[] { "Paint", "Sand", "Prime" }.Select(t => new TodoEntry { Id = Guid.NewGuid(), Text = t }).ToList();
        MemoryTodoService.TrySave(memory, entries);
        DayPlanBlock painting = new DayPlanBlock { Id = Guid.NewGuid(), Date = DateTime.Today, StartMinute = 700, EndMinute = 760, Title = "Paint", Todo = TodoLink.ForMemory(memory, entries[0].Id) };
        DayPlanStore.Save(painting);
        Invoke(main, "NavigateTo", memory);
        await Wait(600);
        MemoryTodoPanel panel = (MemoryTodoPanel)((ProjectView)Content(main)).FindName("TodoPanel");
        string todoFile = MemoryFiles.Todos(memory);
        byte[] own = Bytes(todoFile);
        plan = Bytes(planFile);
        List<TodoEntry> Entries() => Field<List<TodoEntry>>(panel, "todos");

        CheckBox box = Field<Dictionary<TodoEntry, CheckBox>>(panel, "ticks")[Entries()[0]];
        box.IsChecked = true;
        box.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Check(MemoryTodoService.TryLoad(memory, out List<TodoEntry> ticked) && ticked[0].IsDone, "a memory's to-do ticked");
        Check(main.Undo.Undo() && Same(Bytes(todoFile), own) && Same(Bytes(planFile), plan), "undone, with its plan block, byte for byte");
        Invoke(panel, "Remove", (IReadOnlyList<TodoEntry>)Entries().ToList());
        await Wait(200);
        Check(!File.Exists(todoFile), "all three deleted: the emptied list's file goes, as before");
        Check(main.Undo.Undo() && Same(Bytes(todoFile), own), "undone: the file back, byte for byte");
        Invoke(panel, "Rename", Entries()[1], "Sand twice");
        Check(main.Undo.Undo() && Same(Bytes(todoFile), own), "a rename undone");
        RemoveMemory(main, memory);
        DayPlanStore.Delete(new[] { linked.Id, painting.Id });
        QuickTodoService.Delete(new[] { milk.Id, bread.Id, eggs.Id });
    }

    // ---- The Day Plan -------------------------------------------------------------------------------------------

    static async Task UndoPlan(MainWindow main)
    {
        Console.WriteLine("Day Plan: a block deleted (its reminder back once), moved, ticked, several deleted");
        DateTime day = DateTime.Today.AddDays(3);
        DayPlanBlock dentist = new DayPlanBlock { Id = Guid.NewGuid(), Date = day, StartMinute = 600, EndMinute = 660, Title = "Undo dentist", RemindMinutesBefore = 15 };
        DayPlanBlock gym = new DayPlanBlock { Id = Guid.NewGuid(), Date = day, StartMinute = 900, EndMinute = 960, Title = "Undo gym" };
        testReminderIds.Add(dentist.Id);
        Check(DayPlanActions.Save(dentist, true, null) && DayPlanActions.Save(gym, false, null), "two blocks, one with a reminder");
        main.ShowDayPlan(day);
        await Wait(600);
        string file = DayPlanStore.FilePath;
        byte[] start = Bytes(file);
        Dictionary<Guid, string> reminders = RemindersNow();
        DayPlanBlock Stored(Guid id) => DayPlanStore.TryLoad(out DayPlanFile plan) ? plan.Blocks.FirstOrDefault(b => b.Id == id) : null;

        Check(DayPlanActions.ConfirmDelete(Stored(dentist.Id)), "a block deleted");
        await Wait(200);
        Check(Stored(dentist.Id) == null && !RemindersNow().ContainsKey(dentist.Id) && TaskTrigger(dentist.Id) == null, "its reminder and task gone");
        Check(main.Undo.Shown == Loc.F("Undo.Deleted", "Undo dentist"), "the toast: " + main.Undo.Shown);
        Check(main.Undo.Undo() && Same(Bytes(file), start), "undone: dayplan.json byte for byte");
        CheckReminderBack(dentist.Id, reminders[dentist.Id], "its reminder back once, with its task");
        Check(main.Undo.Redo() && Stored(dentist.Id) == null && TaskTrigger(dentist.Id) == null, "redone: gone again, its task too");
        Check(main.Undo.Undo(), "undone again");
        CheckReminderBack(dentist.Id, reminders[dentist.Id], "still one reminder, one task");

        Check(DayPlanActions.Move(Stored(dentist.Id), 720, 780), "moved to 12:00");
        Check(ReminderManager.LoadReminders().Single(r => r.Id == dentist.Id).NextFireTime == day.AddMinutes(705), "its reminder moved along");
        Check(main.Undo.Undo() && Same(Bytes(file), start), "the move undone");
        CheckReminderBack(dentist.Id, reminders[dentist.Id], "and its reminder back at 09:45");

        Check(DayPlanActions.SetDone(Stored(gym.Id), true, null, Profiles(main)) && main.Undo.Undo() && Same(Bytes(file), start), "a tick undone");

        Check(DayPlanActions.ConfirmDelete(new List<DayPlanBlock> { Stored(dentist.Id), Stored(gym.Id) }), "both deleted at once");
        await Wait(200);
        Check(main.Undo.Shown == Loc.Plural("Undo.Deleted.Blocks", 2), "the toast: " + main.Undo.Shown);
        Check(main.Undo.Undo() && Same(Bytes(file), start), "one undo for both");
        CheckReminderBack(dentist.Id, reminders[dentist.Id], "with the reminder");
        DayPlanActions.ConfirmDelete(new List<DayPlanBlock> { Stored(dentist.Id), Stored(gym.Id) });
        Invoke(main, "NavigateHome");
    }

    // ---- Reminders -----------------------------------------------------------------------------------------------

    static async Task UndoReminders(MainWindow main)
    {
        Console.WriteLine("Reminders: deleted in their window and on Home, back with their tasks");
        Reminder call = new Reminder { Id = Guid.NewGuid(), Title = "", Message = "Undo call the bank", NextFireTime = DateTime.Today.AddDays(5).AddHours(10), RepeatType = RepeatType.Once };
        Reminder water = new Reminder { Id = Guid.NewGuid(), Title = "", Message = "Undo water the plants", NextFireTime = DateTime.Today.AddDays(2).AddHours(8), RepeatType = RepeatType.Weekly };
        testReminderIds.Add(call.Id);
        testReminderIds.Add(water.Id);
        ReminderManager.AddReminder(call);
        ReminderManager.AddReminder(water);
        Dictionary<Guid, string> start = RemindersNow();

        RemindersWindow.ShowOrFocus();
        await Wait(500);
        RemindersWindow window = Application.Current.Windows.OfType<RemindersWindow>().First();
        UndoHost host = UndoHost.Of(window);
        Invoke(window, "DeleteSelected", (IReadOnlyList<Guid>)new List<Guid> { call.Id, water.Id });
        await Wait(300);
        Check(!RemindersNow().ContainsKey(call.Id) && !RemindersNow().ContainsKey(water.Id) && TaskTrigger(call.Id) == null && TaskTrigger(water.Id) == null,
            "two deleted, their tasks with them");
        Check(host.Shown == Loc.Plural("Undo.Deleted.Reminders", 2), "the window's toast: " + host.Shown);
        Check(host.Undo(), "undone");
        CheckReminderBack(call.Id, start[call.Id], "the one-off back, with its task");
        CheckReminderBack(water.Id, start[water.Id], "the weekly one back, with its task");
        Check(host.Redo() && TaskTrigger(call.Id) == null && TaskTrigger(water.Id) == null && !RemindersNow().ContainsKey(call.Id), "redone: both gone again");
        Check(host.Undo(), "undone again");
        Check(!host.Undo() && host.Shown == Loc.T("Undo.NothingToUndo"), "nothing more: said so");
        window.Close();
        await Wait(200);

        Invoke(main, "NavigateHome");
        await Wait(300);
        Check(ReminderActions.ConfirmDelete(call.Id, UndoService.Page, call.Message), "one deleted from Home's week");
        Check(main.Undo.Undo(), "undone in the main window");
        CheckReminderBack(call.Id, start[call.Id], "back once, with its task");
        ReminderManager.DeleteReminder(call.Id);
        ReminderManager.DeleteReminder(water.Id);
    }

    // ---- Keys: a text box keeps its own Ctrl+Z; the newest step of the page and the sidebar --------------------

    static async Task UndoKeys(MainWindow main)
    {
        Console.WriteLine("Keys and the toast");
        Profile memory = AddMemory(main, "Undo keys", ProfileType.Collection);
        Profile spare = AddMemory(main, "Undo spare", ProfileType.Project);
        Entity shirt = Garment("Keys shirt", 1), socks = Garment("Keys socks", 2);
        File.WriteAllText(MemoryFiles.Items(memory), JsonSerializer.Serialize(new List<Entity> { shirt, socks }));
        Invoke(main, "NavigateTo", memory);
        await Wait(700);
        await Foreground(main);
        CollectionView view = (CollectionView)Content(main);
        bool Gone() => !EntityStore.Load(memory).Any(e => e.Id == shirt.Id);
        string front = InForeground() ? "" : " (not in front!)";

        Invoke(view, "DeleteEntity", Field<List<Entity>>(view, "entities").First(e => e.Id == shirt.Id));
        await Wait(300);
        TextBox search = (TextBox)view.FindName("SearchBox");
        search.Focus();
        await Held(0x11, () => PressKey(search, Key.Z));
        Check(Gone(), "Ctrl+Z in the search box (nothing typed) doesn't undo the delete" + front);
        search.Text = "sock";
        await Held(0x11, () => PressKey(search, Key.Z));
        Check(Gone(), "nor with something typed" + front);
        search.Text = "";
        ScrollViewer cards = (ScrollViewer)view.FindName("CardScroll");
        cards.Focus();
        await Held(0x11, () => PressKey(cards, Key.Z));
        Check(!Gone(), "Ctrl+Z on the cards: undone" + front);
        await Held(0x11, () => PressKey(cards, Key.Y));
        Check(Gone(), "Ctrl+Y: redone" + front);
        await Held(0x11, () => PressKey(cards, Key.Z));
        Check(!Gone(), "Ctrl+Z again" + front);

        // The toast after a delete: its Undo button is reachable and works; Esc in it only closes it.
        Invoke(view, "DeleteEntity", Field<List<Entity>>(view, "entities").First(e => e.Id == shirt.Id));
        await Wait(300);
        Button undo = main.Undo.ToastButton;
        Check(undo.IsVisible && undo.Focusable && undo.IsTabStop && (string)undo.Content == Loc.T("Undo.Button")
              && System.Windows.Automation.AutomationProperties.GetLiveSetting(Descendants(Window.GetWindow(undo)).OfType<TextBlock>()
                  .First(t => t.Text == main.Undo.Shown)) == System.Windows.Automation.AutomationLiveSetting.Polite,
            "the toast: a keyboard-reachable Undo button, read out politely");
        undo.Focus();
        PressKey(undo, Key.Escape);
        Check(main.Undo.Shown == null && main.IsVisible && Gone(), "Esc in the toast closes it, nothing else");
        Invoke(view, "DeleteEntity", Field<List<Entity>>(view, "entities").First(e => e.Id == socks.Id));
        await Wait(300);
        main.Undo.ToastButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Check(EntityStore.Load(memory).Any(e => e.Id == socks.Id), "its Undo button undoes the delete");

        // The page's step and the sidebar's: the newest goes first.
        Invoke(main, "RemoveMemories", (IReadOnlyList<Profile>)new List<Profile> { spare });
        await Wait(200);
        Invoke(main, "NavigateTo", Profiles(main).First(p => p.Id == memory.Id));
        await Wait(400);
        Check(main.Undo.Undo() && Profiles(main).Any(p => p.Id == spare.Id) && Gone(), "Ctrl+Z: the newer step first (the memory), the item still deleted");
        Check(main.Undo.Undo() && !Gone(), "then the item");
        Check(main.Undo.Redo() && Gone(), "Ctrl+Y: the last undone first (the item)");
        Check(main.Undo.Redo() && !Profiles(main).Any(p => p.Id == spare.Id), "then the memory");
        Check(main.Undo.Undo(), "(the memory back)");
        RemoveMemory(main, spare);
        RemoveMemory(main, memory);
    }

    // ---- The words, in each language ---------------------------------------------------------------------------

    static void UndoLanguages()
    {
        Console.WriteLine("Labels in three languages");
        string was = Loc.Current;
        try
        {
            Loc.Switch("hu");
            Check(Loc.F("Undo.Deleted", "Autó") == "„Autó” törölve" && Loc.Plural("Undo.Deleted.Items", 3) == "3 elem törölve", "Hungarian: " + Loc.Plural("Undo.Deleted.Items", 3));
            Loc.Switch("sr-Latn");
            Check(Loc.Plural("Undo.Deleted.Items", 1) == "Obrisana je 1 stavka" && Loc.Plural("Undo.Deleted.Items", 3) == "Obrisane su 3 stavke"
                  && Loc.Plural("Undo.Deleted.Items", 5) == "Obrisano je 5 stavki" && Loc.Plural("Undo.Deleted.Items", 21) == "Obrisana je 21 stavka",
                "Serbian: one, few, other (1, 3, 5, 21)");
            Check(Loc.F("Undo.Undone", Loc.F("Undo.Deleted", "Auto")) == "Poništeno: Obrisano: „Auto“", "Serbian toast after an undo: " + Loc.F("Undo.Undone", Loc.F("Undo.Deleted", "Auto")));
            Loc.Switch("en");
            Check(Loc.Plural("Undo.Deleted.Items", 1) == "1 item deleted" && Loc.Plural("Undo.Deleted.Items", 2) == "2 items deleted", "English: one, other");
        }
        finally
        {
            Loc.Switch(was);
        }
    }

    // ---- Found in review ---------------------------------------------------------------------------------------------

    static async Task UndoReviewFixes(MainWindow main)
    {
        Console.WriteLine("Review findings");
        await MemoryGainedBetween(main);
        await PageTextAfterUndo(main);
        await ReminderEditCancelled(main);
        await PassedTimes(main);
        await ReminderLeftAlone(main);
        await RemindersUnreadable(main);
    }

    // A deleted memory brought back, given a new picture and a new dated document, then deleted again by a redo: the page
    // that held the picture's step ends, yet the picture stays (the memory can come back); the new reminder goes with
    // the redo and comes back with the next undo; all of it goes once nothing can bring the memory back.
    static async Task MemoryGainedBetween(MainWindow main)
    {
        Profile memory = AddMemory(main, "Undo gained", ProfileType.Collection);
        Entity car = Vehicle("Gained car");
        EntityRecord first = Document("Insurance", DateTime.Today.AddDays(70), 30, "First");
        VehicleSchema.Documents.Write(car, new List<EntityRecord> { first });
        File.WriteAllText(MemoryFiles.Items(memory), JsonSerializer.Serialize(new List<Entity> { car }));
        testReminderIds.Add(first.Id);
        VehicleReminders.Follow(null, car);
        Invoke(main, "RemoveMemories", (IReadOnlyList<Profile>)new List<Profile> { memory });
        await Wait(200);
        Check(main.Undo.Undo() && Profiles(main).Any(p => p.Id == memory.Id), "a memory deleted and brought back");

        Invoke(main, "NavigateTo", Profiles(main).First(p => p.Id == memory.Id));
        await Wait(600);
        CollectionView view = (CollectionView)Content(main);
        string picture = StoreFile(ImageStore.Items.Folder, "gained.png");
        Entity edited = Copy(Field<List<Entity>>(view, "entities").Single());
        edited.ImagePath = Path.GetFileName(picture);
        EntityRecord second = Document("Inspection", DateTime.Today.AddDays(80), 30, "Second");
        testReminderIds.Add(second.Id);
        VehicleSchema.Documents.Write(edited, VehicleSchema.Documents.Read(edited).Append(second).ToList());
        Check((bool)Call(view, "Replace", edited) && RemindersNow().ContainsKey(second.Id), "then given a picture and a dated document (its reminder)");
        Dictionary<Guid, string> reminders = RemindersNow();

        Check(main.Undo.Redo() && !Profiles(main).Any(p => p.Id == memory.Id) && Content(main) is HomeView,
            "Ctrl+Y on its page: the memory's delete again, the page (and its history) left");
        Check(File.Exists(picture), "the new picture stays: the memory can still come back");
        Check(!RemindersNow().ContainsKey(first.Id) && !RemindersNow().ContainsKey(second.Id) && TaskTrigger(second.Id) == null,
            "both documents' reminders gone, the new one too");
        Check(main.Undo.Undo() && EntityStore.Load(Profiles(main).First(p => p.Id == memory.Id)).Single().ImagePath == Path.GetFileName(picture)
              && File.Exists(picture), "undone: back with its picture");
        CheckReminderBack(first.Id, reminders[first.Id], "the first reminder back once");
        CheckReminderBack(second.Id, reminders[second.Id], "the new one back once too");
        Check(main.Undo.Redo(), "deleted again");
        UndoService.EndAll();
        Check(!File.Exists(picture) && !Directory.Exists(MemoryFiles.Folder(memory)), "nothing can bring it back: its folder and the new picture go");
        Check(!RemindersNow().ContainsKey(second.Id) && TaskTrigger(second.Id) == null, "and no reminder of it is left");
    }

    // Project Details: a page deleted, brought back, written on, deleted again by a redo and brought back again keeps
    // what was written; so does the only page emptied.
    static async Task PageTextAfterUndo(MainWindow main)
    {
        Project project = SampleProject(1);
        project.NotePages = Enumerable.Range(1, 3).Select(i => new NotePage { Body = RichTextHelper.ToStorableString(Doc("Page text " + i)) }).ToList();
        ProjectWindow window = await OpenProject(main, project);
        UndoHost host = UndoHost.Of(window);
        List<NotePage> Pages() => Field<List<NotePage>>(window, "pages");
        List<string> Bodies() => Field<List<string>>(window, "pageBodies");
        RichTextBox editor = (RichTextBox)window.FindName("NotesBox");
        try
        {
            NotePage second = Pages()[1];
            Invoke(window, "DeletePages", (IReadOnlyList<NotePage>)new[] { second });
            Check(host.Undo() && Pages().Contains(second), "a page deleted and brought back");
            Invoke(window, "FlipTo", Pages().IndexOf(second));
            editor.Document = Doc("Written after the undo");
            Check(host.Redo() && !Pages().Contains(second), "deleted again by a redo");
            Check(host.Undo() && RichTextHelper.ToPlainText(Bodies()[Pages().IndexOf(second)]).Contains("Written after the undo"),
                "brought back again: with what was written after the first undo");

            Invoke(window, "DeletePages", (IReadOnlyList<NotePage>)Pages().ToList());
            Check(host.Undo(), "every page deleted and brought back");
            Invoke(window, "FlipTo", 0);
            editor.Document = Doc("Page one, rewritten");
            Check(host.Redo() && host.Undo() && RichTextHelper.ToPlainText(Bodies()[0]).Contains("Page one, rewritten"),
                "redone and undone: page 1 keeps what was written on it");
        }
        finally
        {
            window.Close();
        }

        Project single = SampleProject(1);
        single.NotePages = new List<NotePage> { new NotePage { Body = RichTextHelper.ToStorableString(Doc("The only page")) } };
        window = await OpenProject(main, single);
        host = UndoHost.Of(window);
        editor = (RichTextBox)window.FindName("NotesBox");
        try
        {
            Invoke(window, "DeletePages", (IReadOnlyList<NotePage>)Field<List<NotePage>>(window, "pages").ToList());
            Check(host.Undo(), "the only page emptied and brought back");
            editor.Document = Doc("The only page, more");
            Check(host.Redo() && host.Undo() && RichTextHelper.ToPlainText(RichTextHelper.ToStorableString(editor.Document)).Contains("The only page, more"),
                "emptied again and brought back: with what was written in between");
        }
        finally
        {
            window.Close();
        }
        await Wait(200);
    }

    // A reminder's form cancelled while the reminder fired meanwhile: no step, nothing for an undo to bring back twice.
    static async Task ReminderEditCancelled(MainWindow main)
    {
        Invoke(main, "NavigateHome");
        await Wait(300);
        Reminder once = new Reminder { Id = Guid.NewGuid(), Title = "", Message = "Undo fires meanwhile", NextFireTime = DateTime.Today.AddDays(4).AddHours(9), RepeatType = RepeatType.Once };
        testReminderIds.Add(once.Id);
        ReminderManager.AddReminder(once);
        UndoStep before = UndoService.Page.NextUndo;
        Application.Current.Dispatcher.BeginInvoke(new Action(async () =>
        {
            AddReminderWindow form = await WaitFor(() => Application.Current.Windows.OfType<AddReminderWindow>().FirstOrDefault());
            await Wait(300);
            ReminderManager.DeleteReminder(once.Id); // as if it fired: a one-off is gone once shown
            form.Close();
        }));
        Check(!ReminderActions.Edit(main, once.Id, UndoService.Page, once.Message), "the reminder's form, cancelled");
        Check(UndoService.Page.NextUndo == before, "no step: the firing meanwhile isn't taken for the user's change");
        Check(!RemindersNow().ContainsKey(once.Id), "and the fired reminder stays gone");
    }

    // A reminder brought back after its time: never scheduled in the past. A weekly one moves on to its next time, a
    // one-off fires in a minute; a redo and an undo after that still work.
    static async Task PassedTimes(MainWindow main)
    {
        Reminder weekly = new Reminder { Id = Guid.NewGuid(), Title = "", Message = "Undo weekly, passed", NextFireTime = DateTime.Now.AddDays(-2).AddMinutes(-3), RepeatType = RepeatType.Weekly };
        Reminder once = new Reminder { Id = Guid.NewGuid(), Title = "", Message = "Undo once, passed", NextFireTime = DateTime.Now.AddHours(-1), RepeatType = RepeatType.Once };
        testReminderIds.Add(weekly.Id);
        testReminderIds.Add(once.Id);
        List<Reminder> all = ReminderManager.LoadReminders();
        all.Add(weekly);
        all.Add(once);
        File.WriteAllText(AppPaths.Data("reminders.json"), JsonSerializer.Serialize(all)); // stored as they would be had they not fired yet
        UndoHistory history = new UndoHistory("harness");
        try
        {
            Check(ReminderActions.ConfirmDelete(weekly.Id, history, weekly.Message) && ReminderActions.ConfirmDelete(once.Id, history, once.Message),
                "two reminders whose time has passed deleted");
            DateTime now = DateTime.Now;
            Check(history.Apply(history.NextUndo, true, main) && history.Apply(history.NextUndo, true, main), "both undone");
            Reminder w = ReminderManager.LoadReminders().Single(r => r.Id == weekly.Id), o = ReminderManager.LoadReminders().Single(r => r.Id == once.Id);
            Check(w.NextFireTime > now && w.NextFireTime.TimeOfDay == weekly.NextFireTime.TimeOfDay && (w.NextFireTime - weekly.NextFireTime).TotalDays % 7 == 0
                  && OnTime(TaskTrigger(weekly.Id), w.NextFireTime), "the weekly one at its next time, with its task: " + w.NextFireTime.ToString("MM-dd HH:mm"));
            Check(o.NextFireTime > now && o.NextFireTime <= now.AddMinutes(2) && OnTime(TaskTrigger(once.Id), o.NextFireTime), "the one-off within a minute, with its task");
            Check(history.Apply(history.NextRedo, false, main) && history.Apply(history.NextRedo, false, main)
                  && !RemindersNow().ContainsKey(once.Id) && !RemindersNow().ContainsKey(weekly.Id) && TaskTrigger(once.Id) == null && TaskTrigger(weekly.Id) == null,
                "redone: both, as the undo wrote them, go again with their tasks");
            Check(history.Apply(history.NextUndo, true, main) && history.Apply(history.NextUndo, true, main)
                  && RemindersNow().ContainsKey(once.Id) && RemindersNow().ContainsKey(weekly.Id), "and come back again");
        }
        finally
        {
            history.End();
            ReminderManager.DeleteReminder(weekly.Id);
            ReminderManager.DeleteReminder(once.Id);
        }
    }

    // An item's change undone after its reminder was deleted elsewhere: the reminder is left alone, and that is said.
    static async Task ReminderLeftAlone(MainWindow main)
    {
        Profile memory = AddMemory(main, "Undo left alone", ProfileType.Collection);
        Entity car = Vehicle("Left alone car");
        EntityRecord insurance = Document("Insurance", DateTime.Today.AddDays(70), 30, "Alone");
        VehicleSchema.Documents.Write(car, new List<EntityRecord> { insurance });
        File.WriteAllText(MemoryFiles.Items(memory), JsonSerializer.Serialize(new List<Entity> { car }));
        testReminderIds.Add(insurance.Id);
        VehicleReminders.Follow(null, car);
        Invoke(main, "NavigateTo", memory);
        await Wait(600);
        CollectionView view = (CollectionView)Content(main);
        Entity edited = Copy(Field<List<Entity>>(view, "entities").Single());
        List<EntityRecord> documents = VehicleSchema.Documents.Read(edited);
        documents[0].Set(VehicleDocuments.ValidUntilKey, Values.FromDate(DateTime.Today.AddDays(90)));
        VehicleSchema.Documents.Write(edited, documents);
        Call(view, "Replace", edited);
        ReminderManager.DeleteReminder(insurance.Id); // deleted in Reminders meanwhile
        int boxes = selectBoxes.Count;
        Check(main.Undo.Undo(), "the date change undone");
        Check(!RemindersNow().ContainsKey(insurance.Id), "the reminder deleted meanwhile isn't brought back by it");
        Check(selectBoxes.Skip(boxes).Any(b => b.Contains(Loc.T("Undo.RemindersNote"))), "and that is said");
        RemoveMemory(main, memory);
    }

    // reminders.json unreadable while an item is deleted: the delete is still undone, and the undo says the reminders
    // may not be as they were.
    static async Task RemindersUnreadable(MainWindow main)
    {
        Profile memory = AddMemory(main, "Undo unreadable", ProfileType.Collection);
        File.WriteAllText(MemoryFiles.Items(memory), JsonSerializer.Serialize(new List<Entity> { Garment("Unreadable coat") }));
        Invoke(main, "NavigateTo", memory);
        await Wait(600);
        CollectionView view = (CollectionView)Content(main);
        string file = AppPaths.Data("reminders.json");
        string reminders = File.ReadAllText(file);
        File.WriteAllText(file, "{ not json");
        try
        {
            Invoke(view, "DeleteEntity", Field<List<Entity>>(view, "entities").Single());
            await Wait(200);
        }
        finally
        {
            File.WriteAllText(file, reminders);
        }
        int boxes = selectBoxes.Count;
        Check(main.Undo.Undo() && EntityStore.Load(memory).Count == 1, "the delete undone all the same");
        Check(selectBoxes.Skip(boxes).Any(b => b.Contains(Loc.T("Undo.RemindersNote"))), "the undo says the reminders may not be as they were");
        RemoveMemory(main, memory);
    }

    // The app closing (Shutdown) ends every history; a change finishing after that (Project Details closed by the
    // shutdown) isn't recorded, so its files go at once instead of being held by a history that never ends.
    static async Task UndoAfterShutdown(MainWindow main)
    {
        Console.WriteLine("After the app began closing");
        Profile memory = AddMemory(main, "Undo closing", ProfileType.Collection);
        string picture = StoreFile(ImageStore.Items.Folder, "closing.png");
        Entity coat = Garment("Closing coat");
        coat.ImagePath = Path.GetFileName(picture);
        File.WriteAllText(MemoryFiles.Items(memory), JsonSerializer.Serialize(new List<Entity> { coat }));
        Invoke(main, "NavigateTo", memory);
        await Wait(600);
        CollectionView view = (CollectionView)Content(main);
        UndoService.Shutdown();
        Invoke(view, "DeleteEntity", Field<List<Entity>>(view, "entities").Single());
        await Wait(200);
        Check(UndoService.Page.NextUndo == null && !File.Exists(picture), "a delete after Shutdown: no step, its picture deleted at once");
    }

    // ---- Helpers ---------------------------------------------------------------------------------------------------

    static byte[] Bytes(string path) => File.Exists(path) ? File.ReadAllBytes(path) : null;

    static bool Same(byte[] a, byte[] b) => a == null ? b == null : b != null && a.AsSpan().SequenceEqual(b);

    static Dictionary<Guid, string> RemindersNow() =>
        ReminderManager.LoadReminders().GroupBy(r => r.Id).ToDictionary(g => g.Key, g => JsonSerializer.Serialize(g.First()));

    // The reminder is in reminders.json once, exactly as it was, and its task fires at its time.
    static void CheckReminderBack(Guid id, string json, string what)
    {
        List<Reminder> all = ReminderManager.LoadReminders().Where(r => r.Id == id).ToList();
        DateTime? trigger = TaskTrigger(id);
        Check(all.Count == 1 && JsonSerializer.Serialize(all[0]) == json && trigger == all[0].NextFireTime,
            what + " [" + all.Count + " in the file, task " + trigger?.ToString("MM-dd HH:mm") + "]");
    }

    // A task's trigger is kept to the second (ReminderManager.IsHealthy allows the same).
    static bool OnTime(DateTime? trigger, DateTime when) => trigger != null && Math.Abs((trigger.Value - when).TotalSeconds) < 1;

    static Entity Copy(Entity entity) => JsonSerializer.Deserialize<Entity>(JsonSerializer.Serialize(entity));

    static object Call(object o, string method, params object[] args) =>
        o.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).Invoke(o, args);

    static System.Windows.Documents.FlowDocument Doc(string text) =>
        new System.Windows.Documents.FlowDocument(new System.Windows.Documents.Paragraph(new System.Windows.Documents.Run(text)));

    // Ctrl and Shift held, as for Ctrl+Shift+Z.
    static async Task CtrlShift(Action act)
    {
        keybd_event(0x11, 0, 0, UIntPtr.Zero);
        keybd_event(0x10, 0, 0, UIntPtr.Zero);
        await Wait(150);
        try
        {
            act();
        }
        finally
        {
            keybd_event(0x10, 0, 2, UIntPtr.Zero);
            keybd_event(0x11, 0, 2, UIntPtr.Zero);
            await Wait(150);
        }
    }
}
