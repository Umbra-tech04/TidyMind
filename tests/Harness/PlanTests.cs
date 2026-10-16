using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32.TaskScheduler;
using TidyMind;

// Day Plan tests ("Harness plan <dataFolder> <shotsFolder>"), on the app started by App.OnStartup with a temp data
// folder. Message boxes are answered by a background watcher (Yes where there is one, else OK) and logged.
static partial class Program
{
    static readonly DateTime Today = DateTime.Today;
    static readonly DateTime Tomorrow = DateTime.Today.AddDays(1);
    static readonly List<string> answered = new List<string>();
    static readonly HashSet<Guid> testReminderIds = new HashSet<Guid>();

    static async System.Threading.Tasks.Task PlanScenarios(MainWindow main)
    {
        await Wait(1200);
        GuidedTour.Current?.Stop(TourEnd.Skipped); // the welcome card of a fresh data folder
        main.Topmost = true;
        StartAnswering();
        int tasksBefore = ReminderTaskNames().Count;

        Profile studies = AddProjectMemory(main, "Studies");
        File.WriteAllText(MemoryFiles.Todos(studies),
            "[{\"Text\":\"Read chapter 3\",\"IsDone\":false},{\"Text\":\"Árvíztűrő essay\",\"IsDone\":false,\"Kept\":1}]");
        QuickTodo math = QuickTodoService.Add("Math homework", Today);
        QuickTodo later = QuickTodoService.Add("Pack for the trip", Tomorrow);
        QuickTodo overdue = QuickTodoService.Add("Call the bank", Today.AddDays(-2));

        try
        {
            Validation();
            RoundTrip();
            await TodoSync(main, studies, math);
            Reminders();
            Search(main);
            Export(main);
            BrokenFile(main);
            await EdgeLayouts(main);
            await Interactions(main, studies);
            await CalendarAndHome(main);
            await Narrow(main);
            await TourStep(main);
            BackupRestore();
        }
        finally
        {
            CleanUpReminders();
            int left = ReminderTaskNames().Count;
            Check(left == tasksBefore, "no TidyMind_Reminder_* task left behind (" + left + ", before " + tasksBefore + ")");
            Console.WriteLine("message boxes answered: " + answered.Count);
            foreach (string box in answered)
                Console.WriteLine("  " + box);
        }
    }

    // ---- Data -------------------------------------------------------------

    static void Validation()
    {
        Console.WriteLine("Times");
        Check(DayPlanBlock.CheckTimes(13 * 60, 15 * 60) == null, "13:00-15:00 is fine");
        Check(DayPlanBlock.CheckTimes(23 * 60 + 45, 24 * 60) == null, "23:45-24:00 is fine");
        Check(DayPlanBlock.CheckTimes(600, 600) != null, "end = start refused: " + DayPlanBlock.CheckTimes(600, 600));
        Check(DayPlanBlock.CheckTimes(600, 540) != null, "end before start refused");
        Check(DayPlanBlock.CheckTimes(600, 610) != null, "10 minutes refused: " + DayPlanBlock.CheckTimes(600, 610));
        Check(DayPlanBlock.CheckTimes(23 * 60, 24 * 60 + 30) != null, "past midnight refused: " + DayPlanBlock.CheckTimes(1380, 1470));
        Check(DayPlanBlock.CheckTimes(-15, 30) != null, "before 00:00 refused");
        Check(DayPlanBlock.Clock(1440) == "24:00", "the end of the day reads 24:00");
    }

    static void RoundTrip()
    {
        Console.WriteLine("Storage round trip");
        // A file from a "newer version": an unknown top-level list and an unknown field on a block.
        Guid known = Guid.NewGuid();
        File.WriteAllText(DayPlanStore.FilePath, "{\"Blocks\":[{\"Id\":\"" + known + "\",\"Date\":\"" + Tomorrow.ToString("yyyy-MM-dd")
            + "T00:00:00\",\"StartMinute\":480,\"EndMinute\":540,\"Title\":\"Gym\",\"Future\":7}],\"Templates\":[{\"Name\":\"Week\"}]}");

        DayPlanBlock added = new DayPlanBlock
        {
            Id = Guid.NewGuid(), Date = Tomorrow, StartMinute = 13 * 60, EndMinute = 15 * 60, Title = "Matematika házi feladat",
            Notes = "Exercises 1-4\nÁrvíztűrő tükörfúrógép", ColorHex = "#27AE60", RemindMinutesBefore = 10,
            Todo = new TodoLink { Source = TodoSource.Memory, Id = Guid.NewGuid(), MemoryId = Guid.NewGuid() }
        };
        Check(DayPlanStore.Save(added), "a block saves");

        Check(DayPlanStore.TryLoad(out DayPlanFile plan), "the file reads back");
        DayPlanBlock back = plan.Blocks.Single(b => b.Id == added.Id);
        Check(back.Title == added.Title && back.Notes == added.Notes && back.ColorHex == added.ColorHex
              && back.StartMinute == 780 && back.EndMinute == 900 && back.Date == Tomorrow && back.RemindMinutesBefore == 10
              && added.Todo.Matches(back.Todo), "every field comes back the same");

        // The block from the "newer version" edited the way the editor does (no ExtensionData passed in).
        DayPlanBlock gym = plan.Blocks.Single(b => b.Id == known);
        Check(DayPlanStore.Save(new DayPlanBlock { Id = known, Date = Tomorrow, StartMinute = 480, EndMinute = 570, Title = "Gym" }),
            "the older block saves edited");
        string raw = File.ReadAllText(DayPlanStore.FilePath);
        Check(raw.Contains("\"Templates\"") && raw.Contains("\"Future\":7"), "unknown fields (file and block) are kept");
        Check(DayPlanStore.Delete(known) && DayPlanStore.Delete(known), "delete, and delete again, both succeed");
        Check(DayPlanStore.Delete(added.Id), "cleanup");
    }

