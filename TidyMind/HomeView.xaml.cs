using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using ShapePath = System.Windows.Shapes.Path;

namespace TidyMind
{
    // Dashboard, built fresh every time Home is opened; clicking an item navigates to it. Read-only except the
    // quick to-do list, which is edited in place.
    public partial class HomeView : UserControl
    {
        private const string CalendarGlyph = "\uE787"; // Segoe MDL2: Calendar (a calendar note)
        private const int DaybookDays = 8;             // today + the aggregator's 7 upcoming days
        private const int GroupSize = 6;                  // per group: a longer list stops being a nudge
        private const int HeartbeatPercent = 90;           // the leading almost-done ring beats once per visit from here up
        private const double TileSize = 124;
        private const double TileRadius = 12;
        private const double StackBelowWidth = 860;    // narrower than this, the week moves under the to-do sheet

        private static readonly CultureInfo English = CultureInfo.InvariantCulture;
        private readonly IList<Profile> profiles;
        private readonly Action<SearchResult> navigate;
        private Window host;
        private bool heartbeatPending = true; // the almost-done ring beats once per visit, not on every refresh

        public HomeView(IList<Profile> profiles, Action<SearchResult> navigate)
        {
            InitializeComponent();
            this.profiles = profiles;
            this.navigate = navigate;

            SizeChanged += (s, e) => ApplyWidth(e.NewSize.Width);

            // Overlays (calendar, reminders) and dialogs change data while Home stays on screen, and reminders
            // fire from Task Scheduler while the app is in the background; all of them end with the main window
            // being activated again. Subscribed only while Home is the shown view.
            Loaded += (s, e) =>
            {
                host = Window.GetWindow(this);
                if (host != null) host.Activated += Host_Activated;
            };
            Unloaded += (s, e) =>
            {
                if (host == null) return;
                host.Activated -= Host_Activated;
                host.PreviewMouseLeftButtonUp -= RefreshAfterClick;
                host = null;
            };

            Refresh();
        }

        private void Host_Activated(object sender, EventArgs e)
        {
            // Activated by clicking into the window: rebuilding now would swap the row under the cursor between
            // mouse-down and mouse-up and swallow that click, so wait until the click has gone through.
            if (Mouse.LeftButton == MouseButtonState.Pressed)
            {
                host.PreviewMouseLeftButtonUp -= RefreshAfterClick; // activated twice in one press: still one refresh
                host.PreviewMouseLeftButtonUp += RefreshAfterClick;
            }
            else
                Refresh();
        }

        private void RefreshAfterClick(object sender, MouseButtonEventArgs e)
        {
            ((Window)sender).PreviewMouseLeftButtonUp -= RefreshAfterClick;
            Dispatcher.BeginInvoke(new Action(Refresh), DispatcherPriority.Background);
        }

        // Re-reads every dashboard source from disk and rebuilds the sections in place (scroll position,
        // a half-typed to-do and the to-do list's own state survive). New sources belong in here.
        public void Refresh()
        {
            DateTime now = DateTime.Now;
            DashboardData data = DashboardDataAggregator.Build(profiles, now);

            DateText.Text = now.ToString("dddd, MMMM d", English);
            GreetingText.Text = GreetingProvider.Pick(now);

            ShowDaybook(data, now.Date);
            ShowProjects(data);

            // Before it's loaded the control loads itself.
            if (QuickTodos.IsLoaded)
                QuickTodos.Refresh();
        }

        // Side by side when there's room; otherwise the week goes under the to-do sheet and the margins tighten.
        private void ApplyWidth(double width)
        {
            bool stacked = width < StackBelowWidth;

            Grid.SetRow(WeekPanel, stacked ? 1 : 0);
            Grid.SetColumn(WeekPanel, stacked ? 0 : 2);
            Grid.SetColumnSpan(WeekPanel, stacked ? 3 : 1);
            Grid.SetColumnSpan(TodoSheet, stacked ? 3 : 1);
            WeekPanel.Margin = new Thickness(0, stacked ? 20 : 0, 0, 0);

            double side = width < 640 ? 20 : stacked ? 32 : 48;
            Page.Margin = new Thickness(side, stacked ? 28 : 40, side, 88);
        }

