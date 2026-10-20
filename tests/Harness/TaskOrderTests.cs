using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using ClosedXML.Excel;
using TidyMind;

// Part of `tasks`: in every checkbox task list the open tasks stand above the done ones (display only; the stored order
// is never touched), ticking moves a task without a jump (the keyboard, the selection and the scroll position stay),
// unticking puts it back where it was among the open ones, and the exports list the tasks the same way.
static partial class Program
{
    // What the click on a box does: the box toggles, then its Click is raised.
    static void Tick(CheckBox box)
    {
        box.IsChecked = box.IsChecked != true;
        box.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    }

    static bool Order(IEnumerable<string> shown, string expected) => shown.SequenceEqual(expected.Split(' '));

    static async Task TaskOrderScenarios(MainWindow main)
    {
        Console.WriteLine("Open tasks above done ones, in every list");
        Check(TaskOrder.OpenFirst(new[] { 5, 4, 3, 2, 1 }, n => n % 2 == 0).SequenceEqual(new[] { 5, 3, 1, 4, 2 }), "TaskOrder.OpenFirst is stable: each group keeps its own order");
        await ProjectTaskOrder(main);
        await MemoryTodoOrder(main);
        await HomeTodoOrder(main);
        TaskExportOrder();
    }

    // ---- Project Details -------------------------------------------------------------------------

    static async Task ProjectTaskOrder(MainWindow main)
    {
        string[] stored = "T1 T2 T3 T4 T5 T6 T7 T8".Split(' ');
        Project project = new Project
        {
            Name = "Order", Tasks = stored.Select((t, i) => new TaskItem { Title = t, IsDone = i == 1 || i == 3 || i == 6 }).ToList()
        };
        ProjectWindow window = await OpenProject(main, project, 800);
        ListBox list = (ListBox)window.FindName("TaskList");
        ScrollViewer page = PageScroll(window);
        List<string> Shown() => list.Items.Cast<TaskItem>().Select(t => t.Title).ToList();
        CheckBox BoxOf(string title) => Descendants(list).OfType<CheckBox>().First(c => ((TaskItem)c.DataContext).Title == title);
        TaskItem Task(string title) => project.Tasks.First(t => t.Title == title);

        Check(Order(Shown(), "T1 T3 T5 T6 T8 T2 T4 T7"), "project tasks: the open ones, then the done ones, each in the order stored (" + string.Join(" ", Shown()) + ")");
        list.BringIntoView();
        await Wait(300);
        RenderWindow(window, "taskorder-1-project-tasks");
        Check(project.Tasks.Select(t => t.Title).SequenceEqual(stored), "  what is stored keeps its order");

        // A tick moves the one row; the keyboard stays on it, the selection and the scroll position stay.
        list.SelectedItems.Add(Task("T1"));
        list.SelectedItems.Add(Task("T6"));
        page.ScrollToVerticalOffset(Math.Min(20, page.ScrollableHeight));
        await Wait(200);
        BoxOf("T3").Focus(); // the user clicked a box that was in view: so is the page after this
        await Wait(200);
        double offset = page.VerticalOffset;
        Tick(BoxOf("T3"));
        await Wait(300);
        Check(Order(Shown(), "T1 T5 T6 T8 T2 T3 T4 T7") && project.Tasks.First(t => t.Title == "T3").IsDone, "ticking T3 moves it to the done ones, among them in its stored place (" + string.Join(" ", Shown()) + ")");
        Check(Keyboard.FocusedElement is CheckBox focused && ((TaskItem)focused.DataContext).Title == "T3", "  the keyboard is still on T3's box");
        Check(list.SelectedItems.Cast<TaskItem>().Select(t => t.Title).OrderBy(t => t).SequenceEqual(new[] { "T1", "T6" }) && Math.Abs(page.VerticalOffset - offset) < 1,
            "  the selection (T1, T6) and the page's scroll position are as they were (selected " + string.Join(" ", list.SelectedItems.Cast<TaskItem>().Select(t => t.Title)) + "; offset " + offset.ToString("0") + " -> " + page.VerticalOffset.ToString("0") + ")");
        Check(project.Tasks.Select(t => t.Title).SequenceEqual(stored), "  stored order still untouched");

        // A selected task ticked moves and stays selected.
        list.SelectedItems.Add(Task("T5"));
        Tick(BoxOf("T5"));
        await Wait(300);
        Check(Order(Shown(), "T1 T6 T8 T2 T3 T4 T5 T7") && list.SelectedItems.Contains(Task("T5")) && list.SelectedItems.Count == 3,
            "a selected task ticked keeps its selection (" + string.Join(" ", Shown()) + ")");

        // Unticking puts it back among the open ones where it was.
        Tick(BoxOf("T3"));
        await Wait(300);
        Tick(BoxOf("T5"));
        await Wait(300);
        Check(Order(Shown(), "T1 T3 T5 T6 T8 T2 T4 T7"), "unticking T3 and T5 puts them back among the open ones, T3 before T5 as before (" + string.Join(" ", Shown()) + ")");

        // New tasks go after the open ones; rename and delete leave the rest alone.
        ((TextBox)window.FindName("TaskInput")).Text = "T9";
        Invoke(window, "AddTask");
        Check(Order(Shown(), "T1 T3 T5 T6 T8 T9 T2 T4 T7") && project.Tasks.Last().Title == "T9", "a new task goes after the open ones and is stored last");
        Invoke(window, "DeleteTask", Task("T6"));
        Check(Order(Shown(), "T1 T3 T5 T8 T9 T2 T4 T7") && list.Items.Count == project.Tasks.Count, "delete: the others keep their places");
        window.Close();
        await Wait(300);
        Check(project.Tasks.Select(t => t.Title).SequenceEqual("T1 T2 T3 T4 T5 T7 T8 T9".Split(' ')), "closed: stored order is the added order, nothing sorted");
    }

