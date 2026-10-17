using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using TidyMind;

// Harness tasks <data> <shotsFolder>: Project Details as one scrolling page. The task list, files, description and
// notes page grow with their content; nothing scrolls inside the page; the wheel, the keyboard and the caret move
// the page; adding, ticking and deleting tasks still work; 250 tasks open quickly. Screenshots with 5 and 60 tasks.
static partial class Program
{
    static async Task TaskListScenarios(MainWindow main)
    {
        await Wait(1200);
        GuidedTour.Current?.Stop(TourEnd.Skipped);
        await Wait(300);

        foreach (int count in new[] { 0, 5, 60 })
        {
            foreach (string code in count == 60 ? Codes : new[] { Loc.English })
            {
                Loc.Switch(code);
                string tag = (code == Loc.SerbianLatin ? "sr" : code) + "-" + count;
                Console.WriteLine("Project Details with " + count + " tasks (" + code + ")");
                ProjectWindow window = await OpenProject(main, SampleProject(count, files: count == 60 ? 20 : 0));
                ListBox list = (ListBox)window.FindName("TaskList");
                ScrollViewer page = PageScroll(window);

                Check(!Descendants(list).OfType<ScrollViewer>().Any(), tag + ": no scroll area inside the task list");
                Check(list.Items.Count == count && list.ActualHeight >= count * 34, tag + ": every row laid out, the list "
                    + list.ActualHeight.ToString("0") + " px tall");
                List<ScrollViewer> scrolling = Descendants(window).OfType<ScrollViewer>().Where(s => s.ScrollableHeight > 0.5).ToList();
                Check(scrolling.Count == (page.ScrollableHeight > 0.5 ? 1 : 0) && (scrolling.Count == 0 || scrolling[0] == page),
                    tag + ": only the page itself scrolls (" + scrolling.Count + " scrolling area)");
                List<string> cut = Clipped(window).ToList();
                Check(cut.Count == 0, tag + ": nothing cut off" + (cut.Count > 0 ? ": " + string.Join("; ", cut) : ""));
                if (count == 0)
                    Check(Descendants(list).OfType<TextBlock>().Any(t => t.IsVisible && t.Text == Loc.T("ProjectWindow.NoTasks")),
                        tag + ": the empty list says so");

                RenderWindow(window, "tasks-" + tag + "-1-top");
                list.BringIntoView();
                await Wait(300);
                RenderWindow(window, "tasks-" + tag + "-2-tasks");

                if (count == 60 && code == Loc.English)
                    await Behaviour(window, list, page);
                window.Close();
                await Wait(300);
            }
        }
        Loc.Switch(Loc.English);
        await LongLists(main);
    }

