using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TidyMind;

// Harness select <data> <shotsFolder>: selecting several items with Ctrl and Shift and deleting them at once, in every
// list that deletes one item at a time (ItemSelection): memories, projects, collection items, tasks, files, Day Plan
// blocks, memory and Home to-dos, reminders. The selection rules, one confirmation per batch, the same cleanup as one
// delete (folders, pictures, files, reminder tasks), a failure part-way, the open memory, three languages, and
// screenshots of the selected look on the dark sidebar and the light pages. Message boxes are answered by a watcher
// (Delete/Yes/OK, or Cancel/No when asked) and logged.
static partial class Program
{
    static readonly List<string> selectBoxes = new List<string>();
    static volatile bool answerCancel;

    static async Task SelectScenarios(MainWindow main)
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
            await SelectionRules(main);
            await MemoryBatches(main);
            await ProjectBatches(main);
            await CollectionBatches(main);
            await TaskAndFileBatches(main);
            await NotebookPageBatches(main);
            await DayPlanBatches(main);
            await TodoBatches(main);
            await ReminderBatches(main);
            await Languages(main);
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

    // ---- The rules, on the sidebar ------------------------------------------

    static async Task SelectionRules(MainWindow main)
    {
        Console.WriteLine("Selection rules (sidebar)");
        List<Profile> made = new List<Profile>();
        foreach (string name in new[] { "Wardrobe", "Garage", "Books" })
            made.Add(AddMemory(main, name, ProfileType.Collection));
        foreach (string name in new[] { "Studies", "House", "Garden" })
            made.Add(AddMemory(main, name, ProfileType.Project));
        await Wait(300);
        ItemSelection<Profile> selection = Field<ItemSelection<Profile>>(main, "memorySelection");
        List<Profile> shown = Profiles(main).Where(p => p.Type == ProfileType.Collection)
            .Concat(Profiles(main).Where(p => p.Type == ProfileType.Project)).ToList();

        selection.Click(shown[0], ModifierKeys.Control);
        selection.Click(shown[2], ModifierKeys.Control);
        Check(selection.Selected.SequenceEqual(new[] { shown[0], shown[2] }), "Ctrl+click adds one at a time");
        selection.Click(shown[2], ModifierKeys.Control);
        Check(selection.Selected.SequenceEqual(new[] { shown[0] }), "Ctrl+click on a selected one takes it out");
        selection.Click(shown[4], ModifierKeys.Shift);
        Check(selection.Selected.SequenceEqual(shown.Skip(2).Take(3)),
            "Shift+click: the run from the last clicked one (even though Ctrl took it out again), across both groups");
        selection.Click(shown[1], ModifierKeys.Shift);
        Check(selection.Selected.SequenceEqual(shown.Skip(1).Take(2)), "Shift+click again: from the same start, the other way");
        selection.Click(shown[3], ModifierKeys.None);
        Check(selection.Count == 0, "a plain click drops the selection (and opens, as before)");
        selection.Click(shown[5], ModifierKeys.Control | ModifierKeys.Shift);
        Check(selection.Selected.SequenceEqual(shown.Skip(3).Take(3)), "Ctrl+Shift+click: the run from the plain-clicked one");
        selection.Click(shown[0], ModifierKeys.Control | ModifierKeys.Shift);
        Check(selection.Count == shown.Count, "Ctrl+Shift adds a second run to the first");

        ScrollViewer nav = (ScrollViewer)main.FindName("NavScroll");
        nav.Focus();
        PressKey(nav, Key.Escape);
        Check(selection.Count == 0, "Esc clears");
        selection.SelectAll();
        Check(selection.Count == shown.Count, "Ctrl+A's SelectAll takes every memory shown");
        Border bar = (Border)main.FindName("NavSelectionBar");
        Check(bar.IsVisible && ((TextBlock)main.FindName("NavSelectionCount")).Text == Loc.F("Selection.Count", shown.Count),
            "the sidebar bar says how many");
        Border item = NavItem(main, shown[1]);
        Check(item.Background == SelectionLook.SidebarFill, "a selected memory has the selected fill");
        RenderWindow(main, "select-01-sidebar-dark");
        selection.ClearSelection();
        Check(!bar.IsVisible && item.Background != SelectionLook.SidebarFill, "cleared: bar gone, look back");

        // Real input: Ctrl and Shift held while the item gets the press, as a mouse click would deliver it.
        Console.WriteLine("Real keys and presses");
        if (!InForeground())
            await Foreground(main);
        string before = Field<string>(main, "selectedKey");
        // The drag starts from the press: right after it, the sidebar's drag must not hold the item (a plain press does).
        object drag = Field<object>(main, "memoryDrag");
        System.Reflection.FieldInfo pressed = drag.GetType().GetField("pressed", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        object heldByPlain = null, heldByCtrl = new object();
        MouseButton(NavItem(main, shown[2]), down: true);
        heldByPlain = pressed.GetValue(drag);
        MouseButton(NavItem(main, shown[2]), down: false);
        await Wait(300);
        selection.ClearSelection();
        before = Field<string>(main, "selectedKey");
        await Held(0x11, () =>
        {
            MouseButton(NavItem(main, shown[1]), down: true);
            heldByCtrl = pressed.GetValue(drag);
            MouseButton(NavItem(main, shown[1]), down: false);
        });
        await Held(0x11, () => Press(NavItem(main, shown[3])));
        Check(selection.Selected.SequenceEqual(new[] { shown[1], shown[3] }) && Field<string>(main, "selectedKey") == before,
            "Ctrl+press on two items selects them and opens neither" + (InForeground() ? "" : " (not in front!)"));
        Check(heldByPlain != null && heldByCtrl == null, "a plain press arms the drag; a Ctrl+press doesn't");
        await Held(0x10, () => Press(NavItem(main, shown[5])));
        Check(selection.Selected.SequenceEqual(shown.Skip(3).Take(3)), "Shift+press: the run from the last Ctrl+pressed");
        await Held(0x11, () => PressKey(nav, Key.A));
        Check(selection.Count == shown.Count, "Ctrl+A in the sidebar selects every memory");
        Press(nav); // blank space under the items
        Check(selection.Count == 0, "a press on blank space clears");
        Press(NavItem(main, shown[4]));
        await Wait(300);
        Check(Field<string>(main, "selectedKey") == shown[4].Id.ToString("N"), "a plain press still opens the memory");

        foreach (Profile p in made)
            Profiles(main).Remove(p);
        ProfileManager.TrySaveProfiles(Profiles(main));
        Invoke(main, "NavigateHome");
    }

    // ---- Memories -----------------------------------------------------------

    static async Task MemoryBatches(MainWindow main)
    {
        Console.WriteLine("Memories: delete several at once");
        List<Profile> memories = Enumerable.Range(1, 8).Select(i => AddMemory(main, "Memory " + i, ProfileType.Project)).ToList();
        Profile withFiles = memories[1];
        string attachment = StoreFile(AttachmentStore.Folder, "notes.pdf");
        string card = StoreFile(ImageStore.Cards.Folder, "card.png");
        File.WriteAllText(MemoryFiles.Items(withFiles), System.Text.Json.JsonSerializer.Serialize(new List<Project>
        {
            new Project
            {
                Id = Guid.NewGuid(), Name = "With files", Tasks = new List<TaskItem>(),
                CardBackgroundType = BackgroundFill.Image, CardBackgroundImagePath = Path.GetFileName(card),
                Attachments = new List<Attachment> { new Attachment { Id = Guid.NewGuid(), DisplayName = "notes.pdf", StoredFileName = Path.GetFileName(attachment) } }
            }
        }));
        Invoke(main, "NavigateTo", memories[0]); // the open one is among those deleted
        await Wait(500);
        ItemSelection<Profile> selection = Field<ItemSelection<Profile>>(main, "memorySelection");

        selection.Click(memories[0], ModifierKeys.Control);
        selection.Click(memories[1], ModifierKeys.Control);
        answerCancel = true;
        int boxes = selectBoxes.Count;
        selection.DeleteSelected();
        answerCancel = false;
        Check(Profiles(main).Contains(memories[0]) && Directory.Exists(MemoryFiles.Folder(memories[1])) && selection.Count == 2,
            "Cancel: nothing deleted, still selected");
        string box = selectBoxes.Skip(boxes).FirstOrDefault() ?? "";
        Check(box.Contains(Loc.Plural("Bulk.Delete.Memories", 2)) && box.Contains("Memory 1") && box.Contains("Memory 2")
              && box.Contains(Loc.T("Bulk.MemoriesWarning")) && box.EndsWith("[" + Loc.T("Common.Delete") + "][" + Loc.T("Common.Cancel") + "]"),
            "one question: how many, their names, the stronger warning, Delete / Cancel: " + box);

        // A failed save: nothing goes, folders stay.
        string profilesFile = AppPaths.Data("profiles.json");
        File.SetAttributes(profilesFile, FileAttributes.ReadOnly);
        boxes = selectBoxes.Count;
        selection.DeleteSelected();
        File.SetAttributes(profilesFile, FileAttributes.Normal);
        Check(Profiles(main).Contains(memories[0]) && Profiles(main).Contains(memories[1]) && Directory.Exists(MemoryFiles.Folder(memories[1]))
              && File.Exists(attachment), "profiles.json can't be written: neither is deleted, nothing on disk goes");
        Check(selectBoxes.Skip(boxes).Any(b => b.StartsWith(Loc.T("Common.CouldntSave"))), "and it says so");

        Check(selection.Count == 2, "the failed delete keeps the selection");
        ScrollViewer nav = (ScrollViewer)main.FindName("NavScroll");
        nav.Focus();
        PressKey(nav, Key.Delete);
        await Wait(500);
        ProfileManager.TryLoadProfiles(out List<Profile> stored, out _);
        Check(!stored.Any(p => p.Id == memories[0].Id || p.Id == memories[1].Id) && stored.Count(p => p.Name.StartsWith("Memory ")) == 6,
            "Delete key: both gone from profiles.json, the others kept");
        // Kept while the delete can be undone (UndoService), gone once it can't.
        Check(Directory.Exists(MemoryFiles.Folder(memories[0])) && Directory.Exists(MemoryFiles.Folder(memories[1]))
              && File.Exists(attachment) && File.Exists(card), "their folders and files stay while the delete can be undone");
        UndoService.EndAll();
        Check(!Directory.Exists(MemoryFiles.Folder(memories[0])) && !Directory.Exists(MemoryFiles.Folder(memories[1])),
            "their folders deleted once it can't be undone any more");
        Check(!File.Exists(attachment) && !File.Exists(card), "their attached file and card picture deleted then, as for one memory");
        Check(Content(main) is HomeView && Field<string>(main, "selectedKey") == "__home__", "the open one was among them: Home is shown");
        Check(selection.Count == 0 && !((Border)main.FindName("NavSelectionBar")).IsVisible, "selection and bar gone");

        // Six or more: five names, then "and N more".
        foreach (Profile p in memories.Skip(2))
            selection.Click(p, ModifierKeys.Control);
        answerCancel = true;
        boxes = selectBoxes.Count;
        selection.DeleteSelected();
        answerCancel = false;
        box = selectBoxes.Skip(boxes).FirstOrDefault() ?? "";
        Check(box.Contains("Memory 7") && !box.Contains("Memory 8") && box.Contains(Loc.Plural("Bulk.More", 1)),
            "six selected: five names, then the rest counted: " + box);
        selection.ClearSelection();
        foreach (Profile p in memories.Skip(2))
            Profiles(main).Remove(p);
        ProfileManager.TrySaveProfiles(Profiles(main));
        Invoke(main, "NavigateHome");
    }

    // ---- Projects -------------------------------------------------------------

    static async Task ProjectBatches(MainWindow main)
    {
        Console.WriteLine("Projects: Ctrl, Shift, Ctrl+A, Delete on the card grid");
        Profile memory = AddMemory(main, "Projects test", ProfileType.Project);
        string attachment = StoreFile(AttachmentStore.Folder, "plan.pdf");
        List<Project> projects = Enumerable.Range(1, 7).Select(i => new Project
        {
            Id = Guid.NewGuid(), Name = "Project " + i, Order = i, Tasks = new List<TaskItem>(), LastModified = DateTime.Now,
            Attachments = i == 2 ? new List<Attachment> { new Attachment { Id = Guid.NewGuid(), DisplayName = "plan.pdf", StoredFileName = Path.GetFileName(attachment) } } : null
        }).ToList();
        File.WriteAllText(MemoryFiles.Items(memory), System.Text.Json.JsonSerializer.Serialize(projects));
        Invoke(main, "NavigateTo", memory);
        await Wait(800);
        ProjectView view = (ProjectView)Content(main);
        CardSelectionController<Project> selection = Field<CardSelectionController<Project>>(view, "selection");
        List<Project> shown = Field<List<Project>>(view, "allProjects");
        Panel grid = (Panel)view.FindName("ProjectPanel");
        ScrollViewer scroll = (ScrollViewer)view.FindName("CardScroll");

        await Held(0x11, () => Press((UIElement)grid.Children[1]));
        await Held(0x10, () => Press((UIElement)grid.Children[3]));
        Check(selection.Selected.SequenceEqual(shown.Skip(1).Take(3)), "Ctrl+press then Shift+press: projects 2-4");
        Check(Application.Current.Windows.OfType<ProjectWindow>().Count() == 0, "no project opened");
        Check(((FrameworkElement)view.FindName("SelectionBar")).IsVisible && !((FrameworkElement)view.FindName("Toolbar")).IsVisible,
            "the selection bar stands in for search and Add");
        RenderWindow(main, "select-02-project-cards");
        await Held(0x11, () => PressKey(scroll, Key.A));
        Check(selection.Count == 7, "Ctrl+A selects all seven");
        PressKey(scroll, Key.Escape);
        Check(selection.Count == 0, "Esc clears");

        // One selected: the question its own menu asks.
        selection.Click(shown[5], ModifierKeys.Control);
        answerCancel = true;
        int asked = selectBoxes.Count;
        PressKey(scroll, Key.Delete);
        answerCancel = false;
        string single = selectBoxes.Skip(asked).FirstOrDefault() ?? "";
        Check(single.Contains(Loc.F("Common.DeleteNamed", "Project 6")) && selection.Count == 1,
            "Delete with one selected asks about that one, as its menu does (Cancel keeps it selected): " + single);
        selection.ClearSelection();

        // Failure: projects.json can't be written; the cards come back.
        selection.Click(shown[0], ModifierKeys.Control);
        selection.Click(shown[1], ModifierKeys.Control);
        File.SetAttributes(MemoryFiles.Items(memory), FileAttributes.ReadOnly);
        selection.DeleteSelected();
        File.SetAttributes(MemoryFiles.Items(memory), FileAttributes.Normal);
        Check(ProjectStore.Load(memory).Count == 7 && Field<List<Project>>(view, "allProjects").Count == 7 && File.Exists(attachment),
            "projects.json can't be written: all seven still there, on disk and on screen; the file kept");

        shown = Field<List<Project>>(view, "allProjects");
        selection.Click(shown[1], ModifierKeys.Control);
        selection.Click(shown[4], ModifierKeys.Control);
        selection.Click(shown[6], ModifierKeys.Control);
        scroll.Focus();
        PressKey(scroll, Key.Delete);
        await Wait(400);
        List<string> left = ProjectStore.Load(memory).Select(p => p.Name).ToList();
        Check(left.SequenceEqual(new[] { "Project 1", "Project 3", "Project 4", "Project 6" }), "Delete key: three gone (" + string.Join(", ", left) + ")");
        Check(File.Exists(attachment), "the deleted project's attached file kept while the delete can be undone");
        Invoke(main, "NavigateHome"); // the page's undo history ends
        Check(!File.Exists(attachment), "and deleted once the page is left, as for one project");
        RemoveMemory(main, memory);
    }

    // ---- Collection items ---------------------------------------------------

    static async Task CollectionBatches(MainWindow main)
    {
        Console.WriteLine("Collection items");
        Profile memory = AddMemory(main, "Clothes test", ProfileType.Collection);
        string picture = StoreFile(ImageStore.Items.Folder, "shirt.png");
        List<Entity> items = Enumerable.Range(1, 5).Select(i => new Entity
        {
            Id = Guid.NewGuid(), EntityType = "Clothing", Name = "Shirt " + i, Order = i, CreatedDate = DateTime.Now,
            ImagePath = i == 3 ? Path.GetFileName(picture) : null
        }).ToList();
        File.WriteAllText(MemoryFiles.Items(memory), System.Text.Json.JsonSerializer.Serialize(items));
        Invoke(main, "NavigateTo", memory);
        await Wait(800);
        CollectionView view = (CollectionView)Content(main);
        CardSelectionController<Entity> selection = Field<CardSelectionController<Entity>>(view, "selection");
        List<Entity> shown = Field<List<Entity>>(view, "entities");
        selection.Click(shown[1], ModifierKeys.Control);
        selection.Click(shown[3], ModifierKeys.Shift);
        Check(selection.Count == 3, "Ctrl then Shift: three items");
        selection.DeleteSelected();
        await Wait(300);
        List<string> left = EntityStore.Load(memory).Select(x => x.Name).ToList();
        Check(left.SequenceEqual(new[] { "Shirt 1", "Shirt 5" }), "deleted together (" + string.Join(", ", left) + ")");
        Check(File.Exists(picture), "the item's picture kept while the delete can be undone");
        Invoke(main, "NavigateHome"); // the page's undo history ends
        Check(!File.Exists(picture), "and deleted once the page is left, as for one item");
        RemoveMemory(main, memory);
    }

    // ---- Tasks and files in a project -------------------------------------------

    static async Task TaskAndFileBatches(MainWindow main)
    {
        Console.WriteLine("Tasks and files in Project Details");
        string[] stored = Enumerable.Range(1, 4).Select(i => StoreFile(AttachmentStore.Folder, "file" + i + ".pdf")).ToArray();
        Project project = new Project
        {
            Id = Guid.NewGuid(), Name = "Kitchen", Tasks = Enumerable.Range(1, 8).Select(i => new TaskItem { Title = "Task " + i }).ToList(),
            Attachments = stored.Select((s, i) => new Attachment
            {
                Id = Guid.NewGuid(), DisplayName = "File " + (i + 1) + ".pdf", StoredFileName = Path.GetFileName(s), SizeBytes = 100, AddedDate = DateTime.Today
            }).ToList()
        };
        ProjectWindow window = await OpenProject(main, project);
        await Foreground(window);
        ListBox list = (ListBox)window.FindName("TaskList");
        ListBoxItem Row(int i) => (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(i);
        CheckBox Tick(int i) => Descendants(Row(i)).OfType<CheckBox>().First();

        bool ticked = project.Tasks[2].IsDone;
        await Held(0x11, () => Press(Tick(2)));
        await Held(0x11, () => Press(Tick(4)));
        Check(list.SelectedItems.Count == 2 && project.Tasks[2].IsDone == ticked, "Ctrl+press on two rows selects them and ticks neither");
        await Held(0x10, () => Press(Tick(6)));
        Check(list.SelectedItems.Count == 3 && list.SelectedItems.Contains(project.Tasks[5]), "Shift+press: the run from the last Ctrl+pressed (5-7)");
        RenderWindow(window, "select-03-tasks");
        PressKey(list, Key.Escape);
        await Wait(200);
        Check(window.IsVisible && list.SelectedItems.Count == 0, "Esc with several selected clears them and keeps the window open");

        list.Focus();
        await Held(0x11, () => PressKey(list, Key.A));
        Check(list.SelectedItems.Count == 8, "Ctrl+A selects all eight");
        list.UnselectAll();
        await Held(0x11, () => Press(Tick(0)));
        await Held(0x11, () => Press(Tick(7)));
        Check(list.SelectedItems.Count == 2, "Ctrl+press on the first and the last task");
        int boxes = selectBoxes.Count;
        PressKey((UIElement)Keyboard.FocusedElement, Key.Delete);
        await Wait(300);
        Check(project.Tasks.Count == 6 && !project.Tasks.Any(t => t.Title == "Task 1" || t.Title == "Task 8") && list.Items.Count == 6,
            "Delete: both gone from the project and the list");
        string box = selectBoxes.Skip(boxes).FirstOrDefault() ?? "";
        Check(box.Contains(Loc.Plural("Bulk.Delete.Tasks", 2)) && box.Contains("Task 1") && box.Contains("Task 8"), "one question for both: " + box);
        Check(list.SelectedItems.Count == 1 && Keyboard.FocusedElement is ListBoxItem, "the keyboard stays on a selected task, so a second Delete goes on");

        // Files: Ctrl+click two, Delete; Remove / Cancel; the stored copies go once the project is saved.
        AttachmentsSection files = (AttachmentsSection)window.FindName("Files");
        ItemSelection<Attachment> fileSelection = Field<ItemSelection<Attachment>>(files, "selection");
        StackPanel rows = (StackPanel)files.FindName("FileRows");
        rows.BringIntoView();
        await Wait(200);
        await Held(0x11, () => Press((UIElement)rows.Children[0]));
        await Held(0x11, () => Press((UIElement)rows.Children[2]));
        Check(fileSelection.Count == 2, "Ctrl+press selects two files");
        RenderWindow(window, "select-04-files");
        boxes = selectBoxes.Count;
        PressKey(rows, Key.Delete);
        await Wait(300);
        box = selectBoxes.Skip(boxes).FirstOrDefault() ?? "";
        Check(box.Contains(Loc.Plural("Bulk.Remove.Files", 2)) && box.EndsWith("[" + Loc.T("Files.Remove") + "][" + Loc.T("Common.Cancel") + "]"),
            "Remove / Cancel for files: " + box);
        Check(files.Attachments.Count == 2 && files.Removed.Count == 2, "two left, two taken out");
        window.Close();
        await Wait(300);
        AttachmentStore.Delete(window.RemovedAttachments); // what ProjectView does once the project is saved
        Check(!File.Exists(stored[0]) && !File.Exists(stored[2]) && File.Exists(stored[1]), "their copies deleted after the save, the others kept");
    }

    // ---- Notebook pages ----------------------------------------------------------

    // The ☰ page list in Project Details: the rules, a plain click still turns the page, the editor keeps its own
    // Delete, several deleted with the open page among them, the bar, the 🗑 button (the same delete), every page.
    static async Task NotebookPageBatches(MainWindow main)
    {
        Console.WriteLine("Notebook pages (the page list)");
        Project project = new Project
        {
            Id = Guid.NewGuid(), Name = "Diary", Tasks = new List<TaskItem>(),
            NotePages = Enumerable.Range(1, 8).Select(i => new NotePage { Body = "Page text " + i }).ToList()
        };
        List<NotePage> original = project.NotePages.ToList();
        ProjectWindow.OpenAtPage(project.Id, 2);
        ProjectWindow window = await OpenProject(main, project);
        await Foreground(window);
        ItemSelection<NotePage> selection = Field<ItemSelection<NotePage>>(window, "pageSelection");
        List<NotePage> Pages() => Field<List<NotePage>>(window, "pages");
        System.Windows.Controls.Primitives.Popup popup = (System.Windows.Controls.Primitives.Popup)window.FindName("PageListPopup");
        StackPanel rows = (StackPanel)window.FindName("PageListRows");
        SelectionBar bar = (SelectionBar)window.FindName("PageSelectionBar");
        RichTextBox editor = (RichTextBox)window.FindName("NotesBox");
        TextBlock indicator = (TextBlock)window.FindName("PageIndicator");
        Button Row(int i) => (Button)rows.Children[i];
        string Shown() => new System.Windows.Documents.TextRange(editor.Document.ContentStart, editor.Document.ContentEnd).Text.Trim();
        async Task OpenList()
        {
            if (!popup.IsOpen)
                ((Button)window.FindName("PageListButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(250);
        }

        await OpenList();
        Check(popup.IsOpen && rows.Children.Count == 8 && indicator.Text == "3 / 8", "the list shows 8 pages, page 3 open");
        await Held(0x11, () => Press(Row(1)));
        await Held(0x11, () => Press(Row(3)));
        Check(selection.Selected.SequenceEqual(new[] { original[1], original[3] }) && popup.IsOpen && indicator.Text == "3 / 8",
            "Ctrl+press on two pages selects them, turns to neither, the list stays open");
        Check(bar.IsVisible && Keyboard.FocusedElement == Row(3), "the bar shows, the keyboard is in the list");
        await Held(0x10, () => Press(Row(5)));
        Check(selection.Selected.SequenceEqual(original.Skip(3).Take(3)), "Shift+press: pages 4-6, from the last Ctrl+pressed");
        Check(Row(4).Background == SelectionLook.RowFill && Row(0).Background != SelectionLook.RowFill, "selected rows have the selected fill");
        RenderPopup((FrameworkElement)window.FindName("PageListPanel"), "select-05-pages");

        int boxes = selectBoxes.Count;
        PressKey(editor, Key.Delete);
        await Wait(200);
        Check(Pages().Count == 8 && selectBoxes.Count == boxes, "Delete in the editor stays the editor's: no page deleted, nothing asked");
        PressKey(Row(3), Key.Escape);
        Check(selection.Count == 0 && popup.IsOpen && window.IsVisible, "Esc clears the selection; the list and the window stay open");
        await Held(0x11, () => PressKey(Row(0), Key.A));
        Check(selection.Count == 8, "Ctrl+A selects every page");
        PressKey(Row(0), Key.Escape);
        MouseButton(Row(6), down: true);
        Check(selection.Count == 0, "a plain press selects nothing");
        MouseButton(Row(6), down: false);
        if (popup.IsOpen)
            Row(6).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Wait(250);
        Check(!popup.IsOpen && indicator.Text == "7 / 8" && Shown() == "Page text 7", "a plain click still turns to that page");

        // Pages 6-8 with the open page 7 among them: one question, then the page before (the last one left) shows.
        await OpenList();
        await Held(0x11, () => Press(Row(5)));
        await Held(0x10, () => Press(Row(7)));
        boxes = selectBoxes.Count;
        PressKey(Row(7), Key.Delete);
        await Wait(400);
        string box = selectBoxes.Skip(boxes).FirstOrDefault() ?? "";
        Check(selectBoxes.Count == boxes + 1 && box.Contains(Loc.Plural("Bulk.Delete.Pages", 3)) && box.Contains("6. Page text 6")
            && box.Contains("8. Page text 8") && !box.Contains(Loc.T("ProjectWindow.DeleteAllPagesNote")), "one question for the three: " + box);
        Check(Pages().SequenceEqual(original.Take(5)) && indicator.Text == "5 / 5" && Shown() == "Page text 5",
            "pages 6-8 gone; the open page went, so page 5 shows");
        Check(selection.Count == 0 && !bar.IsVisible, "the selection is gone with them");

        // The bar: Cancel in the question keeps everything; then pages 1 and 3 go, the open page stays open.
        await OpenList();
        await Held(0x11, () => Press(Row(0)));
        await Held(0x11, () => Press(Row(2)));
        answerCancel = true;
        boxes = selectBoxes.Count;
        Invoke(bar, "Delete_Click", null, null);
        answerCancel = false;
        await Wait(300);
        Check(selectBoxes.Count == boxes + 1 && Pages().Count == 5, "Cancel in the question deletes nothing");
        await OpenList();
        if (selection.Count != 2)
        {
            await Held(0x11, () => Press(Row(0)));
            await Held(0x11, () => Press(Row(2)));
        }
        Invoke(bar, "Delete_Click", null, null);
        await Wait(400);
        Check(Pages().SequenceEqual(new[] { original[1], original[3], original[4] }) && indicator.Text == "3 / 3" && Shown() == "Page text 5",
            "the bar's Delete: pages 1 and 3 gone, the open page stays open");

        // 🗑 on the open page: the same delete, its own question.
        popup.IsOpen = false;
        boxes = selectBoxes.Count;
        Invoke(window, "DeletePage_Click", null, null);
        await Wait(300);
        box = selectBoxes.Skip(boxes).FirstOrDefault() ?? "";
        Check(box.Contains(Loc.F("ProjectWindow.DeletePageConfirm", 3)) && Pages().Count == 2 && indicator.Text == "2 / 2" && Shown() == "Page text 4",
            "🗑 deletes the open page as before: " + box);

        // Every page: page 1 stays, emptied (a notebook keeps one page).
        await OpenList();
        await Held(0x11, () => PressKey(Row(0), Key.A));
        boxes = selectBoxes.Count;
        PressKey(Row(1), Key.Delete);
        await Wait(400);
        box = selectBoxes.Skip(boxes).FirstOrDefault() ?? "";
        Check(box.Contains(Loc.Plural("Bulk.Delete.Pages", 2)) && box.Contains(Loc.T("ProjectWindow.DeleteAllPagesNote")), "all selected: the question says one page stays: " + box);
        Check(Pages().Count == 1 && Pages()[0] == original[1] && indicator.Text == "1 / 1" && Shown().Length == 0,
            "all pages deleted: one empty page left, shown");
        window.Close();
        await Wait(300);
        Check(project.NotePages.Count == 1 && RichTextHelper.ToPlainText(project.NotePages[0].Body).Trim().Length == 0,
            "saved: the notebook has one empty page");
    }

    // ---- Day Plan -----------------------------------------------------------------

    static async Task DayPlanBatches(MainWindow main)
    {
        Console.WriteLine("Day Plan blocks (list and timeline)");
        Profile memory = AddMemory(main, "Plan memory", ProfileType.Project);
        TodoEntry todo = new TodoEntry { Id = Guid.NewGuid(), Text = "Linked to-do" };
        MemoryTodoService.TrySave(memory, new List<TodoEntry> { todo });
        DateTime day = DateTime.Today.AddDays(2);
        Guid a = Add(day, 9 * 60, 10 * 60, "Swim"), b = Add(day, 11 * 60, 12 * 60, "Read"), c = Add(day, 13 * 60, 14 * 60, "Call"),
            d = Add(day, 15 * 60, 16 * 60, "Walk");
        foreach (Guid id in new[] { a, c })
        {
            DayPlanStore.Change(id, x => x.RemindMinutesBefore = 10, out DayPlanBlock withReminder);
            DayPlanReminders.Set(withReminder);
            testReminderIds.Add(id);
        }
        DayPlanStore.Change(b, x => x.Todo = TodoLink.ForMemory(memory, todo.Id), out _);
        main.ShowDayPlan(day);
        await Wait(800);
        DayPlanView view = (DayPlanView)Content(main);
        ItemSelection<Guid> selection = Field<ItemSelection<Guid>>(view, "selection");
        Canvas surface = (Canvas)view.FindName("Surface");
        Border Card(Guid id) => surface.Children.OfType<Border>().First(x => x.Tag is DayPlanBlock blk && blk.Id == id);

        await Held(0x11, () => Press(Card(a)));
        await Held(0x10, () => Press(Card(c)));
        Check(selection.Selected.SequenceEqual(new[] { a, b, c }), "timeline: Ctrl+press then Shift+press, three blocks");
        Check(Block(a).StartMinute == 9 * 60, "pressing with Ctrl or Shift moved nothing");
        Check(Descendants(Card(b)).OfType<FrameworkElement>().Any(x => x.Tag as string == "SelectedBadge"), "a selected card has its check");
        Check(((FrameworkElement)view.FindName("SelectionBar")).IsVisible, "the header shows the selection bar");
        RenderWindow(main, "select-05-dayplan-timeline");
        ((RadioButton)view.FindName("ListButton")).IsChecked = true;
        await Wait(400);
        RenderWindow(main, "select-06-dayplan-list");
        Check(selection.Count == 3, "the selection carries over to the list view");
        Border walkRow = ((Panel)view.FindName("ListPanel")).Children.OfType<Border>().First(x => x.Tag is DayPlanBlock blk && blk.Id == d);
        walkRow.Focus();
        answerCancel = true;
        int asked = selectBoxes.Count;
        PressKey(walkRow, Key.Delete);
        answerCancel = false;
        string own = selectBoxes.Skip(asked).FirstOrDefault() ?? "";
        Check(own.Contains("Walk") && !own.Contains("Swim") && selection.Count == 3,
            "Tab onto a block that isn't selected: Delete asks about that one only: " + own);

        // reminders.json unreadable: the blocks with a reminder can't lose it, so they stay; the other goes.
        string remindersFile = AppPaths.Data("reminders.json");
        string good = File.ReadAllText(remindersFile);
        File.WriteAllText(remindersFile, "{ not json");
        int boxes = selectBoxes.Count;
        selection.DeleteSelected();
        File.WriteAllText(remindersFile, good);
        await Wait(300);
        Check(Block(a) != null && Block(c) != null && Block(b) == null, "a failure part-way: the two whose reminder couldn't go stay, the other went");
        string report = selectBoxes.Skip(boxes).Skip(1).FirstOrDefault() ?? "";
        Check(report.Contains(Loc.Plural("Bulk.Kept", 2)) && report.Contains("Swim") && report.Contains("Call") && !report.Contains("Read"),
            "and the ones kept are named: " + report);
        Check(MemoryTodoService.TryLoad(memory, out List<TodoEntry> todos) && todos.Any(t => t.Id == todo.Id), "a linked to-do is never touched");

        selection = Field<ItemSelection<Guid>>(view, "selection");
        selection.Click(a, ModifierKeys.Control);
        selection.Click(c, ModifierKeys.Control);
        selection.Click(d, ModifierKeys.Control);
        selection.DeleteSelected();
        await Wait(400);
        Check(Blocks(day).Count == 0, "the rest deleted together");
        CheckNoReminder(a, "the first one's reminder and its scheduled task are gone");
        CheckNoReminder(c, "the second one's too");
        RemoveMemory(main, memory);
        Invoke(main, "NavigateHome");
    }

    // ---- To-dos ---------------------------------------------------------------------

    static async Task TodoBatches(MainWindow main)
    {
        Console.WriteLine("Memory to-dos and Home's to-dos");
        Profile memory = AddMemory(main, "Todo memory", ProfileType.Project);
        List<TodoEntry> entries = Enumerable.Range(1, 4).Select(i => new TodoEntry { Id = Guid.NewGuid(), Text = "Todo " + i }).ToList();
        MemoryTodoService.TrySave(memory, entries);
        memory.TodoCollapsed = false; // open, so the rows show
        Guid linkedBlock = Add(DateTime.Today, 18 * 60, 19 * 60, "From a to-do");
        DayPlanStore.Change(linkedBlock, x => x.Todo = TodoLink.ForMemory(memory, entries[1].Id), out _);
        Invoke(main, "NavigateTo", memory);
        await Wait(800);
        MemoryTodoPanel panel = (MemoryTodoPanel)((FrameworkElement)Content(main)).FindName("TodoPanel");
        ItemSelection<TodoEntry> selection = Field<ItemSelection<TodoEntry>>(panel, "selection");
        StackPanel rows = (StackPanel)panel.FindName("ItemList");
        await Held(0x11, () => Press(Descendants(rows.Children[0]).OfType<CheckBox>().First()));
        await Held(0x10, () => Press(Descendants(rows.Children[2]).OfType<CheckBox>().First()));
        List<TodoEntry> live = Field<List<TodoEntry>>(panel, "todos");
        Check(selection.Count == 3 && !live.Any(t => t.IsDone), "Ctrl then Shift on the checkboxes: three selected, none ticked");
        RenderWindow(main, "select-07-memory-todos");
        selection.DeleteSelected();
        await Wait(300);
        MemoryTodoService.TryLoad(memory, out List<TodoEntry> left);
        Check(left.Select(t => t.Text).SequenceEqual(new[] { "Todo 4" }), "deleted in one save (" + string.Join(", ", left.Select(t => t.Text)) + ")");
        Check(Block(linkedBlock) != null, "a block made from one keeps its time (shows its to-do as gone, as after one delete)");
        DayPlanStore.Delete(linkedBlock);
        RemoveMemory(main, memory);

        Invoke(main, "NavigateHome");
        await Wait(500);
        QuickTodo[] quick = Enumerable.Range(1, 4).Select(i => QuickTodoService.Add("Quick " + i, DateTime.Today)).ToArray();
        HomeView home = (HomeView)Content(main);
        QuickTodoListControl list = (QuickTodoListControl)home.FindName("QuickTodos");
        list.Refresh();
        await Wait(300);
        ItemSelection<Guid> quickSelection = Field<ItemSelection<Guid>>(list, "selection");
        quickSelection.Click(quick[0].Id, ModifierKeys.Control);
        quickSelection.Click(quick[2].Id, ModifierKeys.Control);
        RenderWindow(main, "select-08-home-todos");

        // quicktodos.json can't be read: none go, the list says so.
        string file = AppPaths.Data("quicktodos.json");
        string good = File.ReadAllText(file);
        File.WriteAllText(file, "[ broken");
        quickSelection.DeleteSelected();
        File.WriteAllText(file, good);
        Check(QuickTodoService.LoadTodos().Count(t => t.Text.StartsWith("Quick ")) == 4 && ((TextBlock)list.FindName("StatusText")).IsVisible,
            "quicktodos.json unreadable: none deleted, the list says so");
        list.Refresh();
        await Wait(300);
        quickSelection.ClearSelection();
        quickSelection.Click(quick[0].Id, ModifierKeys.Control);
        quickSelection.Click(quick[2].Id, ModifierKeys.Control);
        quickSelection.DeleteSelected();
        await Wait(300);
        List<string> quickLeft = QuickTodoService.LoadTodos().Select(t => t.Text).Where(t => t.StartsWith("Quick ")).ToList();
        Check(quickLeft.SequenceEqual(new[] { "Quick 2", "Quick 4" }), "two deleted in one write (" + string.Join(", ", quickLeft) + ")");
        QuickTodoService.Delete(new[] { quick[1].Id, quick[3].Id });
    }

    // ---- Reminders --------------------------------------------------------------------

    static async Task ReminderBatches(MainWindow main)
    {
        Console.WriteLine("Reminders window");
        List<Guid> ids = new List<Guid>();
        for (int i = 1; i <= 3; i++)
        {
            Reminder reminder = new Reminder
            {
                Id = Guid.NewGuid(), Message = "Reminder " + i, NextFireTime = DateTime.Now.AddDays(3).AddMinutes(i), RepeatType = RepeatType.Once
            };
            ReminderManager.AddReminder(reminder);
            testReminderIds.Add(reminder.Id);
            ids.Add(reminder.Id);
        }
        RemindersWindow.ShowOrFocus();
        await Wait(600);
        RemindersWindow window = Application.Current.Windows.OfType<RemindersWindow>().Single();
        ItemSelection<Guid> selection = Field<ItemSelection<Guid>>(window, "selection");
        selection.Click(ids[0], ModifierKeys.Control);
        selection.Click(ids[2], ModifierKeys.Shift);
        Check(selection.Count == 3, "Ctrl then Shift: all three");
        RenderWindow(window, "select-09-reminders");
        PressEscape(window);
        Check(window.IsVisible && selection.Count == 0, "Esc clears the selection first, the window stays");
        selection.Click(ids[0], ModifierKeys.Control);
        selection.Click(ids[1], ModifierKeys.Control);
        selection.DeleteSelected();
        await Wait(300);
        CheckNoReminder(ids[0], "the first reminder and its scheduled task are gone");
        CheckNoReminder(ids[1], "the second one's too");
        Check(ReminderManager.LoadReminders().Any(r => r.Id == ids[2]), "the third is kept");
        window.Close();
    }

    // ---- Three languages ------------------------------------------------------------

    static async Task Languages(MainWindow main)
    {
        Console.WriteLine("Three languages");
        Check(Loc.PluralForm(Loc.SerbianLatin, 1) == "One" && Loc.PluralForm(Loc.SerbianLatin, 3) == "Few"
              && Loc.PluralForm(Loc.SerbianLatin, 7) == "Other", "(Serbian forms: 1 One, 3 Few, 7 Other)");
        List<Profile> memories = Enumerable.Range(1, 3).Select(i => AddMemory(main, "Mem " + i, ProfileType.Project)).ToList();
        foreach (string code in Codes)
        {
            Loc.Switch(code);
            await Wait(300);
            string tag = code == Loc.SerbianLatin ? "sr" : code;
            string[] expected = code == Loc.English ? new[] { "Delete 1 task?", "Delete 2 tasks?", "Delete 5 tasks?", "Delete 21 tasks?" }
                : code == Loc.Hungarian ? new[] { "Törölsz 1 feladatot?", "Törölsz 2 feladatot?", "Törölsz 5 feladatot?", "Törölsz 21 feladatot?" }
                : new[] { "Obrisati 1 zadatak?", "Obrisati 2 zadatka?", "Obrisati 5 zadataka?", "Obrisati 21 zadatak?" };
            int[] counts = { 1, 2, 5, 21 };
            for (int i = 0; i < counts.Length; i++)
            {
                string text = Loc.Plural("Bulk.Delete.Tasks", counts[i]);
                Check(text == expected[i], tag + ": " + counts[i] + " → " + text);
            }
            string[] pagesExpected = code == Loc.English ? new[] { "Delete 1 page?", "Delete 2 pages?", "Delete 5 pages?", "Delete 21 pages?" }
                : code == Loc.Hungarian ? new[] { "Törölsz 1 oldalt?", "Törölsz 2 oldalt?", "Törölsz 5 oldalt?", "Törölsz 21 oldalt?" }
                : new[] { "Obrisati 1 stranicu?", "Obrisati 2 stranice?", "Obrisati 5 stranica?", "Obrisati 21 stranicu?" };
            for (int i = 0; i < counts.Length; i++)
            {
                string text = Loc.Plural("Bulk.Delete.Pages", counts[i]);
                Check(text == pagesExpected[i], tag + ": " + counts[i] + " → " + text);
            }
            ItemSelection<Profile> selection = Field<ItemSelection<Profile>>(main, "memorySelection");
            selection.Click(memories[0], ModifierKeys.Control);
            selection.Click(memories[2], ModifierKeys.Shift);
            RenderWindow(main, "select-10-sidebar-" + tag);
            answerCancel = true;
            int boxes = selectBoxes.Count;
            selection.DeleteSelected();
            answerCancel = false;
            string box = selectBoxes.Skip(boxes).FirstOrDefault() ?? "";
            Check(box.Contains(Loc.Plural("Bulk.Delete.Memories", 3)) && box.EndsWith("[" + Loc.T("Common.Delete") + "][" + Loc.T("Common.Cancel") + "]"),
                tag + ": the question and its buttons in the app language: " + box);
            selection.ClearSelection();
        }
        Loc.Switch(Loc.English);
        foreach (Profile p in memories)
            Profiles(main).Remove(p);
        ProfileManager.TrySaveProfiles(Profiles(main));
        Invoke(main, "NavigateHome");
    }

    // ---- Helpers ----------------------------------------------------------------------

    static Profile AddMemory(MainWindow main, string name, ProfileType type)
    {
        List<Profile> profiles = Profiles(main);
        Profile memory = new Profile { Id = Guid.NewGuid(), Name = name, Type = type, Color = "#3498DB", Order = profiles.Count };
        profiles.Add(memory);
        ProfileManager.TrySaveProfiles(profiles);
        Directory.CreateDirectory(MemoryFiles.Folder(memory));
        Invoke(main, "BuildNav");
        return memory;
    }

    static void RemoveMemory(MainWindow main, Profile memory)
    {
        Invoke(main, "NavigateHome");
        Profiles(main).Remove(memory);
        ProfileManager.TrySaveProfiles(Profiles(main));
        MemoryFiles.DeleteFolder(memory);
        Invoke(main, "BuildNav");
    }

    static Border NavItem(MainWindow main, Profile profile) =>
        ((Panel)main.FindName("NavPanel")).Children.OfType<Border>().First(b => b.Name == "Nav" + profile.Id.ToString("N"));

    static string StoreFile(string folder, string name)
    {
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, Guid.NewGuid().ToString("N") + Path.GetExtension(name));
        File.WriteAllText(path, "harness");
        return path;
    }

    // A left press and release on `target`, routed as WPF routes a real click: PreviewMouseDown down the tree, then
    // MouseDown back up (each element turns them into its own PreviewMouseLeftButtonDown / MouseLeftButtonDown).
    static void Press(UIElement target)
    {
        MouseButton(target, down: true);
        MouseButton(target, down: false);
    }

    static void MouseButton(UIElement target, bool down)
    {
        MouseButtonEventArgs args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, System.Windows.Input.MouseButton.Left)
        {
            RoutedEvent = down ? Mouse.PreviewMouseDownEvent : Mouse.PreviewMouseUpEvent
        };
        target.RaiseEvent(args);
        if (!args.Handled)
            target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, System.Windows.Input.MouseButton.Left)
            {
                RoutedEvent = down ? Mouse.MouseDownEvent : Mouse.MouseUpEvent
            });
    }

