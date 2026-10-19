using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using TidyMind;

// Harness quicknote <data> <shotsFolder>: the Quick note is an ordinary window. Never topmost; another application
// in front puts it behind with TidyMind, and coming back to TidyMind brings it back. Closed from itself (Esc, its X)
// it gives the keyboard back to the window and field it was opened from, with nothing reloaded or switched; when
// that is gone (a closed project, another memory shown meanwhile) or minimized, nothing is forced. Typed text is
// saved on close.
static partial class Program
{
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hwnd, uint command);

    static IntPtr Hwnd(Window window) => new WindowInteropHelper(window).Handle;

    // Whether `a` is above `b` in the z-order (walking up from b).
    static bool IsAbove(Window a, Window b)
    {
        IntPtr target = Hwnd(a);
        for (IntPtr h = GetWindow(Hwnd(b), 3 /* GW_HWNDPREV */); h != IntPtr.Zero; h = GetWindow(h, 3))
            if (h == target)
                return true;
        return false;
    }

    static QuickNotesWindow Note() => Application.Current.Windows.OfType<QuickNotesWindow>().SingleOrDefault();

    static async Task<QuickNotesWindow> OpenNote()
    {
        OverlayManager.Route(Overlay.QuickNotes);
        await Wait(500);
        return Note();
    }

    static async Task QuickNoteScenarios(MainWindow main)
    {
        await Wait(1200);
        GuidedTour.Current?.Stop(TourEnd.Skipped);
        StartSelectWatcher();
        await Foreground(main);

        Console.WriteLine("An ordinary window, not always on top");
        QuickNotesWindow note = await OpenNote();
        Check(note != null && note.IsActive && Keyboard.FocusedElement == note.FindName("NotesTextBox"), "opened, active, typing goes into the note");
        Check(!note.Topmost && (GetWindowLong(Hwnd(note), -20 /* GWL_EXSTYLE */) & 0x8 /* WS_EX_TOPMOST */) == 0,
            "not Topmost, for WPF and for Windows");
        Check(note.Owner == main && !note.ShowInTaskbar, "owned by the main window, no taskbar button of its own");

        // Another application comes to the front: a window of its own (no owner), like Chrome from the taskbar.
        Window other = new Window { Title = "Another application", Width = 700, Height = 520, WindowStartupLocation = WindowStartupLocation.CenterScreen, ShowInTaskbar = false };
        other.Show();
        await Foreground(other);
        Check(other.IsActive && IsAbove(other, note) && IsAbove(other, main), "another application activated: it is in front of the note and of TidyMind");
        Check(note.IsVisible && note.WindowState == WindowState.Normal, "the note is behind it, not closed or minimized");
        await Foreground(main);
        Check(IsAbove(note, main) && IsAbove(note, other) && Note() == note, "back to TidyMind: the note is in front again, with the main window");
        other.Close();
        await Wait(200);
        await Foreground(note);
        note.Close();
        await Wait(400);

        await ReturnFromCollection(main, escape: true);
        await ReturnFromCollection(main, escape: false);
        await ReturnFromProject(main);
        await Fallbacks(main);
        Invoke(main, "NavigateHome");
    }

    // Opened from a collection page with the keyboard in its search box: after Esc or X, the same page object (not
    // reloaded), the same memory, tab, scroll position, search text and caret, and the keyboard back in the box.
    static async Task ReturnFromCollection(MainWindow main, bool escape)
    {
        string how = escape ? "Esc" : "its X";
        Console.WriteLine("Opened from a collection page, closed with " + how);
        Profile memory = AddMemory(main, "Jegyzet " + how, ProfileType.Collection);
        EntityStore.SaveOrWarn(memory, Enumerable.Range(1, 40).Select(i => new Entity
        {
            Id = Guid.NewGuid(), EntityType = "Clothing", Name = "Ing " + i, Order = i, CreatedDate = DateTime.Now
        }).ToList());
        Invoke(main, "NavigateTo", memory);
        await Wait(700);
        CollectionView view = (CollectionView)Content(main);
        ScrollViewer cards = (ScrollViewer)view.FindName("CardScroll");
        cards.ScrollToVerticalOffset(300);
        TextBox search = (TextBox)view.FindName("SearchBox");
        await Foreground(main);
        search.Focus();
        Keyboard.Focus(search);
        search.Text = "Ing";
        search.CaretIndex = 1;
        await Wait(600);
        double offset = cards.VerticalOffset;
        string key = Field<string>(main, "selectedKey");

        QuickNotesWindow note = await OpenNote();
        TextBox box = (TextBox)note.FindName("NotesTextBox");
        box.Text += "Jegyzet (" + how + ") ";
        if (escape)
            PressKey(note, Key.Escape);
        else
            note.Close(); // what the title bar's X does
        await Wait(600);

        Check(Note() == null && File.ReadAllText(AppPaths.Data("quicknotes.txt")).Contains("Jegyzet (" + how + ")"), how + ": closed, the typed text saved");
        Check(Content(main) == view && Field<string>(main, "selectedKey") == key, how + ": the same page, not reloaded, the same memory");
        Check(Math.Abs(cards.VerticalOffset - offset) < 0.5 && search.Text == "Ing" && search.CaretIndex == 1,
            how + ": scroll position, search text and caret as they were (" + cards.VerticalOffset + " / " + offset + ")");
        Check(main.IsActive && Keyboard.FocusedElement == search, how + ": TidyMind in front, the keyboard back in the search box");
    }

    // Opened from Project Details: the keyboard goes back to the field it was in there, not to the main window.
    static async Task ReturnFromProject(MainWindow main)
    {
        Console.WriteLine("Opened from Project Details");
        Project project = new Project { Id = Guid.NewGuid(), Name = "Konyha", Tasks = new List<TaskItem> { new TaskItem { Title = "Csempe" } } };
        ProjectWindow window = await OpenProject(main, project);
        await Foreground(window);
        TextBox name = (TextBox)window.FindName("NameBox");
        name.Focus();
        Keyboard.Focus(name);
        name.CaretIndex = 2;
        QuickNotesWindow note = await OpenNote();
        Check(note != null && note.IsActive && IsAbove(note, window), "the note opens in front of the project");
        PressKey(note, Key.Escape);
        await Wait(600);
        Check(window.IsActive && Keyboard.FocusedElement == name && name.CaretIndex == 2, "Esc: back in the project's name box, the caret where it was");
        window.Close();
        await Wait(300);
    }

    static async Task Fallbacks(MainWindow main)
    {
        Console.WriteLine("When where it was opened from is gone");
        // The project closed (or deleted) while the note was open.
        Project project = new Project { Id = Guid.NewGuid(), Name = "Törölt", Tasks = new List<TaskItem>() };
        ProjectWindow window = await OpenProject(main, project);
        await Foreground(window);
        QuickNotesWindow note = await OpenNote();
        window.Close();
        await Wait(300);
        await Foreground(note);
        PressKey(note, Key.Escape);
        await Wait(600);
        Check(Note() == null && Content(main) != null && Application.Current.Windows.OfType<ProjectWindow>().All(w => w != window),
            "the project is gone: the note closes, nothing breaks, the main window keeps its page");

        // Another memory shown meanwhile: it stays; the old page's box can't take the keyboard any more.
        Profile first = AddMemory(main, "Első", ProfileType.Collection), second = AddMemory(main, "Második", ProfileType.Collection);
        Invoke(main, "NavigateTo", first);
        await Wait(500);
        TextBox oldSearch = (TextBox)((CollectionView)Content(main)).FindName("SearchBox");
        await Foreground(main);
        Keyboard.Focus(oldSearch);
        note = await OpenNote();
        Invoke(main, "NavigateTo", second);
        await Wait(500);
        object shown = Content(main);
        await Foreground(note);
        PressKey(note, Key.Escape);
        await Wait(600);
        Check(Note() == null && Content(main) == shown && Field<string>(main, "selectedKey") == second.Id.ToString("N") && Keyboard.FocusedElement != oldSearch,
            "another memory opened meanwhile: it stays shown, nothing is switched back");

        // Minimized TidyMind is not brought back by a close that didn't come from the note.
        await Foreground(main);
        note = await OpenNote();
        main.WindowState = WindowState.Minimized;
        await Wait(300);
        note.Close();
        await Wait(600);
        Check(main.WindowState == WindowState.Minimized, "closed while TidyMind is minimized: it stays minimized");
        main.WindowState = WindowState.Normal;
        await Wait(300);
    }
}