    // Wheel, keyboard, caret, and the task actions, on the 60-task page.
    static async Task Behaviour(ProjectWindow window, ListBox list, ScrollViewer page)
    {
        page.ScrollToTop();
        await Wait(200);
        ListBoxItem row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(10);
        double before = page.VerticalOffset;
        Wheel(row, -120);
        await Wait(200);
        Check(page.VerticalOffset > before, "the wheel over a task row scrolls the page (" + before.ToString("0") + " → " + page.VerticalOffset.ToString("0") + ")");
        before = page.VerticalOffset;
        Wheel(row, 120);
        await Wait(200);
        Check(page.VerticalOffset < before, "and back up");

        StackPanel fileRows = (StackPanel)((FrameworkElement)window.FindName("Files")).FindName("FileRows");
        FrameworkElement fileRow = (FrameworkElement)fileRows.Children[5];
        fileRow.BringIntoView();
        await Wait(200);
        before = page.VerticalOffset;
        Wheel(fileRow, -120);
        await Wait(200);
        Check(fileRows.Children.Count == 20 && page.VerticalOffset > before, "20 files all listed; the wheel over a file row scrolls the page");

        foreach (string name in new[] { "DescriptionBox", "NotesBox" })
        {
            RichTextBox editor = (RichTextBox)window.FindName(name);
            editor.BringIntoView();
            await Wait(200);
            before = page.VerticalOffset;
            // From inside the editor's own scroll area, which would take the wheel if nothing passed it on.
            Wheel(Descendants(editor).OfType<ScrollContentPresenter>().First(), before > 10 ? 120 : -120);
            await Wait(200);
            Check(page.VerticalOffset != before, "the wheel over " + name + " scrolls the page");
        }

        // A long description grows the field; the caret at its end is brought into view by scrolling the page.
        RichTextBox description = (RichTextBox)window.FindName("DescriptionBox");
        description.Document.Blocks.Clear();
        for (int i = 1; i <= 40; i++)
            description.Document.Blocks.Add(new Paragraph(new Run("Description line " + i)));
        page.ScrollToTop();
        await Wait(200);
        description.Focus();
        description.CaretPosition = description.Document.ContentEnd;
        await Wait(400);
        Rect caret = description.CaretPosition.GetCharacterRect(LogicalDirection.Forward);
        Point inPage = description.TranslatePoint(caret.TopLeft, page);
        Check(description.ActualHeight > 600 && inPage.Y >= 0 && inPage.Y + caret.Height <= page.ViewportHeight,
            "a 40-line description: the field grows (" + description.ActualHeight.ToString("0") + " px) and the caret at its end is on screen");
        RenderWindow(window, "tasks-en-60-3-long-description");

        // Clicking into a field taller than the window doesn't make the page jump to the field's top or bottom.
        Keyboard.ClearFocus();
        Point top = description.TranslatePoint(new Point(0, 0), page);
        page.ScrollToVerticalOffset(page.VerticalOffset + top.Y - 80);
        await Wait(300);
        before = page.VerticalOffset;
        description.CaretPosition = description.Document.ContentStart.GetPositionAtOffset(40) ?? description.Document.ContentStart;
        description.Focus();
        await Wait(400);
        Check(description.IsKeyboardFocused && Math.Abs(page.VerticalOffset - before) < 1, "focusing a description taller than the window leaves the page where it was ("
            + before.ToString("0") + " → " + page.VerticalOffset.ToString("0") + ")");

        // A long notes page grows too, its ruled lines with it.
        RichTextBox notes = (RichTextBox)window.FindName("NotesBox");
        for (int i = 1; i <= 30; i++)
            notes.Document.Blocks.Add(new Paragraph(new Run("Note line " + i)));
        await Wait(300);
        notes.Focus();
        notes.CaretPosition = notes.Document.ContentEnd;
        await Wait(400);
        Check(notes.ActualHeight > 30 * 24 - 1, "a 30-line notes page: the page grows (" + notes.ActualHeight.ToString("0") + " px)");
        RenderWindow(window, "tasks-en-60-5-long-notes");

        // The keyboard: End in the list goes to the last task and the page follows.
        list.SelectedIndex = 0;
        ((ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0)).Focus();
        await Wait(200);
        PressKey(list.ItemContainerGenerator.ContainerFromIndex(0) as UIElement, System.Windows.Input.Key.End);
        await Wait(400);
        ListBoxItem last = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(list.Items.Count - 1);
        Point lastTop = last.TranslatePoint(new Point(0, 0), page);
        Check(list.SelectedIndex == list.Items.Count - 1 && last.IsKeyboardFocused && lastTop.Y >= 0 && lastTop.Y + last.ActualHeight <= page.ViewportHeight + 1,
            "End selects the last task and the page scrolls to it");
        PressKey(last, System.Windows.Input.Key.Up);
        await Wait(200);
        Check(list.SelectedIndex == list.Items.Count - 2, "Up moves to the task above");
        PressKey((UIElement)list.ItemContainerGenerator.ContainerFromIndex(list.Items.Count - 2), System.Windows.Input.Key.Home);
        await Wait(400);
        ListBoxItem firstRow = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
        Check(list.SelectedIndex == 0 && firstRow.TranslatePoint(new Point(0, 0), page).Y >= 0, "Home goes back to the first task, on screen");

        // Add, tick, delete.
        Project project = (Project)typeof(ProjectWindow).GetField("currentProject", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(window);
        TextBox input = (TextBox)window.FindName("TaskInput");
        input.Focus();
        input.Text = "Added by the harness";
        PressKey(input, System.Windows.Input.Key.Enter);
        await Wait(200);
        Check(list.Items.Count == 61 && project.Tasks.Count == 61 && project.Tasks.Last().Title == "Added by the harness" && input.Text == ""
            && input.IsKeyboardFocused, "Enter adds a task and keeps the cursor in the box");
        ListBoxItem first = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
        CheckBox tick = Descendants(first).OfType<CheckBox>().First();
        bool wasDone = project.Tasks[0].IsDone;
        tick.IsChecked = !wasDone;
        Check(project.Tasks[0].IsDone == !wasDone, "ticking a row marks its task done");
        tick.IsChecked = wasDone;
        Button delete = Descendants(first).OfType<Button>().Last();
        TaskItem gone = project.Tasks[0];
        delete.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, delete));
        await Wait(200);
        Check(list.Items.Count == 60 && !project.Tasks.Contains(gone), "a row's delete button removes the task");