    static async System.Threading.Tasks.Task TodoSync(MainWindow main, Profile studies, QuickTodo math)
    {
        Console.WriteLine("To-dos and done, both ways");
        List<PlanTodo> all = DayPlanTodos.All(Profiles(main), out List<string> unreadable);
        Check(unreadable.Count == 0, "every to-do list reads");
        Check(File.ReadAllText(MemoryFiles.Todos(studies)).Contains("\"Id\""), "memory to-dos got stable Ids, saved straight away");
        Check(File.ReadAllText(MemoryFiles.Todos(studies)).Contains("\"Kept\":1"), "and kept their unknown fields");
        List<PlanTodo> again = DayPlanTodos.All(Profiles(main), out _);
        Check(all.Select(t => t.Link.Id).SequenceEqual(again.Select(t => t.Link.Id)), "the Ids stay the same on the next read");

        List<PlanTodo> todayOpen = DayPlanTodos.Unscheduled(all, new List<DayPlanBlock>(), Today);
        Check(todayOpen.Any(t => t.Text == "Math homework") && todayOpen.Any(t => t.Text == "Call the bank")
              && !todayOpen.Any(t => t.Text == "Pack for the trip") && todayOpen.Count(t => t.Where == "Studies") == 2,
            "today: Home's due and rolled-over to-dos plus the memory's; not tomorrow's");
        List<PlanTodo> tomorrowOpen = DayPlanTodos.Unscheduled(all, new List<DayPlanBlock>(), Tomorrow);
        Check(tomorrowOpen.Any(t => t.Text == "Pack for the trip") && !tomorrowOpen.Any(t => t.Text == "Call the bank"),
            "tomorrow: its own Home to-do, not the overdue one");

        PlanTodo homeTodo = all.Single(t => t.Text == "Math homework");
        PlanTodo memoryTodo = all.Single(t => t.Text == "Read chapter 3");
        DayPlanBlock homeBlock = DayPlanActions.AddFromTodo(homeTodo, Today, 23 * 60 + 30);
        Check(homeBlock != null && homeBlock.StartMinute == 23 * 60 && homeBlock.EndMinute == 24 * 60,
            "a to-do near midnight gets an hour that still ends by 24:00");
        DayPlanBlock memoryBlock = DayPlanActions.AddFromTodo(memoryTodo, Today, 9 * 60);
        Check(DayPlanTodos.Unscheduled(DayPlanTodos.All(Profiles(main), out _), Blocks(Today), Today).All(t => t.Text != "Math homework" && t.Text != "Read chapter 3"),
            "planned to-dos leave the panel's list");

        // Block -> to-do
        Check(DayPlanActions.SetDone(homeBlock, true, homeTodo, Profiles(main)), "ticking the Home to-do's block");
        Check(QuickTodoService.LoadTodos().Single(t => t.Id == math.Id).IsDone, "  ticks the Home to-do (read from the file)");
        Check(Block(homeBlock.Id).Done, "  and the block (read from the file)");
        Check(DayPlanActions.SetDone(memoryBlock, true, memoryTodo, Profiles(main)), "ticking the memory to-do's block");
        MemoryTodoService.TryLoad(studies, out List<TodoEntry> entries);
        Check(entries.Single(t => t.Text == "Read chapter 3").IsDone && Block(memoryBlock.Id).Done, "  ticks the memory to-do and the block");

        // To-do -> block: Home's list (the service it uses) and the memory's panel (its checkbox, as clicked)
        QuickTodoService.SetDone(math.Id, false);
        Check(!Block(homeBlock.Id).Done, "unticking the Home to-do unticks its block");

        main.ShowDayPlan(Today); // something else on screen, then the memory
        Invoke(main, "NavigateTo", studies);
        await Wait(900);
        MemoryTodoPanel panel = Descendants(main).OfType<MemoryTodoPanel>().First();
        CheckBox box = Descendants(panel).OfType<CheckBox>().First(c => c.IsChecked == true);
        ClickCheck(box);
        await Wait(300);
        Check(!Block(memoryBlock.Id).Done, "unticking it in the memory's to-do list unticks its block");

        // A deleted to-do leaves its block, unlinked; deleting a block leaves the to-do.
        QuickTodo spare = QuickTodoService.Add("Water the plants", Today);
        PlanTodo sparePlan = DayPlanTodos.All(Profiles(main), out _).Single(t => t.Link.Id == spare.Id);
        DayPlanBlock spareBlock = DayPlanActions.AddFromTodo(sparePlan, Today, 7 * 60);
        QuickTodoService.Delete(spare.Id);
        Check(Block(spareBlock.Id) != null && DayPlanTodos.Find(DayPlanTodos.All(Profiles(main), out _), Block(spareBlock.Id).Todo) == null,
            "deleting a to-do keeps its block, now unlinked");
        Check(DayPlanActions.ConfirmDelete(memoryBlock), "deleting a linked block");
        MemoryTodoService.TryLoad(studies, out entries);
        Check(entries.Any(t => t.Text == "Read chapter 3"), "  leaves its to-do");
        DayPlanStore.Delete(spareBlock.Id);
        DayPlanStore.Delete(homeBlock.Id);
    }

