using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace TidyMind
{
    // Dashboard, built fresh every time Home is opened; clicking an item navigates to it. Read-only except the
    // quick to-do list, which is edited in place.
    public partial class HomeView : UserControl
    {
        private const string CalendarGlyph = "\uE787"; // Segoe MDL2: Calendar (a calendar note)
        private const int DaybookDays = 8;             // today + the aggregator's 7 upcoming days
        private const double StackBelowWidth = 860;    // narrower than this, the week moves under the to-do sheet

        private readonly IList<Profile> profiles;
        private readonly Action<SearchResult> navigate;
        private readonly Action openTodayPlan;
        private Window host;

        public HomeView(IList<Profile> profiles, Action<SearchResult> navigate, Action openTodayPlan)
        {
            InitializeComponent();
            this.profiles = profiles;
            this.navigate = navigate;
            this.openTodayPlan = openTodayPlan;

            ShowDiscovery();
            SizeChanged += (s, e) => ApplyWidth(e.NewSize.Width);
            Loc.Track(this, () =>
            {
                ShowDiscovery();
                Refresh();
            });
            QuickTodos.DoneChanged += () => ShowPlan(DateTime.Now); // a linked block's row follows its to-do

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

            DateText.Text = now.ToString(Loc.T("Format.WeekdayDayMonth"), Loc.Culture);
            GreetingText.Text = GreetingProvider.Pick(now);

            ShowPlan(now);
            ShowDaybook(data, now.Date);
            ShowExpiries(data, now.Date);

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

            // As many tiles a row as fit at their narrowest: four on a wide page, two on the narrowest.
            double pageWidth = Math.Min(width - 2 * side, Page.MaxWidth);
            DiscoveryItems.Columns = Math.Clamp((int)((pageWidth + TileGap) / TileMinWidth), 2, 4);
        }

        // ---- What can TidyMind do? ----------------------------------------

        private const double TileMinWidth = 230;
        private const double TileGap = 12;

        // One tile per feature: its Segoe MDL2 glyph and its key; the name and line are "Home.Tile.<Key>.Title" and
        // "Home.Tile.<Key>.Text".
        private static readonly (string Glyph, string Key)[] Features =
        {
            ("\uE821", "Projects"),    // Work
            ("\uE736", "Notebook"),    // ReadingMode
            ("\uE73A", "Todos"),       // CheckboxComposite
            ("\uE7B8", "Collections"), // Package
            ("\uEA8F", "Reminders"),   // Ringer
            ("\uE787", "Calendar"),    // Calendar
            ("\uE823", "DayPlan"),     // Recent
            ("\uE70B", "QuickNotes"),  // QuickNote
            ("\uE721", "Search"),      // Search
            ("\uE790", "Backgrounds"), // Color
            ("\uEDE1", "Export"),      // Export
            ("\uE777", "Backup")       // UpdateRestore
        };

        // Built again after a language switch.
        private void ShowDiscovery()
        {
            DiscoveryItems.Margin = new Thickness(0, 0, -TileGap, -TileGap); // the tiles' right and bottom gaps reach past the edge
            DiscoveryItems.Children.Clear();
            foreach ((string glyph, string key) in Features)
                DiscoveryItems.Children.Add(FeatureTile(glyph, Loc.T("Home.Tile." + key + ".Title"), Loc.T("Home.Tile." + key + ".Text")));
            FoldDiscovery(AppSettings.Load().HideDiscovery == true);
        }

        // Remembered for next time; if settings.json can't be written, just for now.
        private void DiscoveryToggle_Click(object sender, RoutedEventArgs e)
        {
            bool fold = DiscoveryItems.Visibility == Visibility.Visible;
            AppSettings.Update(s => s.HideDiscovery = fold ? true : null);
            FoldDiscovery(fold);
        }

        private void FoldDiscovery(bool folded)
        {
            DiscoveryItems.Visibility = folded ? Visibility.Collapsed : Visibility.Visible;
            DiscoveryToggle.Content = Loc.T(folded ? "Home.Discovery.Show" : "Home.Discovery.Hide");
        }

        // To read, not to click: a quiet frame with the feature's mark, its name and one line. Nothing in it reacts
        // to the mouse or takes the keyboard focus.
        private FrameworkElement FeatureTile(string glyph, string title, string text)
        {
            Grid grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            grid.Children.Add(new Border
            {
                Width = 30,
                Height = 30,
                CornerRadius = new CornerRadius(8),
                Background = Res("AccentSoft"),
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 1, 12, 0),
                Child = new TextBlock
                {
                    Text = glyph,
                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize = 15,
                    Foreground = Res("Accent"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            });

            StackPanel words = new StackPanel();
            Grid.SetColumn(words, 1);
            words.Children.Add(Caption(title, 14, Res("TextMain"), FontWeights.SemiBold, new Thickness(0)));
            TextBlock line = Caption(text, 12, Res("TextSub"), FontWeights.Normal, new Thickness(0, 2, 0, 0));
            line.TextWrapping = TextWrapping.Wrap;
            words.Children.Add(line);
            grid.Children.Add(words);

            return new Border
            {
                Background = Res("BgPanel"),
                BorderBrush = Res("Border"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(14, 12, 14, 12),
                Margin = new Thickness(0, 0, TileGap, TileGap),
                SnapsToDevicePixels = true,
                Child = grid
            };
        }

        // ---- Today's plan ------------------------------------------------

        private const int PlanRowsShown = 3;

        // Today has day plan blocks: the week then doesn't call today empty.
        private bool todayPlanned;

        // The blocks still ahead today (the one going on now first), a few at most; a click on one opens the plan.
        private void ShowPlan(DateTime now)
        {
            PlanRows.Children.Clear();
            bool readable = DayPlanStore.TryLoad(out DayPlanFile plan);
            List<DayPlanBlock> today = DayPlanStore.BlocksOn(plan, now.Date);
            int minute = (int)now.TimeOfDay.TotalMinutes;
            List<DayPlanBlock> ahead = today.Where(b => b.EndMinute > minute).ToList();
            todayPlanned = today.Count > 0;
            OpenPlanLink.Content = Loc.T(today.Count == 0 && readable ? "Home.Plan.PlanToday" : "Home.Plan.Open");

            if (!readable)
                PlanRows.Children.Add(Hint(Loc.T("Home.Plan.Unreadable"), new Thickness(0, 6, 0, 0)));
            else if (ahead.Count == 0)
                PlanRows.Children.Add(Hint(Loc.T(today.Count == 0 ? "Home.Plan.Nothing" : "Home.Plan.NothingMore"),
                    new Thickness(0, 6, 0, 0)));

            foreach (DayPlanBlock block in ahead.Take(PlanRowsShown))
                PlanRows.Children.Add(PlanRow(block));
            if (ahead.Count > PlanRowsShown)
                PlanRows.Children.Add(Hint(Loc.F("Home.Plan.AndMore", ahead.Count - PlanRowsShown), new Thickness(0, 2, 0, 0)));
        }

        private FrameworkElement PlanRow(DayPlanBlock block)
        {
            Border row = HoverRow(new Thickness(10, 5, 10, 5));
            row.Margin = new Thickness(-10, 0, 0, 0);
            CardEffects.AttachClick(row, openTodayPlan);

            Grid grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            TextBlock time = Caption(block.TimeRange(), 13, Res("TextSub"), FontWeights.Normal, new Thickness(0, 1, 0, 0));
            Typography.SetNumeralAlignment(time, FontNumeralAlignment.Tabular);
            grid.Children.Add(time);

            TextBlock title = Caption(block.Title, 14, Res(block.Done ? "TextSub" : "TextMain"), FontWeights.Normal, new Thickness(0));
            title.TextDecorations = block.Done ? TextDecorations.Strikethrough : null;
            Grid.SetColumn(title, 1);
            grid.Children.Add(title);

            row.Child = grid;
            return row;
        }

        private void OpenPlan_Click(object sender, RoutedEventArgs e) => openTodayPlan();

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
            if (empty && isToday && !todayPlanned)
                content.Children.Add(Hint(Loc.T("Home.Week.NothingToday"), new Thickness(0, 6, 0, 0)));
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
                ToolTip = Loc.T("Home.Week.OpenInCalendar")
            };

            TextBlock number = new TextBlock
            {
                Text = date.Day.ToString(Loc.Culture),
                FontFamily = (FontFamily)FindResource("DisplayFont"),
                FontSize = 24,
                FontWeight = isToday ? FontWeights.Normal : FontWeights.Light,
                Foreground = numberBrush,
                MinWidth = 28
            };
            Typography.SetNumeralAlignment(number, FontNumeralAlignment.Tabular);

            TextBlock weekday = new TextBlock
            {
                Text = isToday ? Loc.T("Common.Today") : date.ToString("ddd", Loc.Culture),
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
                TextBox time = RowText(item.Time?.ToString("HH:mm", Loc.Culture) ?? "", 13, Res("TextSub"), menu);
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

        // ---- Upcoming expiries --------------------------------------------

        private void ShowExpiries(DashboardData data, DateTime today)
        {
            ExpiryRows.Children.Clear();
            ExpirySection.Visibility = data.Expiries.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

            int past = data.Expiries.Count(x => x.Date < today);
            int coming = data.Expiries.Count - past;
            // Each part is a whole clause with its own count, so each language agrees its words with that count.
            string? pastPart = past == 0 ? null : Loc.Plural("Home.Expiries.Past", past);
            string? comingPart = coming == 0 ? null : Loc.Plural("Home.Expiries.Coming", coming, DashboardDataAggregator.ExpiryDays);
            ExpiriesLede.Text = pastPart != null && comingPart != null
                ? Loc.F("Home.Expiries.Pair", pastPart, comingPart)
                : Loc.F("Home.Expiries.Single", pastPart ?? comingPart);

            foreach (DashboardExpiry expiry in data.Expiries)
                ExpiryRows.Children.Add(ExpiryRow(expiry, today));
        }

        // The item and which date, where it is, and how close (highlighted by Expiry, in words too). A click opens it.
        private FrameworkElement ExpiryRow(DashboardExpiry expiry, DateTime today)
        {
            Border row = HoverRow(new Thickness(10, 6, 10, 6));
            row.Margin = new Thickness(-10, 0, 0, 0);
            CardEffects.AttachClick(row, () => navigate(expiry.Target));

            Grid grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel text = new StackPanel();
            text.Children.Add(Caption(expiry.ItemName, 14, Res("TextMain"), FontWeights.Normal, new Thickness(0)));
            text.Children.Add(Caption(expiry.Name + "  ·  " + expiry.MemoryName, 12, Res("TextSub"), FontWeights.Normal, new Thickness(0, 1, 0, 0)));
            grid.Children.Add(text);

            ExpiryLevel level = Expiry.Level(expiry.Date, today);
            TextBlock when = Caption(Expiry.Describe(expiry.Date, today), 13, Expiry.Brush(level), Expiry.Weight(level),
                new Thickness(16, 1, 0, 0));
            when.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(when, 1);
            grid.Children.Add(when);

            row.Child = grid;
            return row;
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
