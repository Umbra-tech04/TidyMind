using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using TidyMind;

// Harness notebook <data> <shots>: a memory's own notebook. Old data read and shown without a write, the button on the
// memory's page, pages added, edited and deleted (one, several, all), formatted text kept, undo, search, a failed save
// leaving the data folder as it was, an unreadable notebook, and the notebook going with the memory: exports, backup
// and restore, transfer, rename and delete. One screenshot of the button and the window.
static partial class Program
{
    static async Task NotebookScenarios(MainWindow main)
    {
        await Wait(1200);
        GuidedTour.Current?.Stop(TourEnd.Skipped);
        main.Topmost = true;
        main.Width = 1240;
        main.Height = 820;
        StartSelectWatcher();
        await Foreground(main);

        try
        {
            await NotebookOldData(main);
            Profile home = await NotebookPages(main);
            await NotebookInCollection(main);
            await NotebookFailedSave(main, home);
            await NotebookGoesAlong(main, home);
        }
        finally
        {
            Console.WriteLine("message boxes: " + selectBoxes.Count);
            lock (selectBoxes)
                foreach (string box in selectBoxes)
                    Console.WriteLine("  " + box);
        }
    }

    // ---- Old data -----------------------------------------------------------------------------------

    // Memories from before the notebook: their pages, the notebook opened and closed, change nothing on disk. A notebook
    // from a newer version keeps the fields this one doesn't know.
    static async Task NotebookOldData(MainWindow main)
    {
        Console.WriteLine("Old data: read and shown, nothing written");
        Profile projects = AddMemory(main, "Old projects", ProfileType.Project);
        Profile things = AddMemory(main, "Old things", ProfileType.Collection);
        Project old = new Project
        {
            Id = Guid.NewGuid(), Name = "Old project", Tasks = new List<TaskItem> { new TaskItem { Title = "Old task" } },
            Notes = "The single note from before pages"
        };
        File.WriteAllText(MemoryFiles.Items(projects), JsonSerializer.Serialize(new List<Project> { old }));
        File.WriteAllText(MemoryFiles.Items(things), JsonSerializer.Serialize(new List<Entity> { new Entity { Id = Guid.NewGuid(), Name = "Old lamp" } }));
        File.WriteAllText(MemoryFiles.Todos(projects), JsonSerializer.Serialize(new List<TodoEntry> { new TodoEntry { Id = Guid.NewGuid(), Text = "Old to-do" } }));
        // Once shown, so what the app does to any memory on its first showing (its tabs) is done before the snapshot.
        foreach (Profile memory in new[] { projects, things })
        {
            Invoke(main, "NavigateTo", memory);
            await Wait(500);
        }
        Invoke(main, "NavigateHome");
        await Wait(300);

        Dictionary<string, string> before = FolderSnapshot();
        foreach (Profile memory in new[] { projects, things })
        {
            Invoke(main, "NavigateTo", memory);
            await Wait(500);
            NotebookButton button = NotebookButtonOf(main);
            Check(button != null && button.IsVisible && !CountOf(button).IsVisible, memory.Name + ": the Notebook button, no count");
            NotebookWindow window = await OpenNotebook(main, memory);
            Check(PagesOf(window).Count == 1 && BodiesOf(window)[0] == "", memory.Name + ": the notebook opens on one empty page");
            PressKey(window.Notebook.Editor, Key.Escape);
            await Wait(300);
            Check(!window.IsVisible, memory.Name + ": Esc closes it");
        }
        Invoke(main, "NavigateHome");
        await Wait(300);
        Dictionary<string, string> after = FolderSnapshot();
        Check(SameSnapshot(before, after), "the data folder is byte for byte as it was" + SnapshotDiff(before, after));
        Check(!File.Exists(MemoryFiles.Notebook(projects)) && !File.Exists(MemoryFiles.Notebook(things)), "no notebook file was made");
        Check(ProjectStore.Load(projects).Single().NotePages == null, "the old project still has its single note only (no pages written)");

        // A notebook written by a newer TidyMind: what this version doesn't know is kept when it saves.
        File.WriteAllText(MemoryFiles.Notebook(things),
            "{\"Pages\":[{\"Body\":\"From a newer version\",\"Pinned\":true}],\"Color\":\"#FF8800\"}");
        NotebookWindow newer = await OpenNotebook(main, things);
        Check(PlainOf(newer).Contains("From a newer version"), "a newer version's notebook opens");
        SetPage(newer, new Paragraph(new Run("Edited here")));
        newer.Close();
        await Wait(200);
        JsonObject stored = JsonNode.Parse(File.ReadAllText(MemoryFiles.Notebook(things))).AsObject();
        Check(stored["Color"]?.ToString() == "#FF8800" && stored["Pages"][0]["Pinned"]?.GetValue<bool>() == true
              && RichTextHelper.ToPlainText(stored["Pages"][0]["Body"].ToString()).Contains("Edited here"),
            "saved: its unknown fields kept, the page edited: " + File.ReadAllText(MemoryFiles.Notebook(things)).Substring(0, 60));

        // An older version never reads notebook.json; its own files hold nothing of the notebook, and saving them leaves it.
        byte[] notebook = File.ReadAllBytes(MemoryFiles.Notebook(things));
        Check(!File.ReadAllText(MemoryFiles.Items(things)).Contains("Edited here") && !File.ReadAllText(AppPaths.Data("profiles.json")).Contains("Edited here"),
            "the notebook is only in its own file");
        Check(EntityStore.TrySave(things, EntityStore.Load(things)) && File.ReadAllBytes(MemoryFiles.Notebook(things)).SequenceEqual(notebook),
            "saving the memory's items leaves the notebook file as it was");
        RemoveMemory(main, projects);
        RemoveMemory(main, things);
    }