    static void Reminders()
    {
        Console.WriteLine("Reminders follow their block");
        DayPlanBlock block = new DayPlanBlock
        {
            Id = Guid.NewGuid(), Date = Tomorrow, StartMinute = 10 * 60, EndMinute = 11 * 60, Title = "Dentist", RemindMinutesBefore = 15
        };
        testReminderIds.Add(block.Id);
        Check(DayPlanActions.Save(block, true, null), "a block with a reminder 15 minutes before");
        CheckReminder(block.Id, Tomorrow.AddHours(9).AddMinutes(45), "  scheduled for 09:45 (file and task)");

        Check(DayPlanActions.Move(block, 12 * 60, 13 * 60), "moved to 12:00");
        CheckReminder(block.Id, Tomorrow.AddHours(11).AddMinutes(45), "  the reminder moved to 11:45");

        DayPlanBlock otherDay = Block(block.Id);
        otherDay.Date = Tomorrow.AddDays(1);
        Check(DayPlanActions.Save(otherDay, true, Block(block.Id)), "moved to the day after");
        CheckReminder(block.Id, Tomorrow.AddDays(1).AddHours(11).AddMinutes(45), "  the reminder went along");

        DayPlanBlock off = Block(block.Id);
        off.RemindMinutesBefore = null;
        Check(DayPlanActions.Save(off, false, Block(block.Id)), "reminder unticked");
        CheckNoReminder(block.Id, "  reminder and task removed");

        DayPlanBlock on = Block(block.Id);
        on.RemindMinutesBefore = 0;
        Check(DayPlanActions.Save(on, true, Block(block.Id)), "reminder ticked again");
        Check(DayPlanActions.ConfirmDelete(Block(block.Id)), "the block deleted");
        CheckNoReminder(block.Id, "  its reminder and task went with it");

        // Moved to a time that has passed (today, before now): the reminder goes, with a word about it.
        int now = (int)DateTime.Now.TimeOfDay.TotalMinutes;
        if (now >= 60 && now <= 21 * 60)
        {
            DayPlanBlock soon = new DayPlanBlock { Id = Guid.NewGuid(), Date = Today, StartMinute = (now / 15 + 8) * 15, EndMinute = (now / 15 + 10) * 15, Title = "Later today", RemindMinutesBefore = 0 };
            testReminderIds.Add(soon.Id);
            DayPlanActions.Save(soon, true, null);
            CheckReminder(soon.Id, soon.StartTime(), "a block later today has its reminder");
            int before = answered.Count;
            Check(DayPlanActions.Move(soon, 0, 30), "moved to 00:00, already past");
            CheckNoReminder(soon.Id, "  its reminder was removed");
            Check(answered.Skip(before).Any(a => a.Contains("already passed")), "  and the user was told");
            DayPlanStore.Delete(soon.Id);
        }
        else
            Console.WriteLine("  (skipped the move-into-the-past case: too close to midnight)");

        // reminders.json unreadable: a block that never had a reminder still saves, moves and deletes, without a word.
        string remindersFile = AppPaths.Data("reminders.json");
        string keepReminders = File.Exists(remindersFile) ? File.ReadAllText(remindersFile) : null;
        File.WriteAllText(remindersFile, "{ broken");
        int boxes = answered.Count;
        DayPlanBlock plain = new DayPlanBlock { Id = Guid.NewGuid(), Date = Tomorrow, StartMinute = 600, EndMinute = 660, Title = "No reminder" };
        Check(DayPlanActions.Save(plain, false, null) && DayPlanActions.Move(plain, 660, 720) && DayPlanActions.ConfirmDelete(Block(plain.Id)),
            "with reminders.json unreadable, a block without a reminder saves, moves and deletes");
        Thread.Sleep(600);
        Check(answered.Count == boxes + 1, "  asking only the delete question (" + (answered.Count - boxes) + " box)");
        if (keepReminders == null) File.Delete(remindersFile); else File.WriteAllText(remindersFile, keepReminders);

        // Clock change days: the autumn one ahead of us, and next spring's (02:30 doesn't exist there).
        foreach (DateTime day in ClockChangeDays())
        {
            DayPlanBlock night = new DayPlanBlock { Id = Guid.NewGuid(), Date = day, StartMinute = 150, EndMinute = 210, Title = "Night shift", RemindMinutesBefore = 0 };
            testReminderIds.Add(night.Id);
            Check(DayPlanActions.Save(night, true, null), "clock change day " + day.ToString("yyyy-MM-dd") + ": block 02:30-03:30 with a reminder");
            Reminder r = ReminderManager.LoadReminders().Single(x => x.Id == night.Id);
            // 02:30 doesn't exist when the clocks go forward: reminders.json keeps that moment, which reads 03:30.
            bool missing = TimeZoneInfo.Local.IsInvalidTime(day.AddMinutes(150));
            Check(r.NextFireTime == day.AddMinutes(missing ? 210 : 150), "  reminder at " + (missing ? "02:30, a time that doesn't exist, kept as 03:30" : "02:30")
                + " (" + r.NextFireTime.ToString("yyyy-MM-dd HH:mm") + ")");
            Check(TaskTrigger(night.Id) != null, "  task registered (trigger " + TaskTrigger(night.Id) + ")");
            DayPlanActions.ConfirmDelete(Block(night.Id));
            CheckNoReminder(night.Id, "  removed again");
        }
    }

    static void Search(MainWindow main)
    {
        Console.WriteLine("Search");
        DayPlanBlock block = new DayPlanBlock
        {
            Id = Guid.NewGuid(), Date = Tomorrow, StartMinute = 13 * 60, EndMinute = 15 * 60, Title = "Matematika házi feladat",
            Notes = "Bring the Árvíztűrő book"
        };
        DayPlanStore.Save(block);
        List<SearchResult> index = SearchIndexBuilder.Build(Profiles(main));
        List<SearchResult> byTitle = SearchIndexBuilder.Filter(index, "hazi", 10);
        List<SearchResult> byNotes = SearchIndexBuilder.Filter(index, "ARVIZTURO", 10);
        Check(byTitle.Any(r => r.Kind == SearchResultKind.PlanBlock && r.ItemId == block.Id), "\"hazi\" finds the block by its title");
        Check(byNotes.Any(r => r.Kind == SearchResultKind.PlanBlock && r.ItemId == block.Id), "\"ARVIZTURO\" finds it by its notes");
        SearchResult hit = byTitle.First(r => r.ItemId == block.Id);
        Console.WriteLine("  location: " + hit.Location);
        Invoke(main, "GoToSearchResult", hit);
        Check(Content(main) is DayPlanView view && Field<DateTime>(view, "date") == Tomorrow, "choosing it opens that day's plan");
        DayPlanStore.Delete(block.Id);
    }