        // ---- This week ----------------------------------------------------

        // Every day gets a line, planned or not, so the week keeps its shape; an empty day is just its date.
        private void ShowDaybook(DashboardData data, DateTime today)
        {
            Daybook.Children.Clear();

            for (int offset = 0; offset < DaybookDays; offset++)
            {
                DateTime date = today.AddDays(offset);
                List<DashboardAgendaItem> items = offset == 0
                    ? data.TodayItems
                    : data.UpcomingItems.Where(i => i.Date.Date == date).ToList();
                Daybook.Children.Add(DayRow(date, items, offset == 0));
            }
        }

        private FrameworkElement DayRow(DateTime date, List<DashboardAgendaItem> items, bool isToday)
        {
            bool empty = items.Count == 0;

            Border row = new Border
            {
                BorderBrush = Res("Hairline"),
                BorderThickness = new Thickness(0, isToday ? 0 : 1, 0, 0),
                Padding = new Thickness(0, empty && !isToday ? 6 : 12, 0, empty && !isToday ? 6 : 12),
                SnapsToDevicePixels = true,
                Background = Brushes.Transparent, // so the whole day, gaps included, answers a right-click
                ContextMenu = AgendaMenus.ForDay(this, date, null, Refresh) // entries have their own menus
            };

            Grid grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.Children.Add(DateGutter(date, isToday, empty));

            StackPanel content = new StackPanel();
            Grid.SetColumn(content, 1);
            foreach (DashboardAgendaItem item in items)
                content.Children.Add(AgendaRow(item));
            if (empty && isToday)
                content.Children.Add(Hint("Nothing planned for today.", new Thickness(0, 6, 0, 0)));
            grid.Children.Add(content);

            row.Child = grid;
            return row;
        }

        // The day number, set large like a planner's margin; clicking it opens that day in the calendar.
        private FrameworkElement DateGutter(DateTime date, bool isToday, bool empty)
        {
            Brush numberBrush = isToday ? CardEffects.Accent : empty ? Res("TextSub") : Res("TextMain");

            StackPanel gutter = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                ToolTip = "Open in calendar"
            };

            TextBlock number = new TextBlock
            {
                Text = date.Day.ToString(English),
                FontFamily = (FontFamily)FindResource("DisplayFont"),
                FontSize = 24,
                FontWeight = isToday ? FontWeights.Normal : FontWeights.Light,
                Foreground = numberBrush,
                MinWidth = 28
            };
            Typography.SetNumeralAlignment(number, FontNumeralAlignment.Tabular);

            TextBlock weekday = new TextBlock
            {
                Text = isToday ? "Today" : date.ToString("ddd", English),
                FontSize = 11,
                FontWeight = isToday ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = isToday ? CardEffects.Accent : Res("TextSub"),
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(5, 0, 0, 5)
            };

            gutter.Children.Add(number);
            gutter.Children.Add(weekday);
            CardEffects.AttachClick(gutter, () => OverlayManager.OpenCalendarAt(date));
            return gutter;
        }