    // ---- A memory's to-do list ---------------------------------------------------------------------

    static async Task MemoryTodoOrder(MainWindow main)
    {
        Profile memory = AddMemory(main, "Order memory", ProfileType.Project);
        List<TodoEntry> entries = "A B C D E".Split(' ').Select((t, i) => new TodoEntry { Id = Guid.NewGuid(), Text = t, IsDone = i == 1 || i == 3 }).ToList();
        MemoryTodoService.TrySave(memory, entries);
        memory.TodoCollapsed = false;
        Invoke(main, "NavigateTo", memory);
        await Wait(800);
        MemoryTodoPanel panel = (MemoryTodoPanel)((FrameworkElement)Content(main)).FindName("TodoPanel");
        StackPanel rows = (StackPanel)panel.FindName("ItemList");
        ScrollViewer scroll = (ScrollViewer)panel.FindName("ListScroll");
        ItemSelection<TodoEntry> selection = Field<ItemSelection<TodoEntry>>(panel, "selection");
        List<TodoEntry> live = Field<List<TodoEntry>>(panel, "todos");
        List<string> Shown() => rows.Children.Cast<FrameworkElement>().Select(r => Descendants(r).OfType<TextBox>().First().Text).ToList();
        List<string> Stored() { MemoryTodoService.TryLoad(memory, out List<TodoEntry> now); return now.Select(t => t.Text).ToList(); }
        CheckBox BoxOf(string text) => Descendants(rows).OfType<CheckBox>().First(c => ((Panel)c.Parent).Children.OfType<TextBox>().Any(t => t.Text == text));

        Check(Order(Shown(), "A C E B D"), "memory to-dos: the open ones, then the done ones (" + string.Join(" ", Shown()) + ")");
        scroll.ScrollToVerticalOffset(12);
        await Wait(200);
        double offset = scroll.VerticalOffset;
        selection.Click(live.First(t => t.Text == "C"), ModifierKeys.Control);
        BoxOf("A").Focus();
        Tick(BoxOf("A"));
        await Wait(300);
        Check(Order(Shown(), "C E A B D"), "ticking A moves it among the done ones, in its stored place (" + string.Join(" ", Shown()) + ")");
        RenderWindow(main, "taskorder-2-memory-todos");
        Check(Order(Stored(), "A B C D E"), "  todos.json keeps the order they were added in");
        Check(BoxOf("A").IsKeyboardFocused, "  the keyboard is still on A's box");
        Check(selection.Count == 1 && selection.IsSelected(live.First(t => t.Text == "C")) && Math.Abs(scroll.VerticalOffset - offset) < 1,
            "  the selection (C) and the list's scroll position (" + offset.ToString("0") + ") are as they were");
        Tick(BoxOf("A"));
        await Wait(300);
        Check(Order(Shown(), "A C E B D"), "unticking A puts it back where it was (" + string.Join(" ", Shown()) + ")");

        // A new one goes after the open ones.
        TextBox input = (TextBox)panel.FindName("Input");
        input.Text = "F";
        PressKey(input, Key.Enter);
        await Wait(300);
        Check(Order(Shown(), "A C E F B D") && Order(Stored(), "A B C D E F"), "a new to-do goes after the open ones and is stored last (" + string.Join(" ", Shown()) + ")");
        RemoveMemory(main, memory);
        Invoke(main, "NavigateHome");
    }

