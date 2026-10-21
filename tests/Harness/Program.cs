using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TidyMind;

// Test harness for TidyMind. Every run points the app's data folder at a temp folder first (and stops if that didn't
// work), so the real %LOCALAPPDATA%\TidyMind is never read or written.
//   Harness toast <dataFolder>            fires one reminder through ReminderManager.FireReminder
//   Harness ui <dataFolder> <shotsFolder> the first-run card, the guided tour, Tips & shortcuts, What's new, the ⋯ menus and hint lines, Home's tiles
//   Harness export <dataFolder> <outFolder> every PDF, Word and Excel export in en/hu/sr (PDF pages rendered to PNG, Office files validated)
//   Harness update <dataFolder> <shotsFolder> the in-app update against a local fake release server
//   Harness tasks <dataFolder> <shotsFolder> Project Details as one scrolling page (5, 60, 250, 500 tasks)
//   Harness select <dataFolder> <shotsFolder> selecting several items (Ctrl, Shift, Ctrl+A) and deleting them at once
//   Harness bgshot <dataFolder> <shotsFolder> a collection on a busy background picture at three visibility settings
//   Harness tint <dataFolder> <shotsFolder> Adaptive card tint: sampling, contrast, light and dark photos, the switch
//   Harness pictures <dataFolder> <shotsFolder> several pictures per collection item: data, form, delete safety, card, transfer
//   Harness quicknote <dataFolder> <shotsFolder> the Quick note: not topmost, behind other apps, focus given back on close
//   Harness vehicle <dataFolder> <shotsFolder> the vehicle record: old data untouched, the window, documents and reminders, odometer, transfer, exports
//   Harness undo <dataFolder> <shotsFolder> Ctrl+Z / Ctrl+Y for data changes: every kind undone byte for byte, files and reminders, keys, toast
//   Harness notebook <dataFolder> <shotsFolder> a memory's own notebook: pages, old data, failures, backup, export, transfer, delete
static partial class Program
{
    static int failures;
    static string shots;
    static string mode, uiCulture;