    // ---- Pages ----------------------------------------------------------------------------------------

    static async Task<Profile> NotebookPages(MainWindow main)
    {
        Console.WriteLine("Pages: added, formatted, edited, deleted; undo; search; the button");
        Profile memory = AddMemory(main, "Household", ProfileType.Project);
        Invoke(main, "NavigateTo", memory);
        await Wait(600);
        UndoService.EndAll();
        NotebookButton button = NotebookButtonOf(main);
        ProjectView view = (ProjectView)Content(main);
        FrameworkElement todos = (FrameworkElement)view.FindName("TodoPanel");
        Point at = button.TranslatePoint(new Point(0, 0), view), todoAt = todos.TranslatePoint(new Point(0, 0), view);
        Check(at.X >= todoAt.X + todos.ActualWidth && Math.Abs(at.Y - todoAt.Y) < 1, "the button sits beside the to-do list, at its top");
        Check((string)((Button)button.FindName("OpenButton")).ToolTip == Loc.T("Notebook.Tip"), "its tooltip: " + Loc.T("Notebook.Tip"));

        NotebookWindow window = await OpenFromButton(button);
        Check(window.Owner == main && window.Notebook.Editor.IsVisible, "the button opens the notebook over the main window");
        Check(window.Title == Loc.F("Notebook.WindowTitle", memory.Name) && ((TextBlock)window.FindName("TitleText")).Text == memory.Name,
            "the window is titled with the memory's name: " + window.Title);
        Check(IndicatorOf(window) == "1 / 1", "one page");

        // Three pages, the first formatted; a fourth turned to and left blank isn't kept.
        SetPage(window, new Paragraph(new Run("Plain, ")) { Inlines = { new Bold(new Run("bold")), new Run(" and "), new Italic(new Run("italic")) } },
            new Paragraph(new Run("Wi-Fi: harness-ősz-čćšžđ")));
        Invoke(window.Notebook, "TurnForward");
        SetPage(window, new Paragraph(new Run("Second page: garage codes")));
        Invoke(window.Notebook, "TurnForward");
        SetPage(window, new Paragraph(new Run("Third page")));
        Invoke(window.Notebook, "TurnForward");
        Check(IndicatorOf(window) == "4 / 4", "▶ on a written last page starts page 4");
        window.Close();
        await Wait(300);
        MemoryNotebook stored = Stored(memory);
        Check(stored.Pages.Count == 3, "saved: 3 pages (the blank 4th dropped)");
        Check(stored.Pages[0].Body.StartsWith("<Section") && stored.Pages[0].Body.Contains("FontWeight=\"Bold\"") && stored.Pages[0].Body.Contains("FontStyle=\"Italic\""),
            "page 1 stored formatted (bold, italic)");
        Check(CountOf(button).IsVisible && CountOf(button).Text == "3" && ((string)((Button)button.FindName("OpenButton")).ToolTip).Contains(Loc.Plural("Notebook.Pages", 3)),
            "the button shows 3 pages");

        // The page's undo: the notebook as it was before the window (none), and back.
        byte[] three = File.ReadAllBytes(MemoryFiles.Notebook(memory));
        Check(main.Undo.Undo() && !File.Exists(MemoryFiles.Notebook(memory)) && !CountOf(button).IsVisible, "Ctrl+Z on the page: no notebook again, no count");
        Check(main.Undo.Redo() && Same(Bytes(MemoryFiles.Notebook(memory)), three) && CountOf(button).Text == "3", "Ctrl+Y: back byte for byte");

        // Formatted text survives a reopening, and an untouched notebook isn't written again.
        NotebookEditor.OpenAtPage(memory.Id, 0);
        window = await OpenNotebook(main, memory);
        List<Run> runs = AllRuns(window.Notebook.Editor.Document);
        Check(runs.Any(r => r.Text == "bold" && r.FontWeight == FontWeights.Bold) && runs.Any(r => r.Text == "italic" && r.FontStyle == FontStyles.Italic)
              && PlainOf(window).Contains("harness-ősz-čćšžđ"), "reopened: bold, italic and the accents as written");
        DateTime written = File.GetLastWriteTimeUtc(MemoryFiles.Notebook(memory));
        await Wait(50);
        window.Close();
        await Wait(200);
        Check(Same(Bytes(MemoryFiles.Notebook(memory)), three) && File.GetLastWriteTimeUtc(MemoryFiles.Notebook(memory)) == written,
            "closed untouched: the file isn't written");

        // One page edited; the others stay as stored.
        window = await OpenNotebook(main, memory);
        Invoke(window.Notebook, "FlipTo", 1);
        SetPage(window, new Paragraph(new Run("Second page: garage codes 1234")));
        window.Close();
        await Wait(200);
        MemoryNotebook edited = Stored(memory);
        Check(edited.Pages[0].Body == stored.Pages[0].Body && edited.Pages[2].Body == stored.Pages[2].Body
              && RichTextHelper.ToPlainText(edited.Pages[1].Body).Contains("1234"), "page 2 edited, pages 1 and 3 as they were");

        // One page deleted (it has text: asked first).
        window = await OpenNotebook(main, memory);
        int boxes = selectBoxes.Count;
        Invoke(window.Notebook, "DeletePages", (IReadOnlyList<NotePage>)new[] { PagesOf(window)[1] });
        await Wait(200);
        Check(selectBoxes.Count == boxes + 1 && selectBoxes[boxes].Contains(Loc.F("ProjectWindow.DeletePageConfirm", 2)) && PagesOf(window).Count == 2,
            "page 2 deleted after the question");
        Check(UndoHost.Of(window).Shown == Loc.F("Undo.Deleted", "2. Second page: garage codes 1234"), "the window's toast: " + UndoHost.Of(window).Shown);
        Check(UndoHost.Of(window).Undo() && PagesOf(window).Count == 3 && UndoHost.Of(window).Redo() && PagesOf(window).Count == 2,
            "Ctrl+Z in the window brings it back, Ctrl+Y takes it again");
        window.Close();
        await Wait(200);
        Check(Stored(memory).Pages.Count == 2 && RichTextHelper.ToPlainText(Stored(memory).Pages[1].Body) == "Third page", "saved: 2 pages");
        byte[] two = File.ReadAllBytes(MemoryFiles.Notebook(memory));

        // Several at once from the page list, then every page.
        window = await OpenFromButton(button);
        Invoke(window.Notebook, "FlipTo", 1);
        foreach (string text in new[] { "Fourth", "Fifth", "Sixth" })
        {
            Invoke(window.Notebook, "TurnForward");
            SetPage(window, new Paragraph(new Run(text)));
        }
        Invoke(window.Notebook, "PageList_Click", null, null);
        await Wait(300);
        ItemSelection<NotePage> selection = Field<ItemSelection<NotePage>>(window.Notebook, "pageSelection");
        List<NotePage> five = PagesOf(window).ToList();
        selection.Click(five[1], ModifierKeys.Control);
        selection.Click(five[3], ModifierKeys.Control);
        Check(selection.Count == 2 && ((FrameworkElement)window.Notebook.FindName("PageSelectionBar")).IsVisible, "two pages selected, the bar shows");
        boxes = selectBoxes.Count;
        selection.DeleteSelected();
        await Wait(300);
        Check(selectBoxes.Count == boxes + 1 && selectBoxes[boxes].Contains(Loc.Plural("Bulk.Delete.Pages", 2)), "one question for both");
        Check(PagesOf(window).SequenceEqual(new[] { five[0], five[2], five[4] }), "pages 2 and 4 deleted");
        Check(UndoHost.Of(window).Undo() && PagesOf(window).SequenceEqual(five), "undone in the window: all five back in place");
        Invoke(window.Notebook, "PageList_Click", null, null);
        await Wait(300);
        selection.SelectAll();
        boxes = selectBoxes.Count;
        selection.DeleteSelected();
        await Wait(300);
        Check(selectBoxes.Count == boxes + 1 && selectBoxes[boxes].Contains(Loc.T("ProjectWindow.DeleteAllPagesNote")), "all selected: the question says page 1 stays");
        Check(PagesOf(window).Count == 1 && BodiesOf(window)[0] == "" && IndicatorOf(window) == "1 / 1", "every page deleted: page 1 stays, emptied");
        window.Close();
        await Wait(200);
        Check(!File.Exists(MemoryFiles.Notebook(memory)) && !CountOf(button).IsVisible, "saved empty: no file, no count");
        Check(main.Undo.Undo() && Same(Bytes(MemoryFiles.Notebook(memory)), two) && CountOf(button).Text == "2", "Ctrl+Z on the page: the two pages back byte for byte");

        // Shown again later: the count from the file.
        Invoke(main, "NavigateHome");
        await Wait(300);
        Invoke(main, "NavigateTo", memory);
        await Wait(500);
        Check(CountOf(NotebookButtonOf(main)).Text == "2", "the memory shown again: 2 pages");

        // Global search finds a page by its text and says where it is.
        List<SearchResult> index = SearchIndexBuilder.Build(Profiles(main));
        SearchResult hit = SearchIndexBuilder.Filter(index, "third pag", 20).FirstOrDefault(r => r.Kind == SearchResultKind.MemoryNote);
        Check(hit != null && hit.ItemId == memory.Id && hit.Page == 1 && hit.Location == memory.Name + "  →  " + Loc.T("Notebook.Title") + "  →  " + Loc.F("Search.Page", 2),
            "search finds the notebook page: " + hit?.Location);
        Check(SearchIndexBuilder.Filter(index, "harness-osz-ccszd", 20).Any(r => r.Kind == SearchResultKind.MemoryNote), "accent-insensitive, as everywhere");

        // The screenshot: the button with its count, and the notebook over the page.
        NotebookEditor.OpenAtPage(memory.Id, 0);
        window = await OpenNotebook(main, memory);
        // Below the page's header, so the button (beside the to-do list) shows above it.
        window.Height = 560;
        window.Left = main.Left + main.ActualWidth - window.Width - 60;
        window.Top = main.Top + 245;
        await Foreground(window);
        await Wait(500);
        Shot(main, "notebook-01-button-and-window");
        window.Close();
        await Wait(200);
        return memory;
    }

