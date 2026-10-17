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
//   Harness ui <dataFolder> <shotsFolder> drives the guided tour and Home's "What can TidyMind do?" tiles
//   Harness export <dataFolder> <outFolder> every PDF, Word and Excel export in en/hu/sr (PDF pages rendered to PNG, Office files validated)
//   Harness update <dataFolder> <shotsFolder> the in-app update against a local fake release server
//   Harness tasks <dataFolder> <shotsFolder> Project Details as one scrolling page (5, 60, 250, 500 tasks)
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
        string[] modes = { "toast", "ui", "plan", "loc", "locfirst", "locdata1", "locdata2", "probe", "taskpath", "export", "update", "tasks" };
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

    static async Task Scenarios(MainWindow main)
    {
        await Wait(1200);

        Console.WriteLine("Welcome card (fresh data folder)");
        Check(GuidedTour.Current != null && Steps().Count == 1, "the welcome card is up");
        Shot(main, "01-welcome");
        Choose(TourChoice.Begin);
        await Wait(1500);
        Check(GuidedTour.Current != null && Steps().Count > 1, "Start the tour runs the tour");
        Console.WriteLine("  profiles: " + string.Join(", ", Profiles(main).Select(p => p.Name + "/" + p.IsSample)));
        Check(Profiles(main).Any(p => p.IsSample == true), "the Welcome example memories exist");
        Shot(main, "02-first-step");

        foreach (Overlay overlay in new[] { Overlay.Calendar, Overlay.QuickNotes, Overlay.Reminders })
        {
            Console.WriteLine("Corner step, " + overlay + " opened from its button, then Next");
            await ToStep("Notes, reminders and calendar");
            CheckHoleOn(main, Find(main, "OverlayButtons"), "spotlight on the three corner buttons");
            OverlayManager.Route(overlay); // what the button's click does
            await Wait(700);
            Check(OverlayManager.Current == overlay, overlay + " is open");
            if (overlay == Overlay.Calendar)
                CheckHoleOn(main, Find(main, "Sheet"), "spotlight moves onto the open calendar");
            else
                CheckHoleOn(main, Find(main, "OverlayButtons"), "spotlight stays on the buttons (" + overlay + " is a window in front)");
            Shot(main, "03-corner-" + overlay);
            Choose(TourChoice.Next);
            await Wait(800);
            CheckEndStep(main, overlay.ToString());
        }

        Console.WriteLine("Corner step, calendar closed by hand, then Next");
        await ToStep("Notes, reminders and calendar");
        OverlayManager.Route(Overlay.Calendar);
        await Wait(600);
        OverlayManager.Close(Overlay.Calendar);
        await Wait(600);
        CheckHoleOn(main, Find(main, "OverlayButtons"), "spotlight back on the buttons once the calendar is closed");
        Choose(TourChoice.Next);
        await Wait(800);
        CheckEndStep(main, "closed by hand");

        Console.WriteLine("End step, calendar opened again with Ctrl+Shift+C");
        OverlayManager.Route(Overlay.Calendar);
        await Wait(800);
        Check(Hole() == null, "no spotlight while the calendar covers the ? button");
        Shot(main, "05-end-calendar-reopened");
        OverlayManager.Close(Overlay.Calendar);
        await Wait(800);
        CheckHoleOn(main, Find(main, "HelpButton"), "spotlight back on the ? button once it's closed");

        Console.WriteLine("Skip with the calendar open");
        await ToStep("Notes, reminders and calendar");
        OverlayManager.Route(Overlay.Calendar);
        await Wait(600);
        Choose(TourChoice.Dismiss);
        await Wait(800);
        CheckTourGone(main, "after Skip");
        Check(AppSettings.Load().Tour == AppSettings.TourDismissed, "settings: tour dismissed");

        Console.WriteLine("Esc with Quick Notes open");
        await Restart(main);
        await ToStep("Notes, reminders and calendar");
        OverlayManager.Route(Overlay.QuickNotes);
        await Wait(600);
        main.Activate();
        await Wait(300);
        PressEscape(main);
        await Wait(800);
        CheckTourGone(main, "after Esc");

        Console.WriteLine("Next then Finish, no pause in between");
        await Restart(main);
        await ToStep("Notes, reminders and calendar");
        OverlayManager.Route(Overlay.Calendar);
        await Wait(50);
        Choose(TourChoice.Next);
        Choose(TourChoice.Finish);
        await Wait(900);
        CheckTourGone(main, "after a quick Next, Finish");
        Check(AppSettings.Load().Tour == AppSettings.TourCompleted, "settings: tour completed");

        Console.WriteLine("Project steps: Skip while the tour's project window is open");
        await Restart(main);
        await ToStep("Turn the page");
        await Wait(1500);
        ProjectWindow project = GuidedTour.Open<ProjectWindow>();
        Check(project != null, "the notes step opened the example project");
        Check(Host() == project, "the tour sits on the project window");
        Shot(project ?? (Window)main, "06-notes-step");
        Choose(TourChoice.Dismiss);
        await Wait(1000);
        CheckTourGone(main, "after Skip on the notes step");

        Console.WriteLine("Project steps: Next on 'Close the project' instead of Save");
        await Restart(main);
        await ToStep("Turn the page");
        await Wait(1500);
        Choose(TourChoice.Next); // to "Close the project"
        await Wait(600);
        Check(Step().Title == "Close the project", "on the close step");
        Choose(TourChoice.Next); // without Save
        await Wait(1500);
        Check(Step().Title == "Change a card's look", "on the card step");
        Check(GuidedTour.Open<ProjectWindow>() == null, "the project window was closed (and saved)");
        Check(Host() == main, "the tour is back on the main window");
        Check(Hole() != null, "the card is spotlighted");
        Shot(main, "07-card-step");
        Choose(TourChoice.Dismiss);
        await Wait(600);

        await Home(main);
    }

    static void CheckEndStep(MainWindow main, string after)
    {
        Check(Step().Title == "That's the tour", "on the end step (" + after + ")");
        Check(OverlayManager.Current == Overlay.None, "nothing open over the main window (" + after + ")");
        Check(!QuickNotesWindow.IsOpen && !RemindersWindow.IsOpen && !main.IsCalendarOpen, "Quick Notes, Reminders, Calendar all closed");
        Check(Host() == main, "the tour sits on the main window (host " + (Host()?.GetType().Name ?? "null") + " same="
            + ReferenceEquals(Host(), main) + " mainwindows=" + Application.Current.Windows.OfType<MainWindow>().Count() + ")");
        CheckHoleOn(main, Find(main, "HelpButton"), "spotlight on the ? button");
        Shot(main, "04-end-after-" + after.Replace(' ', '-'));
    }

    static void CheckTourGone(MainWindow main, string when)
    {
        Check(GuidedTour.Current == null, "tour ended " + when);
        Check(OverlayManager.Current == Overlay.None, "no overlay left open " + when);
        Check(GuidedTour.Open<ProjectWindow>() == null, "no project window left open " + when);
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
    static Window Host() => Field<Window>(GuidedTour.Current, "host");

    static Rect? Hole()
    {
        object adorner = Field<object>(GuidedTour.Current, "adorner");
        return adorner == null ? null : Field<Rect?>(adorner, "hole");
    }

    static void Choose(TourChoice choice) => GuidedTour.Current.GetType()
        .GetMethod("Choose", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(GuidedTour.Current, new object[] { choice });

    // Next until that step, like a user clicking through.
    static async Task ToStep(string title)
    {
        if (GuidedTour.Current == null)
            throw new InvalidOperationException("no tour running");
        // Straight there through the tour's own Show, which runs the step's Prepare as Next does.
        int at = Steps().ToList().FindIndex(s => s.Title == title);
        GuidedTour.Current.GetType().GetMethod("Show", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(GuidedTour.Current, new object[] { at });
        await Wait(700);
    }

    static async Task Restart(MainWindow main)
    {
        main.GetType().GetMethod("StartTour", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(main, new object[] { true });
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