        // Reminders lead with their time; a calendar note covers the whole day, so it gets the calendar mark
        // and reads a little heavier. Both keep their text on the same left edge.
        private FrameworkElement AgendaRow(DashboardAgendaItem item)
        {
            bool reminder = item.Kind == AgendaKind.Reminder;
            Action open = reminder
                ? () => OverlayManager.OpenReminder(item.Reminder.Id)
                : (Action)(() => OverlayManager.OpenCalendarAt(item.Date));

            // A note's further lines are part of it; a reminder's detail line is just its labels.
            string copy = reminder || string.IsNullOrEmpty(item.Detail) ? item.Text : item.Text + Environment.NewLine + item.Detail;

            Border row = HoverRow(new Thickness(10, 6, 10, 6));
            row.Margin = new Thickness(-10, 0, 0, 0);
            TextCopy.AttachClick(row, open); // a click navigates; dragging over the text selects it
            ContextMenu menu = reminder
                ? AgendaMenus.ForReminder(this, item.Reminder.Id, item.Date, copy, Refresh)
                : AgendaMenus.ForNote(this, item.Date, copy, Refresh);
            row.ContextMenu = menu;

            Grid grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(54) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            if (reminder)
            {
                TextBox time = RowText(item.Time?.ToString("HH:mm", English) ?? "", 13, Res("TextSub"), menu);
                time.Margin = new Thickness(0, 1, 12, 0);
                time.HorizontalAlignment = HorizontalAlignment.Right;
                Typography.SetNumeralAlignment(time, FontNumeralAlignment.Tabular);
                grid.Children.Add(time);
            }
            else
            {
                grid.Children.Add(new TextBlock
                {
                    Text = CalendarGlyph,
                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize = 12,
                    Foreground = Res("TextSub"),
                    Margin = new Thickness(0, 4, 13, 0),
                    HorizontalAlignment = HorizontalAlignment.Right
                });
            }

            StackPanel text = new StackPanel();
            Grid.SetColumn(text, 1);
            text.Children.Add(RowText(item.Text, 14, Res("TextMain"), menu, reminder ? (FontWeight?)null : FontWeights.SemiBold));
            if (!string.IsNullOrEmpty(item.Detail))
            {
                TextBox detail = RowText(item.Detail, 12, Res("TextSub"), menu);
                detail.Margin = new Thickness(0, 1, 0, 0);
                text.Children.Add(detail);
            }
            grid.Children.Add(text);

            row.Child = grid;
            return row;
        }

        // ---- Projects -----------------------------------------------------

        private void ShowProjects(DashboardData data)
        {
            int active = data.ActiveProjectCount;
            ProjectsLede.Text = active == 0
                ? "Nothing in progress."
                : active == 1
                    ? "1 in progress, " + data.AverageCompletion + "% done."
                    : active + " in progress, " + data.AverageCompletion + "% done on average.";

            ProjectSignals.Children.Clear();

            // Each group appears only when something qualifies; the rules are in DashboardDataAggregator.
            if (data.AlmostDone.Count > 0)
                ProjectSignals.Children.Add(ProjectGroup("Almost done", "70% or more, a few tasks left.", data.AlmostDone,
                    p => p.TasksLeft == 1 ? "1 task left" : p.TasksLeft + " tasks left", true));
            if (data.Stalled.Count > 0)
                ProjectSignals.Children.Add(ProjectGroup("Stalled", "Under 30% and untouched for two weeks or more.", data.Stalled,
                    p => "Untouched " + (DateTime.Today - p.LastModified.Date).Days + " days", false));

            if (ProjectSignals.Children.Count == 0)
                ProjectSignals.Children.Add(Hint(data.ProjectCount == 0 ? "No projects yet." : "Everything's moving along.", new Thickness(0)));
        }

        private FrameworkElement ProjectGroup(string title, string rule, List<DashboardProject> projects,
            Func<DashboardProject, string> status, bool lead)
        {
            StackPanel group = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };

            StackPanel heading = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            heading.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Res("TextMain") });
            heading.Children.Add(new TextBlock
            {
                Text = projects.Count > GroupSize ? rule + "  Showing " + GroupSize + " of " + projects.Count + "." : rule,
                FontSize = 12,
                Foreground = Res("TextSub"),
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Bottom
            });
            group.Children.Add(heading);

            // Negative margin: the cards' own margin leaves room for the hover lift and shadow
            WrapPanel tiles = new WrapPanel { Margin = new Thickness(-10, 0, -10, 0) };
            for (int i = 0; i < projects.Count && i < GroupSize; i++)
                tiles.Children.Add(ProjectTile(projects[i], status(projects[i]), lead && i == 0 && projects[i].Percent >= HeartbeatPercent));
            group.Children.Add(tiles);
            return group;
        }

        // A small version of the project view's card: same white square, same ring filling clockwise.
        private FrameworkElement ProjectTile(DashboardProject project, string status, bool beat)
        {
            // Transparent background: the gap between card and caption answers clicks and right-clicks too.
            StackPanel cell = new StackPanel
            {
                Width = TileSize,
                Margin = new Thickness(10, 8, 10, 12),
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent
            };

            // The project grid's own card (ProjectCard), compact.
            ProjectCardVisual visual = ProjectCard.Build(project.Card, TileSize, TileRadius, compact: true);
            Grid card = visual.Card;
            DropShadowEffect shadow = visual.Shadow;
            ShapePath progress = visual.Progress;
            cell.Children.Add(card);

            // Caption under the card: which memory it lives in, and why it's here.
            cell.Children.Add(Caption(project.MemoryName, 12, Res("TextBody"), FontWeights.Normal, new Thickness(2, 10, 0, 0)));
            cell.Children.Add(Caption(status, 11, Res("TextSub"), FontWeights.Normal, new Thickness(2, 1, 0, 0)));

            CardEffects.AttachHoverLift(card, shadow);
            CardEffects.AttachClick(cell, () => navigate(project.Target));
            AttachProjectMenu(cell, project);

            // The one moment of motion on Home: the nearly finished project's ring beats once as you arrive.
            if (beat && progress != null && heartbeatPending && SystemParameters.ClientAreaAnimation)
            {
                heartbeatPending = false;
                RoutedEventHandler onLoaded = null;
                onLoaded = (s, e) =>
                {
                    card.Loaded -= onLoaded;
                    CardEffects.RingHeartbeat(progress);
                };
                card.Loaded += onLoaded;
            }

            return cell;
        }

        // The project grid's own card menu (ProjectMenu). Built when it opens, from the memory's file read fresh
        // then, so Rename/Delete change and save the current list rather than the copy Home was drawn from.
        private void AttachProjectMenu(FrameworkElement cell, DashboardProject shown)
        {
            Profile memory = shown.Target.Profile;
            Guid projectId = shown.Target.ItemId;

            cell.ContextMenu = new ContextMenu(); // placeholder: without one, WPF doesn't raise ContextMenuOpening
            cell.ContextMenuOpening += (s, e) =>
            {
                if (!ProjectStore.TryLoad(memory, out List<Project> projects, out _))
                {
                    e.Handled = true;
                    return;
                }

                // Deleted since Home was drawn: redraw instead.
                Project project = projects.FirstOrDefault(p => p.Id == projectId);
                if (project == null)
                {
                    e.Handled = true;
                    Refresh();
                    return;
                }

                cell.ContextMenu = ProjectMenu.Build(this, project,
                    save: () => ProjectStore.SaveOrWarn(memory, projects),
                    redraw: Refresh,
                    remove: () => projects.Remove(project));
            };
        }

        // ---- Pieces -------------------------------------------------------

        // A row printed on the page that turns into paper under the cursor.
        private Border HoverRow(Thickness padding)
        {
            Border row = new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = padding,
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand
            };
            row.MouseEnter += (s, e) => row.Background = Res("BgPanel");
            row.MouseLeave += (s, e) => row.Background = Brushes.Transparent;
            return row;
        }

        // Selectable, but with the row's hand cursor: clicking navigates is still the main thing a row does.
        // It also carries the row's menu: selectable text has its own one-item "Copy" menu, which would otherwise
        // answer every right-click on the text and leave the row's menu reachable only from the empty margins.
        // (Ctrl+C still copies a selection.)
        private static TextBox RowText(string text, double fontSize, Brush foreground, ContextMenu rowMenu, FontWeight? weight = null)
        {
            TextBox box = TextCopy.Selectable(text, fontSize, foreground, weight);
            box.Cursor = Cursors.Hand;
            box.ContextMenu = rowMenu;
            return box;
        }

        private static TextBlock Caption(string text, double fontSize, Brush foreground, FontWeight weight, Thickness margin)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = fontSize,
                FontWeight = weight,
                Foreground = foreground,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = margin
            };
        }

        private TextBlock Hint(string text, Thickness margin)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 13,
                Foreground = Res("TextSub"),
                Margin = margin
            };
        }

        private Brush Res(string key) => (Brush)FindResource(key);
    }
}