    [STAThread]
    static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (args.Length == 2 && args[0] == "--test-notification")
            return RunScheduledTest(args[1]);
        // Anything else (e.g. a "--remind" from a task left behind by a crashed run) does nothing.
        string[] modes = { "toast", "ui", "plan", "loc", "locfirst", "locdata1", "locdata2", "probe", "taskpath", "export", "update", "tasks", "select", "bgshot", "transfer", "tint", "pictures", "quicknote", "vehicle", "items", "undo", "notebook" };
        if (args.Length < 2 || !modes.Contains(args[0]))
            return 2;
        string data = Path.GetFullPath(args[1]);
        string real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TidyMind");
        if (data.StartsWith(real, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to run against the real data folder.");
        Directory.CreateDirectory(data);
        Redirect(data);
        PointUpdatesNowhere(data);

        // The older suites read English texts: they run in English, whatever this PC's display language. The first
        // launch test starts without a choice, as the given Windows display language.
        string settingsFile = Path.Combine(data, "settings.json");
        if (args[0] == "locfirst")
            Thread.CurrentThread.CurrentUICulture = new CultureInfo(args[2]);
        else if (args[0] == "toast")
            File.WriteAllText(settingsFile, "{\"Language\":\"" + (args.Length > 2 ? args[2] : "en") + "\"}");
        else if (args[0] != "locdata2" && !File.Exists(settingsFile))
            File.WriteAllText(settingsFile, "{\"Language\":\"en\"}");
        Console.WriteLine("Windows display language: " + CultureInfo.InstalledUICulture.Name + ", thread UI culture: " + CultureInfo.CurrentUICulture.Name);

        mode = args[0];
        uiCulture = args.Length > 2 ? args[2] : null;
        int result = args[0] == "taskpath" ? TaskPath() : args[0] == "probe" ? Probe() : args[0] == "toast" ? Toast(data) : Ui(args[0] == "locfirst" || args[0].StartsWith("locdata") ? Path.Combine(data, "..", "shots") : args[2], args[0] == "plan");
        Console.WriteLine(failures == 0 ? "ALL PASSED" : failures + " FAILED");
        return failures == 0 ? result : 1;
    }

    // AppPaths.DataFolder is a static get-only property; its backing field is set before anything else reads it.
    static void Redirect(string folder)
    {
        FieldInfo field = typeof(AppPaths).GetField("<DataFolder>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic);
        DynamicMethod set = new DynamicMethod("SetDataFolder", null, new[] { typeof(string) }, typeof(AppPaths).Module, true);
        ILGenerator il = set.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Stsfld, field);
        il.Emit(OpCodes.Ret);
        ((Action<string>)set.CreateDelegate(typeof(Action<string>)))(folder);
        if (AppPaths.DataFolder != folder || !AppPaths.Data("x").StartsWith(folder))
            throw new InvalidOperationException("The data folder couldn't be redirected: " + AppPaths.DataFolder);
        Console.WriteLine("data folder: " + AppPaths.DataFolder);
    }

    static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what);
        if (!ok) failures++;
    }

    // ---- Toast ----------------------------------------------------------

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    static int Toast(string data)
    {
        // Like the "--remind" process: no process ID of its own, the notification names App.UserModelId.
        Guid id = Guid.NewGuid();
        Reminder reminder = new Reminder
        {
            Id = id,
            Title = "Harness <b>test</b> & \"quotes\"",
            Message = "Sent by the TidyMind test harness.",
            NextFireTime = DateTime.Now.AddMinutes(-1),
            RepeatType = RepeatType.Once
        };
        File.WriteAllText(Path.Combine(data, "reminders.json"), JsonSerializer.Serialize(new List<Reminder> { reminder }));

        Loc.Initialize(); // as App does before a reminder fires
        Console.WriteLine("language: " + Loc.Current + ", the notification's title should read: "
            + Loc.F("Reminder.Toast.TitleFor", reminder.Title));
        DateTime start = DateTime.Now;
        ReminderManager.FireReminder(id);
        Console.WriteLine("FireReminder took " + (DateTime.Now - start).TotalSeconds.ToString("0.0") + " s");
        Console.WriteLine("reminders.log:");
        Console.WriteLine(File.ReadAllText(Path.Combine(data, "reminders.log")));

        // A banner times out after about 5 s; a notification that stays in the Action Center is still in the history.
        Thread.Sleep(10000);
        var history = Windows.UI.Notifications.ToastNotificationManager.History.GetHistory(App.UserModelId);
        Console.WriteLine("Action Center history for " + App.UserModelId + ": " + history.Count);
        foreach (var toast in history)
            Console.WriteLine("  " + toast.Content.GetXml());
        // The test notification doesn't stay in this PC's Action Center.
        Windows.UI.Notifications.ToastNotificationManager.History.Clear(App.UserModelId);
        return 0;
    }

    // ---- UI ---------------------------------------------------------------

    static int Ui(string shotsFolder, bool plan)
    {
        shots = shotsFolder;
        Directory.CreateDirectory(shots);
        // The app's task check (RepairTasks) removes TidyMind_Reminder_* tasks that the temp reminders.json doesn't
        // know: never run where real ones exist.
        // (The restart phase of the data test finds only the one task its first phase made for this temp folder.)
        HashSet<string> ours = mode == "locdata2"
            ? new HashSet<string>(ReminderManager.TryLoadReminders().Select(r => "TidyMind_Reminder_" + r.Id))
            : new HashSet<string>();
        if (ReminderTaskNames().Any(n => !ours.Contains(n)))
            throw new InvalidOperationException("Real TidyMind_Reminder_* tasks exist on this PC; not running.");

        // The app's own start (App.OnStartup, run once the dispatcher runs): migration, task check, MainWindow.
        App app = new App();
        app.InitializeComponent();

        Dispatcher.CurrentDispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                MainWindow main = (MainWindow)Application.Current.MainWindow;
                Check(Application.Current.Windows.OfType<MainWindow>().Count() == 1, "one main window, from App.OnStartup");
                switch (mode)
                {
                    case "plan": await PlanScenarios(main); break;
                    case "loc": await LocScenarios(main); break;
                    case "locfirst": await LocFirstScenario(main, uiCulture); break;
                    case "locdata1": await LocData1(main); break;
                    case "locdata2": await LocData2(main); break;
                    case "export": await ExportScenarios(main); break;
                    case "update": await UpdateScenarios(main); break;
                    case "tasks": await TaskListScenarios(main); break;
                    case "select": await SelectScenarios(main); break;
                    case "transfer": await TransferScenarios(main); break;
                    case "bgshot": await BackgroundShots(main); break;
                    case "tint": await TintScenarios(main); break;
                    case "pictures": await PictureScenarios(main); break;
                    case "quicknote": await QuickNoteScenarios(main); break;
                    case "vehicle": await VehicleScenarios(main); break;
                    case "items": await ItemShots(main); break;
                    case "undo": await UndoScenarios(main); break;
                    case "notebook": await NotebookScenarios(main); break;
                    default: await Scenarios(main); break;
                }
            }
            catch (Exception e)
            {
                Check(false, "harness threw: " + e);
            }
            finally
            {
                foreach (Window w in Application.Current.Windows.OfType<Window>().ToList())
                    w.Close();
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        }), DispatcherPriority.ApplicationIdle);
        Dispatcher.Run();
        return 0;
    }

    // The first-run card, the tour (every step on its element, Next, Finish, Esc, a missing element skipped, the smallest
    // window, the three languages), the examples, Tips & shortcuts, What's new, the new visible ways in, then Home.
    static async Task Scenarios(MainWindow main)
    {
        await Wait(1200);
        string[] keys = { "Memories", "NewMemory", "Todos", "Corner", "Search", "End" };
        string[] targets = { "NavScroll", "NewButtons", "AddRow", "OverlayButtons", "NavSearch", "HelpButton" };

        Console.WriteLine("Welcome card (fresh data folder)");
        Check(GuidedTour.Current != null && Steps().Count == 1, "the welcome card is up");
        Check(CalloutTexts().Contains(Loc.T("Tour.Welcome.Title")) && CalloutButtons().Contains(Loc.T("Examples.Add")),
            "it offers the tour, the example memories and Not now");
        RenderWindow(main, "01-welcome");
        Choose(TourChoice.Begin);
        await Wait(1200);
        Check(GuidedTour.Current != null && Steps().Select(s => s.Key).SequenceEqual(keys), "Start the tour: the six steps");
        Check(Profiles(main).Count == 0, "no memory was added: the tour works on empty data");
        Check(Content(main) is HomeView, "Home is shown");

        Console.WriteLine("Every step on its element, in order");
        for (int i = 0; i < keys.Length; i++)
        {
            Check(Step().Key == keys[i], "step " + (i + 1) + ": " + Step().Key);
            CheckHoleOn(main, Find(main, targets[i]), keys[i] + ": spotlight on " + targets[i]);
            Check(GuidedTour.OnScreen(Find(main, targets[i])), keys[i] + ": its element is wholly on screen");
            Check(CalloutDots() == keys.Length, keys[i] + ": " + keys.Length + " dots");
            RenderWindow(main, "02-step-" + (i + 1) + "-" + keys[i]);
            if (i < keys.Length - 1)
            {
                Choose(TourChoice.Next);
                await Wait(500);
            }
        }
        Check(CalloutButtons().Contains(Loc.T("Tour.Button.Finish")), "the last step ends with Finish");
        Choose(TourChoice.Finish);
        await Wait(500);
        CheckTourGone(main, "after Finish");
        Check(AppSettings.Load().Tour == AppSettings.TourCompleted, "settings: tour completed");

        Console.WriteLine("Esc skips at any step");
        await Restart(main);
        Choose(TourChoice.Next);
        await Wait(400);
        PressEscape(main);
        await Wait(500);
        CheckTourGone(main, "after Esc");

        Console.WriteLine("A step whose element is missing is skipped");
        await Restart(main);
        FrameworkElement search = Find(main, "NavSearch");
        search.Visibility = Visibility.Collapsed;
        await Wait(300);
        await ToStepKey("Corner");
        Check(CalloutDots() == keys.Length - 1, "the dots count five steps");
        Choose(TourChoice.Next);
        await Wait(500);
        Check(Step().Key == "End", "Next goes past Search to the last step");
        search.Visibility = Visibility.Visible;
        GuidedTour.Current?.Stop(TourEnd.Skipped);
        await Wait(300);

        Console.WriteLine("A click through the spotlight that changes the window");
        await Restart(main);
        main.ShowDayPlan(DateTime.Today); // as a click on Day Plan inside the sidebar's spotlight
        await Wait(300);
        await ToStepKey("Todos");
        Check(Step().Key == "Todos" && Content(main) is HomeView, "the to-dos step shows Home again instead of being skipped");
        await ToStepKey("Corner");
        OverlayManager.Route(Overlay.Calendar); // as a click on 📅 inside the spotlight
        await Wait(600);
        Choose(TourChoice.Next);
        await Wait(500);
        Check(Step().Key == "Search" && !main.IsCalendarOpen, "the next step closes the calendar that covered it");
        TextBox todoBox = (TextBox)Find(main, "Input");
        todoBox.Focus();
        PressEscape(main);
        await Wait(400);
        CheckTourGone(main, "after Esc in the empty to-do box");

        Console.WriteLine("The smallest window");
        main.Width = main.MinWidth;
        main.Height = main.MinHeight;
        await Wait(600);
        await Restart(main);
        List<string> shown = new List<string>();
        for (int guard = 0; GuidedTour.Current != null && guard < 10; guard++)
        {
            shown.Add(Step().Key);
            FrameworkElement target = Find(main, targets[Array.IndexOf(keys, Step().Key)]);
            Check(GuidedTour.OnScreen(target), "small window, " + Step().Key + ": its element is wholly on screen");
            if (shown.Count == 1)
                RenderWindow(main, "03-small-window");
            Choose(Step().OwnButtons ? Step().PrimaryChoice : TourChoice.Next);
            await Wait(400);
        }
        Console.WriteLine("  shown at " + main.ActualWidth + "×" + main.ActualHeight + ": " + string.Join(", ", shown));
        Check(GuidedTour.Current == null && shown.Count >= 4 && shown.Last() == "End", "the tour runs to its end in the smallest window");
        main.Width = 1160;
        main.Height = 720;
        await Wait(500);

        Console.WriteLine("Hungarian and Serbian");
        foreach (string code in new[] { Loc.Hungarian, Loc.SerbianLatin })
        {
            Loc.Switch(code);
            await Restart(main);
            Check(CalloutTexts().Contains(Loc.T("Tour.Memories.Title")) && CalloutButtons().Contains(Loc.T("Tour.Button.Skip")),
                code + ": the step reads in " + code);
            await ToStepKey("Corner");
            CheckHoleOn(main, Find(main, "OverlayButtons"), code + ": spotlight on the corner buttons");
            RenderWindow(main, "04-tour-" + code);
            GuidedTour.Current?.Stop(TourEnd.Skipped);
            await Wait(300);
        }
        Loc.Switch(Loc.English);
        await Wait(300);

        await Examples(main);
        await Checklist(main);
        await TipsPage(main);
        await WhatsNewNote(main);
        await VisibleWaysIn(main);
        await Home(main);
    }

    // The first-run card's (and the ? menu's) example memories.
    static async Task Examples(MainWindow main)
    {
        Console.WriteLine("Example memories");
        Popup help = (Popup)main.FindName("HelpPopup");
        help.IsOpen = true;
        await Wait(300);
        Check(((Button)main.FindName("AddExamplesLink")).IsVisible, "the ? menu offers the examples while there are none");
        help.IsOpen = false;
        Invoke(main, "TourEnded", TourEnd.Examples); // what the welcome card's link does
        await Wait(800);
        Check(WelcomeMemory.Exists(Profiles(main)) && Content(main) is ProjectView, "added, and the example project memory is shown");
        help.IsOpen = true;
        await Wait(300);
        Check(!((Button)main.FindName("AddExamplesLink")).IsVisible, "the ? menu no longer offers them");
        help.IsOpen = false;
    }

    // Home's Getting started: ticked from the user's own data (not the examples'), hidden with Hide, back from ?.
    static async Task Checklist(MainWindow main)
    {
        Console.WriteLine("Getting started");
        HomeView Home() => (HomeView)Content(main);
        FrameworkElement Section() => (FrameworkElement)Home().FindName("ChecklistSection");
        string Lede() => ((TextBox)Home().FindName("ChecklistLede")).Text;
        Invoke(main, "NavigateHome");
        await Wait(500);
        Check(Section().IsVisible && Lede() == Loc.F("Checklist.Progress", 0, 4) && ((Panel)Home().FindName("ChecklistRows")).Children.Count == 4,
            "shown with four steps, none done: the example memories don't count (" + Lede() + ")");

        Profile mine = AddMemory(main, "My plans", ProfileType.Project);
        File.WriteAllText(MemoryFiles.Items(mine), JsonSerializer.Serialize(new List<Project>
        {
            new Project { Id = Guid.NewGuid(), Name = "Move house", Tasks = new List<TaskItem> { new TaskItem { Title = "Book a van" } } }
        }));
        Profile things = AddMemory(main, "My things", ProfileType.Collection);
        File.WriteAllText(MemoryFiles.Items(things), JsonSerializer.Serialize(new List<Entity> { new Entity { Id = Guid.NewGuid(), Name = "Lamp" } }));
        Invoke(main, "NavigateHome");
        await Wait(500);
        bool[] done = GettingStarted.Done(Profiles(main));
        Check(done.SequenceEqual(new[] { true, true, false, true }) && Lede() == Loc.F("Checklist.Progress", 3, 4),
            "a project, a task and an item of the user's own: ticked (" + Lede() + ")");
        Home().BringChecklistIntoView();
        await Wait(400);
        RenderWindow(main, "09-getting-started");

        // A reminder (straight from the file: one shown on Home would get a Task Scheduler task).
        File.WriteAllText(AppPaths.Data("reminders.json"), JsonSerializer.Serialize(new List<Reminder>
        {
            new Reminder { Id = Guid.NewGuid(), Title = "", Message = "Checklist", NextFireTime = DateTime.Today.AddDays(3), RepeatType = RepeatType.Once }
        }));
        Check(GettingStarted.Done(Profiles(main))[2], "a reminder: ticked");
        File.Delete(AppPaths.Data("reminders.json"));
        bool[] all = { true, true, true, true };
        AppSettings.Update(s => s.Checklist = null);
        Check(!GettingStarted.Shown(all) && GettingStarted.Shown(done), "it goes once all four are done");

        Click((Button)Home().FindName("ChecklistHide"));
        await Wait(300);
        Check(!Section().IsVisible && AppSettings.Load().Checklist == AppSettings.ChecklistHidden, "Hide puts it away");
        Invoke(main, "NavigateHome");
        await Wait(400);
        Check(!Section().IsVisible, "and it stays away");

        Popup help = (Popup)main.FindName("HelpPopup");
        help.IsOpen = true;
        await Wait(300);
        Click((Button)main.FindName("ChecklistLink"));
        await Wait(600);
        Check(Content(main) is HomeView && Section().IsVisible && AppSettings.Load().Checklist == AppSettings.ChecklistShown,
            "? > Getting started brings it back");
        Check(GettingStarted.Shown(all), "brought back, it stays even with everything done");
        RemoveMemory(main, mine);
        RemoveMemory(main, things);
        Invoke(main, "NavigateHome");
        await Wait(300);
    }

    // Tips & shortcuts: grouped, every entry has its texts, every Ctrl+Shift key is a real shortcut, the filter, Esc.
    static async Task TipsPage(MainWindow main)
    {
        Console.WriteLine("Tips & shortcuts");
        Check(Tips.All.Select(t => t.Id).Distinct().Count() == Tips.All.Count, Tips.All.Count + " tips, no id twice");
        foreach (string code in Codes)
        {
            Loc.Switch(code);
            List<string> unresolved = Tips.All.Where(t => t.Keys.StartsWith("Tips.") || t.Text.StartsWith("Tips.") || t.GroupTitle.StartsWith("Tips."))
                .Select(t => t.Id).ToList();
            Check(unresolved.Count == 0, code + ": every tip has its keys, text and group" + (unresolved.Count > 0 ? ": " + string.Join(", ", unresolved) : ""));
        }
        Loc.Switch(Loc.English);
        MethodInfo overlayFor = typeof(OverlayManager).GetMethod("OverlayFor", BindingFlags.Static | BindingFlags.NonPublic);
        foreach (Tip tip in Tips.All.Where(t => t.Keys.StartsWith("Ctrl+Shift+") && t.Keys.Length == "Ctrl+Shift+F".Length))
        {
            Key key = (Key)Enum.Parse(typeof(Key), tip.Keys.Substring("Ctrl+Shift+".Length));
            Check((Overlay)overlayFor.Invoke(null, new object[] { key }) != Overlay.None, tip.Keys + " is a shortcut the app handles");
        }

        Popup help = (Popup)main.FindName("HelpPopup");
        help.IsOpen = true;
        await Wait(300);
        Click((Button)main.FindName("TipsLink"));
        await Wait(600);
        TipsWindow tips = OpenWindow<TipsWindow>();
        Check(tips != null && !help.IsOpen, "? > Tips & shortcuts opens it");
        Check(OverlayManager.Route(Overlay.Search) && main.IsSearchOpen, "with it open, Ctrl+Shift+F still opens search");
        main.CloseSearch();
        await Wait(300);
        StackPanel groups = (StackPanel)tips.FindName("Groups");
        Check(groups.Children.OfType<TextBlock>().Count() == Tips.All.Select(t => t.Group).Distinct().Count(), "a heading per group");
        RenderWindow(tips, "05-tips");
        TextBox filter = (TextBox)tips.FindName("FilterBox");
        filter.Text = "shift+f";
        await Wait(200);
        Check(Descendants(groups).OfType<TextBlock>().Count(t => t.Text == "Ctrl+Shift+F") == 1, "the filter keeps the matching tips");
        filter.Text = "kéz-zz-nothing";
        await Wait(200);
        Check(((TextBlock)tips.FindName("NothingFound")).IsVisible, "no match: says so");
        PressKey(filter, Key.Escape);
        Check(filter.Text.Length == 0 && tips.IsVisible, "Esc empties the filter first");
        PressKey(filter, Key.Escape);
        await Wait(300);
        Check(!tips.IsVisible, "then closes the window");
    }

    // What's new: once after an update to the version its points describe; not on a fresh install; again from ?.
    static async Task WhatsNewNote(MainWindow main)
    {
        Console.WriteLine("What's new");
        string notes = WhatsNew.NotesVersion;
        AppSettings.Update(s => s.LastVersion = null);
        Check(!WhatsNew.ShowIfNew(main, false, notes) && AppSettings.Load().LastVersion == notes && OpenWindow<WhatsNewWindow>() == null,
            "a fresh install: not shown, the version noted");
        AppSettings.Update(s => s.LastVersion = "1.0.0");
        Check(WhatsNew.ShowIfNew(main, true, notes), "after an update to " + notes + ": shown");
        await Wait(500);
        WhatsNewWindow window = OpenWindow<WhatsNewWindow>();
        Check(window != null && ((ItemsControl)window.FindName("PointList")).Items.Count == WhatsNew.Points.Length
              && WhatsNew.Points.Length >= 2 && WhatsNew.Points.Length <= 4, WhatsNew.Points.Length + " points");
        RenderWindow(window, "06-whats-new");
        window.Close();
        await Wait(200);
        Check(!WhatsNew.ShowIfNew(main, true, notes), "the next start: not again");
        AppSettings.Update(s => s.LastVersion = null);
        Check(!WhatsNew.ShowIfNew(main, true, notes + ".1") && OpenWindow<WhatsNewWindow>() == null,
            "a version its points don't describe: not shown");
        AppSettings.Update(s => s.LastVersion = null);
        Check(WhatsNew.ShowIfNew(main, true, notes), "a copy in use from before the version was kept: shown");
        await Wait(300);
        OpenWindow<WhatsNewWindow>()?.Close();
        Popup help = (Popup)main.FindName("HelpPopup");
        help.IsOpen = true;
        await Wait(300);
        Click((Button)main.FindName("WhatsNewLink"));
        await Wait(500);
        Check(OpenWindow<WhatsNewWindow>() != null, "? > What's new opens it again");
        OpenWindow<WhatsNewWindow>()?.Close();
        await Wait(200);
    }

    // Search in the sidebar, the page's ⋯ menu, a card's ⋯ button, the hint lines.
    static async Task VisibleWaysIn(MainWindow main)
    {
        Console.WriteLine("Visible ways in");
        Border search = (Border)Find(main, "NavSearch");
        MouseButton(search, down: true);
        MouseButton(search, down: false);
        await Wait(500);
        Check(main.IsSearchOpen, "Search in the sidebar opens search");
        main.CloseSearch();
        await Wait(300);

        main.ShowSample(ProfileType.Project);
        await Wait(600);
        ProjectView view = (ProjectView)Content(main);
        Button more = (Button)view.FindName("MoreButton");
        Click(more);
        await Wait(400);
        List<string> headers = OpenMenus().SelectMany(m => m.Items.OfType<MenuItem>()).Select(i => i.Header as string).ToList();
        Check(headers.Contains(Loc.T("Common.Rename")) && headers.Contains(Loc.T("Memory.Menu.Export")) && headers.Contains(Loc.T("Background.Edit")),
            "the page's ⋯: the memory's menu and Edit Background (" + string.Join(", ", headers) + ")");
        foreach (ContextMenu open in OpenMenus().ToList())
            open.IsOpen = false;

        Panel cards = (Panel)view.FindName("ProjectPanel");
        Grid card = cards.Children.OfType<Grid>().First();
        Button cardMore = card.Children.OfType<Button>().First(b => (string)b.ToolTip == Loc.T("Card.More"));
        Check(cardMore.Visibility == Visibility.Hidden, "a card's ⋯ is hidden until the card is pointed at");
        cardMore.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, cardMore));
        await Wait(400);
        Check(card.ContextMenu != null && card.ContextMenu.IsOpen && card.ContextMenu.PlacementTarget == cardMore, "it opens the card's own menu");
        RenderWindow(main, "07-card-menu");
        card.ContextMenu.IsOpen = false;

        Check(((FrameworkElement)view.FindName("CardHint")).IsVisible, "the hint line under the cards");
        RenderWindow(main, "08-hint-line");
        Invoke(main, "NavigateHome");
        await Wait(300);
    }

    static List<string> CalloutButtons() =>
        Descendants(Field<FrameworkElement>(GuidedTour.Current, "callout")).OfType<Button>().Select(b => b.Content as string).ToList();

    static int CalloutDots() =>
        Descendants(Field<FrameworkElement>(GuidedTour.Current, "callout")).OfType<System.Windows.Shapes.Rectangle>().Count();

    static T OpenWindow<T>() where T : Window => Application.Current.Windows.OfType<T>().FirstOrDefault(w => w.IsVisible);

    // The example project with notes on two pages, opened the way a search result opens a project.
    static void OpenSampleNotebook(MainWindow main)
    {
        Profile sample = WelcomeMemory.Find(Profiles(main), ProfileType.Project);
        Guid? notebook = sample == null ? null : ProjectStore.LoadOrEmpty(sample).FirstOrDefault(p => p.NotePages != null && p.NotePages.Count > 1)?.Id;
        if (notebook != null)
            Invoke(main, "GoToSearchResult", new SearchResult { Kind = SearchResultKind.Project, Profile = sample, ItemId = notebook.Value });
    }

    static void CheckTourGone(MainWindow main, string when)
    {
        Check(GuidedTour.Current == null, "tour ended " + when);
        Check(OverlayManager.Current == Overlay.None, "no overlay left open " + when);
        List<Window> open = Application.Current.Windows.OfType<Window>().Where(w => w.IsVisible).ToList();
        Check(open.Count == 1, "only the main window is open " + when + " (" + string.Join(", ", open.Select(w =>
            w.GetType().Name + " '" + w.Title + "' same=" + ReferenceEquals(w, main))) + ")");
        AdornerLayer layer = AdornerLayer.GetAdornerLayer((UIElement)main.Content);
        Adorner[] left = layer?.GetAdorners((UIElement)main.Content) ?? new Adorner[0];
        Check(!left.Any(a => a.GetType().Name == "TourAdorner"), "no dimmed overlay left on the main window " + when);
    }

    static void CheckHoleOn(MainWindow main, FrameworkElement element, string what)
    {
        Rect? hole = Hole();
        UIElement page = (UIElement)main.Content;
        Rect expected = element.TransformToAncestor(page).TransformBounds(new Rect(element.RenderSize));
        expected.Inflate(6, 6);
        expected.Intersect(new Rect(page.RenderSize));
        bool ok = hole is Rect h && Math.Abs(h.X - expected.X) < 1 && Math.Abs(h.Y - expected.Y) < 1
            && Math.Abs(h.Width - expected.Width) < 1 && Math.Abs(h.Height - expected.Height) < 1;
        Check(ok, what + " (hole " + (hole?.ToString() ?? "none") + ", expected " + expected + ")");
    }

    // ---- Home ---------------------------------------------------------

    static async Task Home(MainWindow main)
    {
        Console.WriteLine("Home: What can TidyMind do?");
        Invoke(main, "NavigateHome");
        await Wait(800);
        HomeView home = (HomeView)((ContentControl)main.FindName("ContentArea")).Content;
        UniformGrid tiles = (UniformGrid)home.FindName("DiscoveryItems");
        Button toggle = (Button)home.FindName("DiscoveryToggle");

        Check(tiles.Children.Count == 12, "12 tiles");
        List<DependencyObject> inside = Descendants(tiles).ToList();
        Check(!inside.OfType<ButtonBase>().Any() && !inside.OfType<Control>().Any(), "no buttons or other controls in the tiles");
        Check(inside.OfType<UIElement>().All(e => !e.Focusable), "nothing in the tiles takes the focus");
        Check(inside.OfType<FrameworkElement>().All(e => e.Cursor == null), "no hand cursor on the tiles");
        Check(inside.OfType<FrameworkElement>().All(e => e.ToolTip == null && e.ContextMenu == null), "no tooltip or menu on the tiles");
        Check((string)toggle.Content == "Hide" && tiles.Visibility == Visibility.Visible, "unfolded, link says Hide");
        Console.WriteLine("  columns at " + main.ActualWidth + " wide: " + tiles.Columns);
        await ShowTiles(main, tiles, "10-home-default");

        Click(toggle);
        await Wait(300);
        Check(tiles.Visibility == Visibility.Collapsed && (string)toggle.Content == "Show", "Hide folds the tiles, link says Show");
        Check(TitleVisible(home), "the title is still there");
        Check(AppSettings.Load().HideDiscovery == true, "settings: folded");
        await ShowTiles(main, toggle, "11-home-folded");

        Invoke(main, "NavigateHome"); // Home is rebuilt from settings.json, as on the next start
        await Wait(800);
        home = (HomeView)((ContentControl)main.FindName("ContentArea")).Content;
        tiles = (UniformGrid)home.FindName("DiscoveryItems");
        toggle = (Button)home.FindName("DiscoveryToggle");
        Check(tiles.Visibility == Visibility.Collapsed && (string)toggle.Content == "Show", "still folded when Home is built again");

        Click(toggle);
        await Wait(300);
        Check(tiles.Visibility == Visibility.Visible && (string)toggle.Content == "Hide", "Show brings them back, link says Hide");
        Check(AppSettings.Load().HideDiscovery == null, "settings: unfolded (left out of the file)");
        Console.WriteLine("  settings.json: " + File.ReadAllText(AppPaths.Data("settings.json")));

        main.Width = main.MinWidth;
        await Wait(800);
        Console.WriteLine("  columns at " + main.ActualWidth + " wide: " + tiles.Columns);
        Check(tiles.Columns == 2, "two columns at the narrowest window");
        await ShowTiles(main, tiles, "12-home-narrow");

        main.WindowState = WindowState.Maximized;
        await Wait(1000);
        Console.WriteLine("  columns at " + main.ActualWidth + " wide: " + tiles.Columns);
        await ShowTiles(main, tiles, "13-home-maximized");
        main.WindowState = WindowState.Normal;
    }

    static bool TitleVisible(HomeView home) =>
        Descendants(home).OfType<TextBlock>().Any(t => t.Text == "What can TidyMind do?" && t.IsVisible);

    static async Task ShowTiles(MainWindow main, FrameworkElement element, string name)
    {
        element.BringIntoView();
        await Wait(500);
        Shot(main, name);
    }

    // ---- Driving the tour -----------------------------------------------

    static T Field<T>(object o, string name) =>
        (T)o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(o);

    static IReadOnlyList<TourStep> Steps() => Field<IReadOnlyList<TourStep>>(GuidedTour.Current, "steps");
    static TourStep Step() => Steps()[Field<int>(GuidedTour.Current, "index")];

    static Rect? Hole()
    {
        object adorner = Field<object>(GuidedTour.Current, "adorner");
        return adorner == null ? null : Field<Rect?>(adorner, "hole");
    }

    static void Choose(TourChoice choice) => GuidedTour.Current.GetType()
        .GetMethod("Choose", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(GuidedTour.Current, new object[] { choice });

    static async Task Restart(MainWindow main)
    {
        main.GetType().GetMethod("StartTour", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(main, null);
        await Wait(1000);
    }

    static List<Profile> Profiles(MainWindow main) => Field<List<Profile>>(main, "profiles");

    static void Click(Button button) =>
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();

    static void PressEscape(Window window)
    {
        KeyEventArgs args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Escape)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent
        };
        window.RaiseEvent(args);
    }

    static FrameworkElement Find(Window window, string name) => GuidedTour.Find(window, name);

    static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (DependencyObject deeper in Descendants(child))
                yield return deeper;
        }
    }

    static Task Wait(int ms) => Task.Delay(ms);

    // What's on screen over the window (owned windows included), as a PNG.
    static void Shot(Window window, string name)
    {
        try
        {
            window.UpdateLayout();
            Matrix toDevice = PresentationSource.FromVisual(window).CompositionTarget.TransformToDevice;
            System.Drawing.Rectangle r;
            if (window.WindowState == WindowState.Maximized)
            {
                System.Windows.Forms.Screen screen = System.Windows.Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(window).Handle);
                r = screen.WorkingArea;
            }
            else
            {
                r = new System.Drawing.Rectangle((int)(window.Left * toDevice.M11), (int)(window.Top * toDevice.M22),
                    (int)(window.ActualWidth * toDevice.M11), (int)(window.ActualHeight * toDevice.M22));
            }
            using (System.Drawing.Bitmap bitmap = new System.Drawing.Bitmap(r.Width, r.Height))
            {
                using (System.Drawing.Graphics g = System.Drawing.Graphics.FromImage(bitmap))
                    g.CopyFromScreen(r.Location, System.Drawing.Point.Empty, r.Size);
                bitmap.Save(Path.Combine(shots, name + ".png"), System.Drawing.Imaging.ImageFormat.Png);
            }
        }
        catch (Exception e)
        {
            Console.WriteLine("  (screenshot " + name + " failed: " + e.Message + ")");
        }
    }
}