        // The smallest window: still one page, nothing cut off.
        window.Width = window.MinWidth;
        window.Height = window.MinHeight;
        await Wait(400);
        List<string> cut = Clipped(window).ToList();
        Check(cut.Count == 0 && Descendants(window).OfType<ScrollViewer>().Count(s => s.ScrollableHeight > 0.5) == 1,
            "at the smallest size: one scrolling page, nothing cut off" + (cut.Count > 0 ? ": " + string.Join("; ", cut) : ""));
        list.BringIntoView();
        await Wait(200);
        RenderWindow(window, "tasks-en-60-4-smallest");
    }

    static async Task LongLists(MainWindow main)
    {
        foreach (int count in new[] { 5, 250, 500 })
        {
            Stopwatch took = Stopwatch.StartNew();
            ProjectWindow window = await OpenProject(main, SampleProject(count), settle: 0);
            long opened = took.ElapsedMilliseconds;
            ListBox list = (ListBox)window.FindName("TaskList");
            ScrollViewer page = PageScroll(window);
            took.Restart();
            page.ScrollToEnd();
            window.UpdateLayout();
            long scrolled = took.ElapsedMilliseconds;
            Check(list.Items.Count == count && opened < (count <= 250 ? 1500 : 3000) && scrolled < 500,
                count + " tasks: the window is open and laid out in " + opened + " ms, scrolled to the end in " + scrolled + " ms");
            window.Close();
            await Wait(300);
        }
    }

    static async Task<ProjectWindow> OpenProject(MainWindow main, Project project, int settle = 600)
    {
        ProjectWindow window = new ProjectWindow(project) { Owner = main, Width = 640, Height = 760 };
        window.Show();
        window.UpdateLayout();
        await Wait(settle);
        return window;
    }

    static ScrollViewer PageScroll(Window window) =>
        Descendants(window).OfType<ScrollViewer>().First(s => s.Content is StackPanel);

    static Project SampleProject(int tasks, int files = 0)
    {
        string[] titles =
        {
            "Book the venue", "Send invitations to everyone on the list, including the people from the old office",
            "Order the cake", "Ask Ágnes about the music", "Print the seating plan", "Buy candles", "Confirm the photographer",
            "Pick up the suit from the tailor on Thursday afternoon", "Đorđe: chairs", "Call the caterer"
        };
        Project project = new Project { Name = "Harness project", Tasks = new List<TaskItem>() };
        for (int i = 0; i < tasks; i++)
            project.Tasks.Add(new TaskItem { Title = (i + 1) + ". " + titles[i % titles.Length], IsDone = i % 4 == 1 });
        project.Attachments = Enumerable.Range(1, files).Select(i => new Attachment
        {
            Id = Guid.NewGuid(), DisplayName = "Invoice " + i + ".pdf", StoredFileName = Guid.NewGuid() + ".pdf", SizeBytes = 1000 * i,
            AddedDate = DateTime.Today
        }).ToList();
        return project;
    }

    static void Wheel(IInputElement target, int delta)
    {
        MouseWheelEventArgs preview = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
        target.RaiseEvent(preview);
        if (!preview.Handled)
            target.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = Mouse.MouseWheelEvent });
    }

    static void PressKey(UIElement target, Key key)
    {
        PresentationSource source = PresentationSource.FromVisual(target);
        KeyEventArgs preview = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        target.RaiseEvent(preview);
        if (!preview.Handled)
            target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.KeyDownEvent });
    }
}