    static void Export(MainWindow main)
    {
        Console.WriteLine("Export");
        DayPlanStore.Save(new DayPlanBlock { Id = Guid.NewGuid(), Date = Tomorrow, StartMinute = 480, EndMinute = 540, Title = "Export me", Notes = "with a note", Done = true });
        string folder = System.IO.Path.Combine(AppPaths.DataFolder, "..", "exports");
        Directory.CreateDirectory(folder);
        foreach (ExportFormat format in new[] { ExportFormat.Docx, ExportFormat.Pdf, ExportFormat.Excel })
        {
            string path = System.IO.Path.Combine(folder, "all." + (format == ExportFormat.Excel ? "xlsx" : format.ToString().ToLowerInvariant()));
            ExportService.ExportAll(Profiles(main), path, format);
            Check(File.Exists(path) && new FileInfo(path).Length > 0, format + " export written");
        }
        using (ZipArchive docx = ZipFile.OpenRead(System.IO.Path.Combine(folder, "all.docx")))
        using (StreamReader body = new StreamReader(docx.GetEntry("word/document.xml").Open()))
        {
            string xml = body.ReadToEnd();
            Check(xml.Contains("Day plan") && xml.Contains("Export me (done)") && xml.Contains("08:00–09:00"), "Word: the Day plan group, block line and time");
        }
        using (ClosedXML.Excel.XLWorkbook book = new ClosedXML.Excel.XLWorkbook(System.IO.Path.Combine(folder, "all.xlsx")))
            Check(book.Worksheets.Any(s => s.Name == "Day plan"), "Excel: a Day plan sheet");
        Check(ExportService.HasDayPlan(), "Export all counts the plan as something to export");
    }

    static void BrokenFile(MainWindow main)
    {
        Console.WriteLine("Unreadable dayplan.json");
        string keep = File.ReadAllText(DayPlanStore.FilePath);
        File.WriteAllText(DayPlanStore.FilePath, "{ this isn't json");
        byte[] broken = File.ReadAllBytes(DayPlanStore.FilePath);

        Check(!DayPlanStore.TryLoad(out _), "reads as unreadable");
        Check(!DayPlanStore.Save(new DayPlanBlock { Id = Guid.NewGuid(), Date = Today, StartMinute = 60, EndMinute = 120, Title = "x" }), "a save is refused");
        Check(DayPlanActions.AddFromTodo(new PlanTodo { Text = "x", Link = new TodoLink() }, Today, 60) == null, "adding a to-do is refused (with a message)");
        Check(File.ReadAllBytes(DayPlanStore.FilePath).SequenceEqual(broken), "the file wasn't touched");
        string log = File.Exists(AppPaths.Data("error.log")) ? File.ReadAllText(AppPaths.Data("error.log")) : "";
        Check(log.Contains("dayplan.json"), "error.log names it");

        main.ShowDayPlan(Today);
        DayPlanView view = (DayPlanView)Content(main);
        TextBlock error = (TextBlock)view.FindName("ErrorText");
        Check(error.Visibility == Visibility.Visible && !((Button)view.FindName("AddButton")).IsEnabled, "the plan says so; Add block is off");
        Render(main, "20-plan-unreadable");
        Invoke(main, "NavigateHome");
        HomeView home = (HomeView)Content(main);
        Check(Descendants((StackPanel)home.FindName("PlanRows")).OfType<TextBlock>().Any(t => t.Text == "Couldn't read the day plan."), "Home says so");
        OverlayManager.Open(Overlay.Calendar);
        Check(main.IsCalendarOpen, "the calendar still opens");
        OverlayManager.Close(Overlay.Calendar);
        Check(SearchIndexBuilder.Build(Profiles(main)).Count > 0, "search still builds");
        Check(File.ReadAllBytes(DayPlanStore.FilePath).SequenceEqual(broken), "still untouched after all of that");
        File.WriteAllText(DayPlanStore.FilePath, keep);
    }

    // ---- Layout -----------------------------------------------------------

    static async System.Threading.Tasks.Task EdgeLayouts(MainWindow main)
    {
        Console.WriteLine("Layout edge cases");
        DateTime day = Tomorrow.AddDays(2);
        Guid a = Add(day, 600, 660, "Same time A"), b = Add(day, 600, 660, "Same time B");
        Guid last = Add(day, 23 * 60 + 45, 24 * 60, "Last quarter");
        Add(day, 7 * 60, 8 * 60, new string('W', 40) + " a very long title that keeps going and going without stopping " + new string('x', 200));
        for (int i = 0; i < 20; i++)
            Add(day, 12 * 60 + i * 30, 12 * 60 + i * 30 + 45, "Block " + (i + 1)); // each overlaps the next
        main.WindowState = WindowState.Normal;
        main.Width = 1160;
        main.Height = 760;
        main.ShowDayPlan(day);
        await Wait(900);
        DayPlanView view = (DayPlanView)Content(main);
        Canvas surface = (Canvas)view.FindName("Surface");
        FrameworkElement Card(Guid id) => surface.Children.OfType<FrameworkElement>().Single(c => c.Tag is DayPlanBlock x && x.Id == id);
        Check(Canvas.GetLeft(Card(a)) != Canvas.GetLeft(Card(b)) && Math.Abs(Card(a).Width - Card(b).Width) < 0.5
              && Card(a).Width < surface.ActualWidth / 2, "identical times: side by side, equal width");
        Check(Math.Abs(Canvas.GetTop(Card(last)) - (23 * 60 + 45 + 1)) < 0.5 && Card(last).Height >= 8, "23:45-24:00 sits at the bottom");
        Check(surface.Children.OfType<FrameworkElement>().Count(c => c.Tag is DayPlanBlock) == 24, "24 blocks drawn");
        Console.WriteLine("  summary: " + ((TextBlock)view.FindName("SummaryText")).Text);
        ((ScrollViewer)view.FindName("TimelineScroll")).ScrollToVerticalOffset(11.5 * 60);
        await Wait(400);
        Render(main, "21-crowded-timeline");
        ((RadioButton)view.FindName("ListButton")).IsChecked = true;
        await Wait(500);
        Check(((StackPanel)view.FindName("ListPanel")).Children.OfType<Border>().Count() == 24, "the list has every block");
        Render(main, "22-crowded-list");
        ((RadioButton)view.FindName("TimelineButton")).IsChecked = true;

        foreach (DateTime change in ClockChangeDays())
        {
            Guid night = Add(change, 60, 240, "01:00-04:00 across the clock change");
            Add(change, 120, 180, "02:00-03:00");
            main.ShowDayPlan(change);
            await Wait(700);
            DayPlanView v = (DayPlanView)Content(main);
            Canvas s = (Canvas)v.FindName("Surface");
            FrameworkElement card = s.Children.OfType<FrameworkElement>().Single(c => c.Tag is DayPlanBlock x && x.Id == night);
            Check(Math.Abs(Canvas.GetTop(card) - 61) < 0.5 && Math.Abs(card.Height - 178) < 0.5 && s.Height == 1440,
                change.ToString("yyyy-MM-dd") + ": a 24-hour scale, the block placed by its clock times");
            ((ScrollViewer)v.FindName("TimelineScroll")).ScrollToVerticalOffset(0);
            await Wait(300);
            Render(main, "23-clock-change-" + change.ToString("MM-dd"));
        }
    }