    // ---- Home's to-dos -------------------------------------------------------------------------------

    static async Task HomeTodoOrder(MainWindow main)
    {
        Invoke(main, "NavigateHome");
        await Wait(500);
        QuickTodo[] quick =
        {
            QuickTodoService.Add("Q1", DateTime.Today), QuickTodoService.Add("Q2", DateTime.Today), QuickTodoService.Add("Q3", DateTime.Today.AddDays(1))
        };
        HomeView home = (HomeView)Content(main);
        QuickTodoListControl list = (QuickTodoListControl)home.FindName("QuickTodos");
        list.Refresh();
        await Wait(300);
        StackPanel rows = (StackPanel)list.FindName("TodoList");
        ItemSelection<Guid> selection = Field<ItemSelection<Guid>>(list, "selection");
        List<string> Shown() => rows.Children.OfType<FrameworkElement>().Select(r => Descendants(r).OfType<TextBox>().FirstOrDefault()?.Text)
            .Where(t => t != null && t.StartsWith("Q")).ToList();
        CheckBox BoxOf(string text) => Descendants(rows).OfType<CheckBox>().First(c => ((Panel)c.Parent).Children.OfType<TextBox>().Any(t => t.Text == text));

        Check(Order(Shown(), "Q1 Q2 Q3"), "Home's to-dos: by day (" + string.Join(" ", Shown()) + ")");
        selection.Click(quick[2].Id, ModifierKeys.Control);
        BoxOf("Q1").Focus();
        Tick(BoxOf("Q1"));
        await Wait(300);
        Check(Order(Shown(), "Q2 Q3 Q1"), "ticking Q1: it goes below the open ones, still listed so it can be undone (" + string.Join(" ", Shown()) + ")");
        RenderWindow(main, "taskorder-3-home-todos");
        Check(BoxOf("Q1").IsKeyboardFocused && selection.Count == 1 && selection.IsSelected(quick[2].Id), "  the keyboard is still on Q1's box and Q3 is still selected");
        Tick(BoxOf("Q2"));
        await Wait(300);
        Check(Order(Shown(), "Q3 Q1 Q2"), "Q2 ticked as well: the done ones by day, then as added (" + string.Join(" ", Shown()) + ")");
        Tick(BoxOf("Q1"));
        await Wait(300);
        Check(Order(Shown(), "Q1 Q3 Q2"), "unticking Q1 puts it back before Q3, as it was (" + string.Join(" ", Shown()) + ")");
        QuickTodoService.Delete(quick.Select(q => q.Id).ToArray());
    }

    // ---- Exports ---------------------------------------------------------------------------------------

    static void TaskExportOrder()
    {
        Project project = new Project
        {
            Id = Guid.NewGuid(), Name = "Order",
            Tasks = new List<TaskItem>
            {
                new TaskItem { Title = "Alpha one" }, new TaskItem { Title = "Bravo two", IsDone = true }, new TaskItem { Title = "Charlie three" },
                new TaskItem { Title = "Delta four", IsDone = true }, new TaskItem { Title = "Echo five" }
            }
        };
        string[] expected = { "Alpha one", "Charlie three", "Echo five", "Bravo two", "Delta four" };
        string words = WordSample("tasks-order", path => ExportService.ExportProject(project, path, ExportFormat.Docx));
        int[] at = expected.Select(t => words.IndexOf(t, StringComparison.Ordinal)).ToArray();
        Check(at.All(i => i >= 0) && at.SequenceEqual(at.OrderBy(i => i)), "Word: the open tasks, then the done ones, as in the app");
        ExcelSample("tasks-order", path => ExportService.ExportProject(project, path, ExportFormat.Excel), book =>
        {
            IXLWorksheet sheet = book.Worksheet(Loc.T("ProjectWindow.Tasks"));
            List<string> rows = Enumerable.Range(XlHeader + 1, 5).Select(r => sheet.Cell(r, 2).GetString()).ToList();
            Check(rows.SequenceEqual(expected), "Excel: the same order (" + string.Join(", ", rows) + ")");
        });
    }
}