    // A real key (Ctrl 0x11, Shift 0x10) held down while `act` runs, so Keyboard.Modifiers sees it.
    static async Task Held(byte key, Action act)
    {
        keybd_event(key, 0, 0, UIntPtr.Zero);
        await Wait(150);
        try
        {
            act();
        }
        finally
        {
            keybd_event(key, 0, 2, UIntPtr.Zero);
            await Wait(150);
        }
    }

    // Logs every message box (caption | text | [buttons]) and answers it: Cancel (or No) when asked, else Yes or OK.
    static void StartSelectWatcher()
    {
        uint self = (uint)Environment.ProcessId;
        Thread watcher = new Thread(() =>
        {
            while (true)
            {
                EnumWindows((hwnd, _) =>
                {
                    GetWindowThreadProcessId(hwnd, out uint pid);
                    StringBuilder cls = new StringBuilder(64);
                    GetClassName(hwnd, cls, 64);
                    if (pid != self || cls.ToString() != "#32770" || !IsWindowVisible(hwnd))
                        return true;
                    StringBuilder title = new StringBuilder(256), text = new StringBuilder(4096);
                    GetWindowText(hwnd, title, 256);
                    GetWindowText(GetDlgItem(hwnd, 0xFFFF), text, 4096);
                    string buttons = "";
                    foreach (int id in new[] { 6, 1, 7, 2 })
                    {
                        IntPtr button = GetDlgItem(hwnd, id);
                        if (button == IntPtr.Zero) continue;
                        StringBuilder label = new StringBuilder(64);
                        GetWindowText(button, label, 64);
                        buttons += "[" + label + "]";
                    }
                    int answer = answerCancel ? (GetDlgItem(hwnd, 2) != IntPtr.Zero ? 2 : 7)
                        : new[] { 6, 1, 2 }.First(i => GetDlgItem(hwnd, i) != IntPtr.Zero || i == 2);
                    lock (selectBoxes)
                        selectBoxes.Add(title + " | " + text.ToString().Replace("\r", "").Replace("\n", " / ") + " | " + buttons);
                    PostMessage(GetDlgItem(hwnd, answer), 0x00F5, IntPtr.Zero, IntPtr.Zero); // BM_CLICK
                    Thread.Sleep(200);
                    return true;
                }, IntPtr.Zero);
                Thread.Sleep(100);
            }
        }) { IsBackground = true };
        watcher.Start();
    }
}