    static async System.Threading.Tasks.Task Narrow(MainWindow main)
    {
        Console.WriteLine("Narrowest window");
        main.Width = main.MinWidth;
        main.Height = main.MinHeight;
        main.ShowDayPlan(Today);
        await Wait(900);
        DayPlanView view = (DayPlanView)Content(main);
        Canvas surface = (Canvas)view.FindName("Surface");
        StackPanel todos = (StackPanel)view.FindName("TodoPanel");
        TextBlock dateText = (TextBlock)view.FindName("DateText");
        Check(new FormattedText(dateText.Text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(dateText.FontFamily, dateText.FontStyle, dateText.FontWeight, dateText.FontStretch), dateText.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(dateText).PixelsPerDip).Width <= dateText.ActualWidth + 1, "the date isn't cut off (" + dateText.Text + ")");
        Check(surface.ActualWidth >= 300 && todos.ActualWidth > 200, "timeline " + (int)surface.ActualWidth + " px, to-dos " + (int)todos.ActualWidth + " px wide");
        Render(main, "24-narrow");
        main.Width = 1160;
        main.Height = 760;
    }

    // ---- Real mouse and keyboard on the timeline --------------------------

    static async System.Threading.Tasks.Task Interactions(MainWindow main, Profile studies)
    {
        Console.WriteLine("Timeline by mouse and keyboard");
        main.Activate();
        await Wait(300);
        if (!InForeground())
        {
            Console.WriteLine("  SKIPPED: TidyMind isn't the foreground window (the session is locked, or another window is in front), "
                + "so real mouse and keyboard input can't reach it");
            return;
        }
        DateTime day = Tomorrow.AddDays(3);
        main.ShowDayPlan(day);
        await Wait(900);
        main.Activate();
        DayPlanView view = (DayPlanView)Content(main);
        Canvas surface = (Canvas)view.FindName("Surface");
        ScrollViewer scroll = (ScrollViewer)view.FindName("TimelineScroll");
        scroll.ScrollToVerticalOffset(7 * 60);
        await Wait(400);

        // Draw 08:00 -> 09:30; the editor opens with those times.
        double x = 58 + (surface.ActualWidth - 68) / 2;

        System.Threading.Tasks.Task drawing = Drag(surface.PointToScreen(new Point(x, 8 * 60 + 3)), surface.PointToScreen(new Point(x, 9 * 60 + 28)));
        DayPlanBlockDialog editor = await WaitFor(() => Application.Current.Windows.OfType<DayPlanBlockDialog>().FirstOrDefault());
        Check(editor != null, "drawing on the timeline opens the editor");
        if (editor != null)
        {
            Check(((ComboBox)editor.FindName("StartHourBox")).SelectedIndex == 8 && ((ComboBox)editor.FindName("EndHourBox")).SelectedIndex == 9
                  && ((ComboBox)editor.FindName("EndMinuteBox")).SelectedIndex == 30, "  with 08:00-09:30");
            ((TextBox)editor.FindName("TitleInput")).Text = "Drawn block";
            await Wait(600); // past its fade-in
            Render(editor, "30-editor");
            Click((Button)editor.FindName("SaveButton"));
        }
        await drawing;
        await Wait(700);
        DayPlanBlock drawn = Blocks(day).SingleOrDefault(b => b.Title == "Drawn block");
        Check(drawn != null && drawn.StartMinute == 480 && drawn.EndMinute == 570, "saved as 08:00-09:30");

        // Move it down an hour (grab the middle), then pull its bottom edge 30 minutes lower, then its top up.
        view = (DayPlanView)Content(main);
        surface = (Canvas)view.FindName("Surface");
        await Drag(surface.PointToScreen(new Point(x, 8 * 60 + 45)), surface.PointToScreen(new Point(x, 9 * 60 + 45)));
        await Wait(700);
        drawn = Block(drawn.Id);
        Check(drawn.StartMinute == 540 && drawn.EndMinute == 630, "dragged down an hour: 09:00-10:30 (" + drawn.TimeRange() + ")");
        view = (DayPlanView)Content(main);
        surface = (Canvas)view.FindName("Surface");
        await Drag(surface.PointToScreen(new Point(x, 630 - 1)), surface.PointToScreen(new Point(x, 660 - 1)));
        await Wait(700);
        drawn = Block(drawn.Id);
        Check(drawn.StartMinute == 540 && drawn.EndMinute == 660, "bottom edge pulled down: 09:00-11:00 (" + drawn.TimeRange() + ")");
        view = (DayPlanView)Content(main);
        surface = (Canvas)view.FindName("Surface");
        await Drag(surface.PointToScreen(new Point(x, 540 + 3)), surface.PointToScreen(new Point(x, 510 + 3)));
        await Wait(700);
        drawn = Block(drawn.Id);
        Check(drawn.StartMinute == 510 && drawn.EndMinute == 660, "top edge pulled up: 08:30-11:00 (" + drawn.TimeRange() + ")");

        // A to-do dragged from the panel onto 14:00.
        view = (DayPlanView)Content(main);
        surface = (Canvas)view.FindName("Surface");
        scroll = (ScrollViewer)view.FindName("TimelineScroll");
        scroll.ScrollToVerticalOffset(12 * 60);
        await Wait(400);
        StackPanel todoPanel = (StackPanel)view.FindName("TodoPanel");
        Border essay = todoPanel.Children.OfType<Border>().First(r => Descendants(r).OfType<TextBlock>().Any(t => t.Text == "Árvíztűrő essay"));
        Point grab = essay.PointToScreen(new Point(30, essay.ActualHeight / 2));
        await Drag(grab, surface.PointToScreen(new Point(x, 14 * 60 + 5)), steps: 30);
        await Wait(800);
        DayPlanBlock dropped = Blocks(day).SingleOrDefault(b => b.Title == "Árvíztűrő essay");
        Check(dropped != null && dropped.StartMinute == 14 * 60 && dropped.EndMinute == 15 * 60 && dropped.Todo?.Source == TodoSource.Memory,
            "a dragged to-do becomes a linked block at 14:00-15:00 (" + dropped?.TimeRange() + ")");

        // Add to plan, for the keyboard
        view = (DayPlanView)Content(main);
        todoPanel = (StackPanel)view.FindName("TodoPanel");
        Border pack = todoPanel.Children.OfType<Border>().FirstOrDefault(r => Descendants(r).OfType<TextBlock>().Any(t => t.Text == "Read chapter 3"));
        Click(Descendants(pack).OfType<Button>().First());
        await Wait(600);
        DayPlanBlock planned = Blocks(day).SingleOrDefault(b => b.Title == "Read chapter 3");
        Check(planned != null && planned.StartMinute == 9 * 60, "Add to plan: 09:00 on a day other than today");
        Render(main, "31-after-drags");

        // Keyboard: Enter edits, Delete deletes (confirmed by the watcher).
        view = (DayPlanView)Content(main);
        surface = (Canvas)view.FindName("Surface");
        FrameworkElement card = surface.Children.OfType<FrameworkElement>().Single(c => c.Tag is DayPlanBlock b && b.Id == planned.Id);
        card.Focus();
        await Wait(200);
        System.Threading.Tasks.Task enter = Keys(0x0D);
        DayPlanBlockDialog edit = await WaitFor(() => Application.Current.Windows.OfType<DayPlanBlockDialog>().FirstOrDefault());
        Check(edit != null && ((TextBox)edit.FindName("TitleInput")).Text == "Read chapter 3", "Enter opens the editor on that block");
        edit?.Close();
        await enter;
        await Wait(500);
        view = (DayPlanView)Content(main);
        surface = (Canvas)view.FindName("Surface");
        card = surface.Children.OfType<FrameworkElement>().Single(c => c.Tag is DayPlanBlock b && b.Id == planned.Id);
        card.Focus();
        await Wait(200);
        await Keys(0x2E);
        await Wait(700);
        Check(Block(planned.Id) == null, "Delete removes it (after Yes)");

        // List view: tick done there; the linked memory to-do follows.
        view = (DayPlanView)Content(main);
        ((RadioButton)view.FindName("ListButton")).IsChecked = true;
        await Wait(500);
        Border row = ((StackPanel)view.FindName("ListPanel")).Children.OfType<Border>().Single(r => r.Tag is DayPlanBlock b && b.Id == dropped.Id);
        CheckBox done = Descendants(row).OfType<CheckBox>().First();
        ClickCheck(done);
        await Wait(600);
        MemoryTodoService.TryLoad(studies, out List<TodoEntry> entries);
        Check(Block(dropped.Id).Done && entries.Single(t => t.Text == "Árvíztűrő essay").IsDone, "ticked in the list: the block and its to-do are done");
        view = (DayPlanView)Content(main);
        Render(main, "32-list");
        ((RadioButton)view.FindName("TimelineButton")).IsChecked = true;
        await Wait(300);
    }