    // A collection's notebook: the button in the toolbar, the file in its own folder; an unreadable items file leaves it.
    static async Task NotebookInCollection(MainWindow main)
    {
        Console.WriteLine("A collection's notebook");
        Profile garage = AddMemory(main, "Garage", ProfileType.Collection);
        Invoke(main, "NavigateTo", garage);
        await Wait(600);
        NotebookButton button = NotebookButtonOf(main);
        Check(button.IsVisible && ((FrameworkElement)((FrameworkElement)Content(main)).FindName("Toolbar")).IsAncestorOf(button), "the button is in the toolbar");
        NotebookWindow window = await OpenFromButton(button);
        SetPage(window, new Paragraph(new Run("Tyre pressures: 2.4 front, 2.6 back")));
        window.Close();
        await Wait(200);
        Check(File.Exists(Path.Combine(MemoryFiles.Folder(garage), "notebook.json")) && CountOf(button).Text == "1", "saved in the collection's folder, 1 page");
        Check(main.Undo.Undo() && !File.Exists(MemoryFiles.Notebook(garage)) && main.Undo.Redo() && File.Exists(MemoryFiles.Notebook(garage)),
            "undone and redone on the collection's page");

        File.WriteAllText(MemoryFiles.Items(garage), "{ not json");
        Invoke(main, "NavigateHome");
        await Wait(200);
        Invoke(main, "NavigateTo", garage);
        await Wait(500);
        button = NotebookButtonOf(main);
        Check(button.IsVisible && CountOf(button).Text == "1" && !((FrameworkElement)((FrameworkElement)Content(main)).FindName("SearchBox")).IsVisible,
            "items unreadable: the search and Add go, the notebook stays");
        File.WriteAllText(MemoryFiles.Items(garage), "[]");
        Invoke(main, "NavigateHome");
        await Wait(200);
    }