    static async System.Threading.Tasks.Task CalendarAndHome(MainWindow main)
    {
        Console.WriteLine("Calendar and Home");
        // Today: one block that has ended, two still ahead (if the clock allows).
        int now = (int)DateTime.Now.TimeOfDay.TotalMinutes;
        if (now >= 60)
            Add(Today, 0, 30, "Already over");
        Add(Today, Math.Min(now / 15 * 15 + 15, 1380), Math.Min(now / 15 * 15 + 75, 1440), "Next thing");
        Add(Today, 1380, 1440, "Last of the day");

        OverlayManager.Open(Overlay.Calendar);
        await Wait(700);
        CalendarPanel calendar = Descendants(main).OfType<CalendarPanel>().First();
        UniformGrid grid = (UniformGrid)calendar.FindName("DayGrid");
        Border todayCell = grid.Children.OfType<Border>().First(c => c.BorderThickness.Left > 0 && Descendants(c).OfType<TextBlock>().Any(t => t.Text == Today.Day.ToString() && t.FontWeight == FontWeights.Bold));
        Check(Descendants(todayCell).OfType<Rectangle>().Any(r => r.Width == 8 && r.Height == 3), "today's cell has the plan mark");
        Check(Descendants((StackPanel)calendar.FindName("MonthList")).OfType<TextBlock>().Any(t => t.Text.StartsWith("Day plan: ")), "the month list names the planned days");
        Render(main, "40-calendar-marker");

        // Clicking the day: its dialog offers the plan.
        _ = main.Dispatcher.BeginInvoke(new System.Action(() => calendar.GetType().GetMethod("EditDay", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(calendar, new object[] { Today })));
        DayEditDialog dayDialog = await WaitFor(() => Application.Current.Windows.OfType<DayEditDialog>().FirstOrDefault());
        Button openPlan = (Button)dayDialog.FindName("OpenPlanButton");
        Console.WriteLine("  day dialog link: " + openPlan.Content);
        Render(dayDialog, "41-day-dialog");
        Click(openPlan);
        await Wait(900);
        Check(!main.IsCalendarOpen && Content(main) is DayPlanView v && Field<DateTime>(v, "date") == Today, "Open the day plan: calendar closed, today's plan shown");
        Check(!calendar.IsKeyboardFocusWithin, "the focus isn't left in the closed calendar");

        Invoke(main, "NavigateHome");
        await Wait(700);
        HomeView home = (HomeView)Content(main);
        StackPanel rows = (StackPanel)home.FindName("PlanRows");
        List<string> texts = Descendants(rows).OfType<TextBlock>().Select(t => t.Text).ToList();
        Console.WriteLine("  Home's plan: " + string.Join(" | ", texts));
        Check(texts.Contains("Next thing") && !texts.Contains("Already over"), "Home shows what's still ahead, not what's over");
        Check(!Descendants((StackPanel)home.FindName("Daybook")).OfType<TextBlock>().Any(t => t.Text == "Nothing planned for today."), "the week doesn't call a planned today empty");
        ((FrameworkElement)home.FindName("PlanRows")).BringIntoView();
        await Wait(300);
        Render(main, "42-home-card");
        Button link = (Button)home.FindName("OpenPlanLink");
        Click(link);
        await Wait(500);
        Check(Content(main) is DayPlanView, "its link opens the plan");
        Render(main, "43-today-plan");
    }

    static async System.Threading.Tasks.Task TourStep(MainWindow main)
    {
        Console.WriteLine("Tour: Plan your day");
        Invoke(main, "NavigateHome");
        await Restart(main);
        await ToStep("Plan your day");
        CheckHoleOn(main, GuidedTour.Find(main, "NavDayPlan"), "spotlight on the Day Plan item");
        Render(main, "50-tour-dayplan");
        main.ShowDayPlan(Today); // what clicking the item does
        await Wait(900);
        Check(GuidedTour.Current != null && Step().Title == "Notes, reminders and calendar", "opening it moves the tour on");
        Choose(TourChoice.Next);
        await Wait(700);
        CheckHoleOn(main, GuidedTour.Find(main, "HelpButton"), "the end step still points at the ? button");
        Choose(TourChoice.Finish);
        await Wait(500);
        Check(GuidedTour.Current == null, "the tour finishes");
    }

    // A backup of today's data restores with its plan; one made before the Day Plan existed restores without one.
    static void BackupRestore()
    {
        Console.WriteLine("Backup and restore");
        int blocks = DayPlanStore.TryLoad(out DayPlanFile plan) ? plan.Blocks.Count : -1;
        string parent = System.IO.Path.GetDirectoryName(AppPaths.DataFolder);
        string withPlan = System.IO.Path.Combine(parent, "with-plan.zip");
        DataBackup.Create(withPlan);
        using (ZipArchive zip = ZipFile.OpenRead(withPlan))
            Check(zip.GetEntry("dayplan.json") != null, "the backup has dayplan.json");

        // "Before the feature": the same backup without dayplan.json.
        string before = System.IO.Path.Combine(parent, "before-day-plan.zip");
        File.Copy(withPlan, before, true);
        using (ZipArchive zip = ZipFile.Open(before, ZipArchiveMode.Update))
            zip.GetEntry("dayplan.json").Delete();

        DataBackup.Restore(before, out string safety1);
        Check(!File.Exists(DayPlanStore.FilePath) && DayPlanStore.TryLoad(out DayPlanFile empty) && empty.Blocks.Count == 0,
            "restoring a backup from before the Day Plan: no plan, no error");
        DataBackup.Restore(withPlan, out string safety2);
        Check(DayPlanStore.TryLoad(out DayPlanFile back) && back.Blocks.Count == blocks, "restoring the new one brings all " + blocks + " blocks back");
    }

    // ---- Helpers ----------------------------------------------------------

    static Profile AddProjectMemory(MainWindow main, string name)
    {
        List<Profile> profiles = Profiles(main);
        Profile memory = new Profile { Id = Guid.NewGuid(), Name = name, Type = ProfileType.Project, Color = "#3498DB", Order = profiles.Count };
        profiles.Add(memory);
        ProfileManager.TrySaveProfiles(profiles);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(MemoryFiles.Todos(memory)));
        Invoke(main, "BuildNav");
        return memory;
    }

    static Guid Add(DateTime day, int start, int end, string title)
    {
        DayPlanBlock block = new DayPlanBlock { Id = Guid.NewGuid(), Date = day, StartMinute = start, EndMinute = end, Title = title };
        DayPlanStore.Save(block);
        return block.Id;
    }

    static List<DayPlanBlock> Blocks(DateTime day)
    {
        DayPlanStore.TryLoad(out DayPlanFile plan);
        return DayPlanStore.BlocksOn(plan, day);
    }

    static DayPlanBlock Block(Guid id)
    {
        DayPlanStore.TryLoad(out DayPlanFile plan);
        return plan.Blocks.SingleOrDefault(b => b.Id == id);
    }

    // The window's own drawing (not the screen), so a capture never catches it half painted.
    static void Render(Window window, string name)
    {
        window.UpdateLayout();
        FrameworkElement content = (FrameworkElement)window.Content;
        DpiScale dpi = VisualTreeHelper.GetDpi(content);
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(content.ActualWidth * dpi.DpiScaleX),
            (int)(content.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        DrawingVisual page = new DrawingVisual();
        using (DrawingContext dc = page.RenderOpen())
            dc.DrawRectangle((Brush)Application.Current.FindResource("BgMain"), null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
        bitmap.Render(page);
        bitmap.Render(content);
        var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
        png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(shots);
        using (FileStream file = File.Create(System.IO.Path.Combine(shots, name + ".png")))
            png.Save(file);
    }

    // What a mouse click on a checkbox does: it turns, then Click runs.
    static void ClickCheck(CheckBox box)
    {
        box.IsChecked = box.IsChecked != true;
        box.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, box));
    }