    // ---- Failures --------------------------------------------------------------------------------------

    // The notebook file can't be replaced (held open by another program): the data folder stays as it was, the window
    // stays open unless the changes are given up. An unreadable notebook is said and never saved over.
    static async Task NotebookFailedSave(MainWindow main, Profile memory)
    {
        Console.WriteLine("A failed save, an unreadable notebook");
        Invoke(main, "NavigateTo", memory);
        await Wait(500);
        Dictionary<string, string> before = FolderSnapshot();
        NotebookWindow window = await OpenNotebook(main, memory);
        SetPage(window, new Paragraph(new Run("This edit can't be saved")));
        using (FileStream held = new FileStream(MemoryFiles.Notebook(memory), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            answerCancel = true;
            int boxes = selectBoxes.Count;
            window.Close();
            answerCancel = false;
            await Wait(300);
            string box = selectBoxes.Skip(boxes).FirstOrDefault() ?? "";
            Check(box.Contains(Loc.T("Notebook.Title")) && box.Contains(MemoryFiles.Notebook(memory)), "the save failed and that is said: " + box);
            Check(window.IsVisible && PlainOf(window).Contains("This edit can't be saved"), "No: the window stays open with the edit");
            boxes = selectBoxes.Count;
            window.Close();
            await Wait(300);
            Check(selectBoxes.Count == boxes + 1 && !window.IsVisible, "Yes: closed, the changes given up");
        }
        Dictionary<string, string> after = FolderSnapshot();
        Check(SameSnapshot(before, after), "the data folder is byte for byte as it was (no temp file left)" + SnapshotDiff(before, after));
        Check(!main.Undo.Undo(), "no undo step was recorded for it");

        byte[] good = File.ReadAllBytes(MemoryFiles.Notebook(memory));
        File.WriteAllText(MemoryFiles.Notebook(memory), "{ \"Pages\": [ damaged");
        byte[] damaged = File.ReadAllBytes(MemoryFiles.Notebook(memory));
        int count = selectBoxes.Count;
        NotebookWindow opened = NotebookWindow.Open(main, memory);
        await Wait(200);
        Check(opened == null && selectBoxes.Count == count + 1 && selectBoxes[count].Contains(MemoryFiles.Notebook(memory)),
            "unreadable: said, and the notebook doesn't open");
        NotebookButton button = NotebookButtonOf(main);
        button.Refresh();
        Check(Same(Bytes(MemoryFiles.Notebook(memory)), damaged) && !CountOf(button).IsVisible, "the file is left as it is; no count");
        File.WriteAllBytes(MemoryFiles.Notebook(memory), good);
        button.Refresh();
    }

    // ---- Where the memory goes ----------------------------------------------------------------------

    static async Task NotebookGoesAlong(MainWindow main, Profile memory)
    {
        Console.WriteLine("Exports, Export all, backup and restore, transfer, rename, delete");
        Invoke(main, "NavigateHome");
        await Wait(300);
        memory = Profiles(main).First(p => p.Id == memory.Id); // the list as the main window holds it now
        string folder = Path.Combine(Path.GetDirectoryName(AppPaths.DataFolder), "notebook-out");
        Directory.CreateDirectory(folder);
        Profile garage = Profiles(main).First(p => p.Name == "Garage");

        // The memory's export and Export all, in the layout of a project's notes.
        string docx = Path.Combine(folder, "household.docx"), xlsx = Path.Combine(folder, "household.xlsx"), pdf = Path.Combine(folder, "household.pdf");
        ExportService.ExportMemory(memory, docx, ExportFormat.Docx);
        ExportService.ExportMemory(memory, xlsx, ExportFormat.Excel);
        ExportService.ExportMemory(memory, pdf, ExportFormat.Pdf);
        string word = DocxText(docx);
        Check(word.Contains(Loc.T("Notebook.Title")) && word.Contains("Wi-Fi: harness-ősz-čćšžđ") && word.Contains("Third page"), "Word: the notebook's pages");
        string excel = XlsxText(xlsx);
        Check(excel.Contains("Wi-Fi: harness-ősz-čćšžđ") && excel.Contains("Third page"), "Excel: the pages on the text sheet");
        Check(new FileInfo(pdf).Length > 1000, "PDF written");
        string garageDocx = Path.Combine(folder, "garage.docx");
        ExportService.ExportMemory(garage, garageDocx, ExportFormat.Docx);
        Check(DocxText(garageDocx).Contains("Tyre pressures"), "a collection's export has its notebook");
        string all = Path.Combine(folder, "all.docx");
        ExportService.ExportAll(Profiles(main), all, ExportFormat.Docx);
        string allText = DocxText(all);
        Check(allText.Contains("Third page") && allText.Contains("Tyre pressures"), "Export all: both memories' notebooks");
        string allXlsx = Path.Combine(folder, "all.xlsx");
        ExportService.ExportAll(Profiles(main), allXlsx, ExportFormat.Excel);
        Check(XlsxText(allXlsx).Contains("Tyre pressures"), "Export all to Excel too");

        // Backup and restore: the notebook goes in and comes back; a backup with a damaged notebook isn't restored.
        string zip = Path.Combine(folder, "backup.zip");
        DataBackup.Create(zip);
        string entry = "Memories/" + memory.Id.ToString("N") + "/notebook.json";
        byte[] saved = File.ReadAllBytes(MemoryFiles.Notebook(memory));
        using (ZipArchive archive = ZipFile.OpenRead(zip))
            Check(archive.GetEntry(entry) != null, "the backup has " + entry);
        MemoryNotebookStore.TrySave(memory, new MemoryNotebook { Pages = new List<NotePage> { new NotePage { Body = "Changed after the backup" } } });
        DataBackup.Restore(zip, out _);
        Check(Same(Bytes(MemoryFiles.Notebook(memory)), saved), "restored: the notebook byte for byte as backed up");
        string damaged = Path.Combine(folder, "damaged.zip");
        File.Copy(zip, damaged, true);
        using (ZipArchive archive = ZipFile.Open(damaged, ZipArchiveMode.Update))
        {
            archive.GetEntry(entry).Delete();
            using (StreamWriter writer = new StreamWriter(archive.CreateEntry(entry).Open()))
                writer.Write("{ damaged");
        }
        Dictionary<string, string> beforeRestore = FolderSnapshot();
        bool refused = false;
        try
        {
            DataBackup.Restore(damaged, out _);
        }
        catch (InvalidDataException e)
        {
            refused = e.Message.Contains("notebook.json");
        }
        Check(refused && SameSnapshot(beforeRestore, FolderSnapshot()), "a backup with an unreadable notebook is refused, nothing changed");

        // Transfer: a collection takes its notebook along (format 4); one without a notebook stays format 1.
        string package = Path.Combine(folder, "garage" + TransferPackage.Extension);
        EntityStore.TryLoad(garage, out List<Entity> items, out _);
        MemoryNotebookStore.TryLoad(garage, out MemoryNotebook garageNotebook, out _);
        TransferPackage.ExportCollection(garage, new List<MemoryTab>(), items, package, null, CancellationToken.None, garageNotebook);
        using (ZipArchive archive = ZipFile.OpenRead(package))
        {
            JsonNode manifest = JsonNode.Parse(new StreamReader(archive.GetEntry("manifest.json").Open()).ReadToEnd());
            Check(archive.GetEntry("notebook.json") != null && (int)manifest["Format"] == 4, "the package has notebook.json, format 4");
        }
        string plain = Path.Combine(folder, "plain" + TransferPackage.Extension);
        TransferPackage.ExportCollection(garage, new List<MemoryTab>(), items, plain, null, CancellationToken.None, new MemoryNotebook());
        using (ZipArchive archive = ZipFile.OpenRead(plain))
            Check(archive.GetEntry("notebook.json") == null && (int)JsonNode.Parse(new StreamReader(archive.GetEntry("manifest.json").Open()).ReadToEnd())["Format"] == 1,
                "an empty notebook isn't packed: format 1, as older versions read");
        OpenedPackage opened = TransferPackage.Open(package);
        Check(opened.NotebookPages == 1, "the preview counts its page");
        Profile imported;
        using (StagedImport staged = TransferPackage.Stage(opened, null, CancellationToken.None))
            imported = TransferPackage.CommitCollection(staged, Profiles(main), "#3498DB");
        Invoke(main, "BuildNav");
        MemoryNotebookStore.TryLoad(imported, out MemoryNotebook copy, out _);
        Check(imported.Id != garage.Id && copy.Pages.Select(p => p.Body).SequenceEqual(garageNotebook.Pages.Select(p => p.Body)),
            "imported as a new collection with the same notebook pages");

        // Rename: the folder is named by Id, so the notebook stays.
        memory.Name = "Household (renamed)";
        ProfileManager.TrySaveProfiles(Profiles(main));
        Invoke(main, "BuildNav");
        Check(Same(Bytes(MemoryFiles.Notebook(memory)), saved), "renamed: the notebook is where it was");

        // Delete: kept while the delete can be undone, gone with the folder after.
        Invoke(main, "RemoveMemories", (IReadOnlyList<Profile>)new List<Profile> { memory, imported });
        await Wait(300);
        Check(File.Exists(MemoryFiles.Notebook(memory)), "deleted: the notebook kept while it can be undone");
        Check(main.Undo.Undo() && Same(Bytes(MemoryFiles.Notebook(memory)), saved) && MemoryNotebookStore.WrittenPages(memory) == 2, "undone: the memory and its notebook back");
        Check(main.Undo.Redo(), "deleted again");
        UndoService.EndAll();
        Check(!File.Exists(MemoryFiles.Notebook(memory)) && !Directory.Exists(MemoryFiles.Folder(memory)) && !Directory.Exists(MemoryFiles.Folder(imported)),
            "once it can't be undone: the folders and notebooks are gone");
        Check(File.Exists(MemoryFiles.Notebook(garage)), "another memory's notebook untouched");
    }

    // ---- Helpers ----------------------------------------------------------------------------------------

    static async Task<NotebookWindow> OpenNotebook(MainWindow main, Profile memory)
    {
        Check(MemoryNotebookStore.TryLoad(memory, out MemoryNotebook notebook, out string problem), "the notebook reads " + problem);
        NotebookWindow window = new NotebookWindow(memory, notebook) { Owner = main };
        window.Show();
        window.UpdateLayout();
        await Wait(400);
        return window;
    }

    // As a click on the button opens it: modal, and the button reads its count again once it closes.
    static async Task<NotebookWindow> OpenFromButton(NotebookButton button)
    {
        button.Dispatcher.BeginInvoke(new Action(button.Open));
        await Wait(500);
        return Application.Current.Windows.OfType<NotebookWindow>().First(w => w.IsVisible);
    }

    static NotebookButton NotebookButtonOf(MainWindow main) => (NotebookButton)((FrameworkElement)Content(main)).FindName("NotebookButton");
    static TextBlock CountOf(NotebookButton button) => (TextBlock)button.FindName("CountText");
    static List<NotePage> PagesOf(NotebookWindow window) => Field<List<NotePage>>(window.Notebook, "pages");
    static List<string> BodiesOf(NotebookWindow window) => Field<List<string>>(window.Notebook, "pageBodies");
    static string IndicatorOf(NotebookWindow window) => ((TextBlock)window.Notebook.FindName("PageIndicator")).Text;

    static string PlainOf(NotebookWindow window)
    {
        FlowDocument page = window.Notebook.Editor.Document;
        return new TextRange(page.ContentStart, page.ContentEnd).Text;
    }

    // The page's text replaced, as typing would (the page keeps its document and ruled lines).
    static void SetPage(NotebookWindow window, params Block[] blocks)
    {
        FlowDocument page = window.Notebook.Editor.Document;
        page.Blocks.Clear();
        page.Blocks.AddRange(blocks);
    }

    static List<Run> AllRuns(FlowDocument document)
    {
        List<Run> runs = new List<Run>();
        for (TextPointer at = document.ContentStart; at != null; at = at.GetNextContextPosition(LogicalDirection.Forward))
            if (at.Parent is Run run && !runs.Contains(run))
                runs.Add(run);
        return runs;
    }

    static MemoryNotebook Stored(Profile memory) =>
        JsonSerializer.Deserialize<MemoryNotebook>(File.ReadAllText(MemoryFiles.Notebook(memory)));

    // Every file of the data folder (logs left out) by its hash.
    static Dictionary<string, string> FolderSnapshot() =>
        Directory.EnumerateFiles(AppPaths.DataFolder, "*", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(f => Path.GetRelativePath(AppPaths.DataFolder, f), f =>
            {
                using (FileStream stream = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    return Convert.ToHexString(SHA256.HashData(stream));
            });

    static bool SameSnapshot(Dictionary<string, string> a, Dictionary<string, string> b) =>
        a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out string hash) && hash == p.Value);

    static string SnapshotDiff(Dictionary<string, string> a, Dictionary<string, string> b)
    {
        List<string> differ = a.Keys.Union(b.Keys).Where(k => !a.TryGetValue(k, out string x) || !b.TryGetValue(k, out string y) || x != y).ToList();
        return differ.Count == 0 ? "" : " (differs: " + string.Join(", ", differ) + ")";
    }

    static string DocxText(string path)
    {
        using (WordprocessingDocument document = WordprocessingDocument.Open(path, false))
            return document.MainDocumentPart.Document.Body.InnerText;
    }

    static string XlsxText(string path)
    {
        using (XLWorkbook workbook = new XLWorkbook(path))
            return string.Join("\n", workbook.Worksheets.SelectMany(s => s.CellsUsed()).Select(c => c.GetString()));
    }
}