    static object Content(MainWindow main) => ((ContentControl)main.FindName("ContentArea")).Content;

    static void Invoke(object o, string method, params object[] args) =>
        o.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).Invoke(o, args);

    // This PC's clock change days: the next autumn one and the spring after (local time zone rules).
    static List<DateTime> ClockChangeDays()
    {
        List<DateTime> days = new List<DateTime>();
        TimeZoneInfo zone = TimeZoneInfo.Local;
        for (DateTime d = Today; d < Today.AddDays(400) && days.Count < 2; d = d.AddDays(1))
        {
            // The clocks change in the small hours, so noon is on the other side of it from the noon before.
            if (zone.IsDaylightSavingTime(d.AddHours(12)) != zone.IsDaylightSavingTime(d.AddDays(-1).AddHours(12)))
                days.Add(d);
        }
        return days;
    }

    static void CheckReminder(Guid id, DateTime when, string what)
    {
        Reminder reminder = ReminderManager.LoadReminders().SingleOrDefault(r => r.Id == id);
        DateTime? trigger = TaskTrigger(id);
        Check(reminder != null && reminder.NextFireTime == when && reminder.RepeatType == RepeatType.Once && trigger == when,
            what + " [file " + reminder?.NextFireTime.ToString("MM-dd HH:mm") + ", task " + trigger?.ToString("MM-dd HH:mm") + "]");
    }

    static void CheckNoReminder(Guid id, string what) =>
        Check(!ReminderManager.LoadReminders().Any(r => r.Id == id) && TaskTrigger(id) == null, what);

    static DateTime? TaskTrigger(Guid id)
    {
        using (TaskService service = new TaskService())
        using (Microsoft.Win32.TaskScheduler.Task task = service.GetTask("TidyMind_Reminder_" + id))
            return task?.Definition.Triggers.OfType<TimeTrigger>().FirstOrDefault()?.StartBoundary;
    }

    static List<string> ReminderTaskNames()
    {
        using (TaskService service = new TaskService())
            return service.RootFolder.Tasks.Select(t => t.Name).Where(n => n.StartsWith("TidyMind_Reminder_")).ToList();
    }

    // Whatever this run scheduled goes, whatever the tests did.
    static void CleanUpReminders()
    {
        foreach (Guid id in testReminderIds)
        {
            try { ReminderManager.DeleteReminder(id); } catch (Exception e) { Console.WriteLine("  cleanup: " + e.Message); }
        }
    }

    static async System.Threading.Tasks.Task<T> WaitFor<T>(Func<T> find, int ms = 4000) where T : class
    {
        for (int waited = 0; waited < ms; waited += 100)
        {
            T found = find();
            if (found != null)
                return found;
            await Wait(100);
        }
        return null;
    }

    // ---- Input --------------------------------------------------------------

    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);

    // Press at `from`, move in steps, release at `to` (screen pixels), from a background thread: the UI thread may
    // be inside DoDragDrop or a dialog meanwhile.
    static System.Threading.Tasks.Task Drag(Point from, Point to, int steps = 14) => System.Threading.Tasks.Task.Run(() =>
    {
        SetCursorPos((int)from.X, (int)from.Y);
        Thread.Sleep(120);
        mouse_event(0x02, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(120);
        for (int i = 1; i <= steps; i++)
        {
            SetCursorPos((int)(from.X + (to.X - from.X) * i / steps), (int)(from.Y + (to.Y - from.Y) * i / steps));
            Thread.Sleep(30);
        }
        Thread.Sleep(150);
        mouse_event(0x04, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(200);
    });

    static System.Threading.Tasks.Task Keys(byte key) => System.Threading.Tasks.Task.Run(() =>
    {
        keybd_event(key, 0, 0, UIntPtr.Zero);
        Thread.Sleep(40);
        keybd_event(key, 0, 2, UIntPtr.Zero);
        Thread.Sleep(150);
    });

    // Real input reaches the app only while one of its windows is in the foreground (never while the session is locked).
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();

    static bool InForeground()
    {
        IntPtr front = GetForegroundWindow();
        if (front == IntPtr.Zero)
            return false;
        GetWindowThreadProcessId(front, out uint pid);
        return pid == (uint)Environment.ProcessId;
    }

    // ---- Message boxes ----------------------------------------------------

    delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] static extern IntPtr GetDlgItem(IntPtr hwnd, int id);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    static void StartAnswering()
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
                    StringBuilder title = new StringBuilder(256), text = new StringBuilder(2048);
                    GetWindowText(hwnd, title, 256);
                    GetWindowText(GetDlgItem(hwnd, 0xFFFF), text, 2048);
                    // Yes if there is one; an OK-only box's button has the Cancel id (2).
                    int id = new[] { 6, 1, 2 }.First(i => GetDlgItem(hwnd, i) != IntPtr.Zero || i == 2);
                    lock (answered)
                    {
                        if (answered.Count == 0 || !answered[answered.Count - 1].StartsWith(title + ": " + text.ToString().Replace("\n", " ")))
                            answered.Add(title + ": " + text.ToString().Replace("\n", " ") + " -> " + (id == 6 ? "Yes" : "OK"));
                    }
                    PostMessage(GetDlgItem(hwnd, id), 0x00F5, IntPtr.Zero, IntPtr.Zero); // BM_CLICK
                    return true;
                }, IntPtr.Zero);
                Thread.Sleep(250);
            }
        }) { IsBackground = true };
        watcher.Start();
    }
}
