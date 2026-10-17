using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace TidyMind
{
    // One day's plan (DayPlanStore): its blocks as a timeline or a list, and the to-dos not on it yet. Built fresh
    // on every visit and redrawn from the files after every change, like Home.
    public partial class DayPlanView : UserControl
    {
        private const double PerMinute = 1.0;      // the timeline: 60 px an hour
        private const double Gutter = 58;          // the hour labels
        private const double RightPad = 10;
        private const int Snap = 15;               // dragging moves and sizes in quarter hours
        private const double EdgeGrip = 6;         // px at a block's top and bottom edge that resize it
        private const int DefaultScroll = 6 * 60;  // where an empty day other than today opens
        private const string TodoFormat = "TidyMind.PlanTodo";

        private const string LinkGlyph = "\uE71B";  // Segoe MDL2: Link
        private const string BellGlyph = "\uEA8F";  // Segoe MDL2: Ringer
        private const string AddGlyph = "\uE710";   // Segoe MDL2: Add

        // Timeline or List, kept while TidyMind runs.
        private static bool showList;

        private readonly IList<Profile> memories;
        private readonly DispatcherTimer clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        private DateTime date;
        private Guid? focusBlock;  // focused after the next redraw (a search result, a block just made or moved)
        private bool scrolled;     // the timeline has been scrolled to this day's starting place

        private bool readable;
        private List<DayPlanBlock> blocks = new List<DayPlanBlock>();
        private List<PlanTodo> todos = new List<PlanTodo>();
        private List<string> unusableTodos = new List<string>();
        private HashSet<Guid> reminders = new HashSet<Guid>();
        private readonly ItemSelection<Guid> selection;

        public DayPlanView(IList<Profile> memories, DateTime date, Guid? select = null)
        {
            InitializeComponent();
            this.memories = memories;
            this.date = date.Date;
            focusBlock = select;
            Surface.Height = DayPlanBlock.DayEnd * PerMinute;
            // By Id: every redraw reads the blocks again. A block's list row and timeline card are one item.
            selection = new ItemSelection<Guid>(Sheet, DeleteSelected, PaintSelected);
            selection.ClearOnBlankClick(Sheet);
            selection.SelectionChanged += UpdateSelectionBar;
            SelectionBar.DeleteClicked += () => selection.DeleteSelected();
            SelectionBar.CancelClicked += selection.ClearSelection;

            clock.Tick += (s, e) => PlaceNowLine();
            Loaded += (s, e) => clock.Start();
            Unloaded += (s, e) => clock.Stop();
            Loc.Track(this, Refresh);

            Refresh();
            (showList ? ListButton : TimelineButton).IsChecked = true;
        }

        // Reads everything again and redraws: the plan, the to-dos, which blocks have a reminder.
        private void Refresh()
        {
            readable = DayPlanStore.TryLoad(out DayPlanFile plan);
            blocks = DayPlanStore.BlocksOn(plan, date);
            todos = DayPlanTodos.All(memories, out unusableTodos);
            reminders = ReminderManager.TryLoadReminders().Select(r => r.Id).ToHashSet();

            ShowHeader();
            ShowBlocks();
            ShowTodos();
            ShowMode();
        }

        // Both views, so the selection always knows every block's list row and timeline card.
        private void ShowBlocks()
        {
            selection.BeginRender();
            ShowList();
            ShowTimeline();
            selection.EndRender();
        }

        private void ShowDate(DateTime day)
        {
            date = day.Date;
            scrolled = false;
            Refresh();
        }

        private PlanTodo? Linked(DayPlanBlock block) => DayPlanTodos.Find(todos, block.Todo);

        // A linked block is done when its to-do is, wherever that was ticked.
        private bool IsDone(DayPlanBlock block) => Linked(block)?.Done ?? block.Done;

        // ---- Header -------------------------------------------------------

        private void ShowHeader()
        {
            DateTime today = DateTime.Today;
            DateText.Text = date.ToString(Loc.T("Format.WeekdayDayMonth"), Loc.Culture);
            WhenText.Text = date == today ? Loc.T("Common.Today") : date == today.AddDays(1) ? Loc.T("Common.Tomorrow")
                : date == today.AddDays(-1) ? Loc.T("Common.Yesterday") : date.Year.ToString(CultureInfo.InvariantCulture);
            TodayButton.IsEnabled = date != today;
            AddButton.IsEnabled = readable;

            ErrorText.Visibility = readable ? Visibility.Collapsed : Visibility.Visible;
            ErrorText.Text = Loc.F("Plan.Unreadable", DayPlanStore.FilePath);

            if (!readable)
                SummaryText.Text = "";
            else if (blocks.Count == 0)
                SummaryText.Text = Loc.T("Plan.Summary.Empty");
            else
            {
                int done = blocks.Count(IsDone);
                int overlapping = blocks.Count(b => blocks.Any(o => o != b && o.Overlaps(b)));
                // Separate facts, each with its own count, side by side.
                List<string> facts = new List<string>
                {
                    Loc.Plural("Plan.Summary.Blocks", blocks.Count),
                    Loc.F("Plan.Summary.Planned", Duration(PlannedMinutes()))
                };
                if (done > 0)
                    facts.Add(Loc.Plural("Plan.Summary.Done", done));
                if (overlapping > 0)
                    facts.Add(Loc.Plural("Plan.Summary.Overlapping", overlapping));
                SummaryText.Text = string.Join("  ·  ", facts);
            }
        }

        // The time the blocks cover, overlaps counted once.
        private int PlannedMinutes()
        {
            int total = 0, runStart = 0, runEnd = -1;
            foreach (DayPlanBlock block in blocks)
            {
                if (block.StartMinute >= runEnd)
                {
                    total += Math.Max(0, runEnd - runStart);
                    runStart = block.StartMinute;
                }
                runEnd = Math.Max(runEnd, block.EndMinute);
            }
            return total + Math.Max(0, runEnd - runStart);
        }

        private static string Duration(int minutes) =>
            minutes < 60 ? Loc.F("Plan.Duration.Minutes", minutes)
            : minutes % 60 == 0 ? Loc.F("Plan.Duration.Hours", minutes / 60)
            : Loc.F("Plan.Duration.HoursMinutes", minutes / 60, minutes % 60);

        private const double NarrowBelow = 920; // narrower than this, the header buttons go under the day

        private void View_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            bool narrow = e.NewSize.Width < NarrowBelow;
            foreach (FrameworkElement place in new FrameworkElement[] { HeaderButtons, SelectionBar })
            {
                Grid.SetRow(place, narrow ? 1 : 0);
                Grid.SetColumn(place, narrow ? 0 : 1);
                place.HorizontalAlignment = narrow ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
                place.Margin = narrow ? new Thickness(-6, 12, 0, 0) : new Thickness(16, 0, 0, 2);
            }
        }

        private void PrevDay_Click(object sender, RoutedEventArgs e) => ShowDate(date.AddDays(-1));
        private void NextDay_Click(object sender, RoutedEventArgs e) => ShowDate(date.AddDays(1));
        private void Today_Click(object sender, RoutedEventArgs e) => ShowDate(DateTime.Today);

        private void ViewMode_Checked(object sender, RoutedEventArgs e)
        {
            showList = ListButton.IsChecked == true;
            ShowMode();
        }

        private void ShowMode()
        {
            TimelineScroll.Visibility = readable && !showList ? Visibility.Visible : Visibility.Collapsed;
            ListScroll.Visibility = readable && showList ? Visibility.Visible : Visibility.Collapsed;
            FocusPending();
        }

        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            int start = DayPlanActions.DefaultStart(date);
            NewBlock(start, start + DayPlanActions.DefaultLength);
        }

        // ---- Shared by both views -----------------------------------------

        private List<PlanTodo> Choices() => DayPlanTodos.Unscheduled(todos, blocks, date);

        private void NewBlock(int start, int end)
        {
            if (DayPlanBlockDialog.Show(this, null, date, start, end, memories, Choices(), null, false)
                != DayPlanBlockDialog.Outcome.None)
                Refresh();
        }

        private void Edit(DayPlanBlock block)
        {
            // Read now: a reminder that has fired since the last redraw is gone.
            bool reminderSet = ReminderManager.TryLoadReminders().Any(r => r.Id == block.Id);
            DayPlanBlockDialog.Outcome outcome = DayPlanBlockDialog.Show(this, block, date, block.StartMinute,
                block.EndMinute, memories, Choices(), Linked(block), reminderSet);
            if (outcome == DayPlanBlockDialog.Outcome.None)
                return;
            if (outcome == DayPlanBlockDialog.Outcome.Saved)
                focusBlock = block.Id;
            Refresh();
        }

        // ---- Several blocks at once ---------------------------------------

        // While any block is selected, the selection bar stands in for the header's buttons.
        private void UpdateSelectionBar()
        {
            bool any = selection.Count > 0;
            SelectionBar.Show(selection.Count);
            SelectionBar.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            HeaderButtons.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        }

        // List row: the selected fill and edge, else the hover tint and the focus edge as before.
        private static void PaintListRow(Border row, bool on) =>
            SelectionLook.PaintRow(row, on, row.IsMouseOver ? Res("BgSubtle") : Brushes.Transparent,
                row.IsKeyboardFocused ? Res("Accent") : Brushes.Transparent);

        // Timeline card: the accent edge (also its focus look) over its own color, and a check first in its row of
        // marks (before the link, bell and done box), so nothing on the card is covered.
        private void PaintSelected(FrameworkElement element, bool on)
        {
            Border box = (Border)element;
            if (box.Parent != Surface)
            {
                PaintListRow(box, on);
                return;
            }
            box.BorderBrush = on || box.IsKeyboardFocused ? Res("Accent") : Brushes.Transparent;
            StackPanel marks = ((Grid)box.Child).Children.OfType<StackPanel>().First(p => Grid.GetColumn(p) == 2);
            FrameworkElement? badge = marks.Children.OfType<FrameworkElement>().FirstOrDefault(c => c.Tag as string == SelectedBadge);
            if (on && badge == null)
            {
                badge = SelectionLook.CheckBadge(16);
                badge.Tag = SelectedBadge;
                badge.VerticalAlignment = VerticalAlignment.Top;
                badge.Margin = new Thickness(0, 0, 4, 0);
                marks.Children.Insert(0, badge);
            }
            else if (!on && badge != null)
                marks.Children.Remove(badge);
        }

        private const string SelectedBadge = "SelectedBadge";

        // A selected row's × takes the whole selection along; otherwise just that block goes.
        private void DeleteFromRow(DayPlanBlock block)
        {
            if (selection.IsSelected(block.Id) && selection.Count > 1)
                selection.DeleteSelected();
            else
                Delete(block);
        }

        // One question for all of them; each goes the way one does (DayPlanActions). Redrawn either way: a reminder
        // may have gone even where its block couldn't.
        // One selected block goes the way Delete on it always went. Several: the keyboard stays in the plan, on the
        // first block after them, else the one before them, else Add block.
        private bool DeleteSelected(IReadOnlyList<Guid> ids)
        {
            List<DayPlanBlock> doomed = blocks.Where(b => ids.Contains(b.Id)).ToList();
            if (doomed.Count == 1)
            {
                Delete(doomed[0]);
                return !blocks.Any(b => b.Id == doomed[0].Id);
            }
            int first = blocks.FindIndex(b => ids.Contains(b.Id)), last = blocks.FindLastIndex(b => ids.Contains(b.Id));
            DayPlanBlock? neighbour = blocks.Skip(last + 1).FirstOrDefault(b => !ids.Contains(b.Id))
                ?? blocks.Take(first).LastOrDefault();
            bool deleted = DayPlanActions.ConfirmDelete(doomed);
            focusBlock = deleted ? neighbour?.Id : null;
            Refresh();
            if (deleted && neighbour == null)
                AddButton.Focus();
            return deleted;
        }

        // The keyboard stays in the plan: on the next block, else the one before, else Add block.
        private void Delete(DayPlanBlock block)
        {
            if (!DayPlanActions.ConfirmDelete(block))
                return;
            int at = blocks.FindIndex(b => b.Id == block.Id);
            DayPlanBlock? neighbour = blocks.Skip(at + 1).FirstOrDefault() ?? (at > 0 ? blocks[at - 1] : null);
            focusBlock = neighbour?.Id;
            Refresh();
            if (neighbour == null)
                AddButton.Focus();
        }

        private void SetDone(DayPlanBlock block, bool done)
        {
            DayPlanActions.SetDone(block, done, Linked(block), memories);
            focusBlock = block.Id;
            Refresh(); // also when it failed: the tick goes back to what's stored
        }

        private void Add(PlanTodo todo, int start)
        {
            DayPlanBlock? added = DayPlanActions.AddFromTodo(todo, date, start);
            if (added != null)
                focusBlock = added.Id;
            Refresh();
        }

        private ContextMenu Menu(DayPlanBlock block)
        {
            bool done = IsDone(block);
            MenuItem edit = new MenuItem { Header = Loc.T("Common.Edit") };
            edit.Click += (s, e) => Edit(block);
            MenuItem tick = new MenuItem { Header = Loc.T(done ? "Plan.MarkNotDone" : "Plan.MarkDone") };
            tick.Click += (s, e) => SetDone(block, !done);
            MenuItem delete = new MenuItem { Header = Loc.T("Common.Delete") };
            delete.Click += (s, e) => Delete(block);

            ContextMenu menu = new ContextMenu();
            menu.Items.Add(edit);
            menu.Items.Add(tick);
            menu.Items.Add(delete);
            menu.Items.Add(new Separator());
            menu.Items.Add(TextCopy.CopyItem(() => block.TimeRange() + "  " + block.Title
                + (string.IsNullOrWhiteSpace(block.Notes) ? "" : Environment.NewLine + block.Notes)));
            return menu;
        }

        // Title, time, notes, its to-do and reminder: everything the block can't always show.
        private object Tip(DayPlanBlock block)
        {
            StackPanel tip = new StackPanel { MaxWidth = 300, Margin = new Thickness(2) };
            tip.Children.Add(new TextBlock { Text = block.Title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            tip.Children.Add(new TextBlock { Text = IsDone(block) ? Loc.F("Plan.TimeDone", block.TimeRange()) : block.TimeRange(), Opacity = 0.8 });
            if (!string.IsNullOrWhiteSpace(block.Notes))
                tip.Children.Add(new TextBlock { Text = block.Notes, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
            foreach (string line in Details(block))
                tip.Children.Add(new TextBlock { Text = line, Opacity = 0.8, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
            return tip;
        }

        // The block's to-do, reminder and overlaps, one short line each.
        private List<string> Details(DayPlanBlock block)
        {
            List<string> lines = new List<string>();
            PlanTodo? todo = Linked(block);
            if (todo != null)
                lines.Add(Loc.F("Plan.Detail.Todo", todo.Text, todo.Where));
            else if (block.Todo != null)
                lines.Add(Loc.T("Plan.Detail.TodoMissing"));
            if (reminders.Contains(block.Id))
                lines.Add(Loc.F("Plan.Detail.Reminder", DayPlanReminders.LeadText(block.RemindMinutesBefore ?? 0).ToLower(Loc.Culture)));
            if (blocks.Any(o => o != block && o.Overlaps(block)))
                lines.Add(Loc.T("Plan.Detail.Overlaps"));
            return lines;
        }

        // After a redraw: the block that was just made, moved, edited or searched for gets the focus, in view.
        private void FocusPending()
        {
            if (focusBlock == null)
                return;
            Panel host = showList ? ListPanel : Surface;
            FrameworkElement? target = host.Children.OfType<FrameworkElement>()
                .FirstOrDefault(c => c.Tag is DayPlanBlock b && b.Id == focusBlock);
            if (target == null)
                return;
            focusBlock = null;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                target.BringIntoView();
                target.Focus();
            }), DispatcherPriority.Loaded);
        }

        // A block's keys: Enter edits, Delete deletes (asking first).
        private void BlockKeys(FrameworkElement element, DayPlanBlock block)
        {
            element.KeyDown += (s, e) =>
            {
                if (e.OriginalSource != element)
                    return;
                if (e.Key == Key.Enter)
                    Edit(block);
                else if (e.Key == Key.Delete)
                    Delete(block);
                else
                    return;
                e.Handled = true;
            };
        }

        // A color this version can't read shows as the default.
        private static Color ColorOf(DayPlanBlock block) => ProjectCard.ParseColor(block.ColorHex) ?? CardEffects.Accent.Color;

        // The color at that strength on white.
        private static Color Tint(Color color, double amount)
        {
            byte Mix(byte channel) => (byte)Math.Round(255 + (channel - 255) * amount);
            return Color.FromRgb(Mix(color.R), Mix(color.G), Mix(color.B));
        }

        private static TextBlock Glyph(string glyph, string tip) => new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 10,
            Foreground = Res("TextSub"),
            Margin = new Thickness(0, 2, 4, 0),
            ToolTip = tip
        };

        private static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

        // ---- Timeline -----------------------------------------------------

        private enum DragMode { None, Create, Move, Top, Bottom }

        private DragMode drag;
        private bool dragging;          // moved past the drag threshold
        private Point dragFrom;
        private int dragAnchor;         // Create: the quarter hour the drag started in
        private int dragStart, dragEnd; // the times the drag would give
        private Border? preview;        // a block being drawn, or a to-do being dropped
        private FrameworkElement? nowLine;

        private void Surface_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.WidthChanged)
                ShowBlocks();
        }

        private void ShowTimeline()
        {
            Surface.Children.Clear();
            preview = null;
            nowLine = null;
            double width = Surface.ActualWidth;
            if (width <= Gutter + RightPad)
                return; // not laid out yet: SizeChanged comes back

            for (int hour = 0; hour <= 24; hour++)
                AddHourLine(hour, width);

            double area = width - Gutter - RightPad;
            Dictionary<Guid, (int Column, int Columns)> columns = Columns(blocks);
            foreach (DayPlanBlock block in blocks)
            {
                (int column, int count) = columns[block.Id];
                double height = Math.Max((block.EndMinute - block.StartMinute) * PerMinute - 2, 8);
                FrameworkElement card = BlockCard(block, height);
                card.Width = Math.Max(area / count - 3, 12);
                card.Height = height;
                Canvas.SetLeft(card, Gutter + area * column / count);
                Canvas.SetTop(card, block.StartMinute * PerMinute + 1);
                Surface.Children.Add(card);
            }

            PlaceNowLine();
            if (!scrolled)
            {
                scrolled = true;
                int first = date == DateTime.Today ? (int)DateTime.Now.TimeOfDay.TotalMinutes - 60
                    : blocks.Count > 0 ? blocks[0].StartMinute - 30 : DefaultScroll;
                Dispatcher.BeginInvoke(new Action(() => TimelineScroll.ScrollToVerticalOffset(Math.Max(0, first) * PerMinute)),
                    DispatcherPriority.Loaded);
            }
            FocusPending();
        }

        private void AddHourLine(int hour, double width)
        {
            double y = hour * 60 * PerMinute;
            Surface.Children.Add(new Rectangle
            {
                Width = width - Gutter - RightPad + 6,
                Height = 1,
                Fill = Res("Border"),
                IsHitTestVisible = false,
                Margin = new Thickness(Gutter - 6, y, 0, 0)
            });
            if (hour < 24)
            {
                Surface.Children.Add(new Rectangle
                {
                    Width = width - Gutter - RightPad,
                    Height = 1,
                    Fill = Res("BgSubtle"),
                    IsHitTestVisible = false,
                    Margin = new Thickness(Gutter, y + 30 * PerMinute, 0, 0)
                });
            }
            TextBlock label = new TextBlock
            {
                Text = DayPlanBlock.Clock(hour * 60),
                FontSize = 11,
                Foreground = Res("TextSub"),
                IsHitTestVisible = false,
                Margin = new Thickness(12, y - 8, 0, 0)
            };
            Typography.SetNumeralAlignment(label, FontNumeralAlignment.Tabular);
            Surface.Children.Add(label);
        }

        // Today: a line across the timeline at the current time.
        private void PlaceNowLine()
        {
            if (nowLine != null)
                Surface.Children.Remove(nowLine);
            nowLine = null;
            if (date != DateTime.Today || Surface.ActualWidth <= Gutter + RightPad)
                return;

            Brush accent = Res("Accent");
            Grid line = new Grid { IsHitTestVisible = false, Width = Surface.ActualWidth - Gutter - RightPad + 8 };
            line.Children.Add(new Rectangle { Height = 2, Fill = accent, VerticalAlignment = VerticalAlignment.Center });
            line.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = accent, HorizontalAlignment = HorizontalAlignment.Left });
            Canvas.SetLeft(line, Gutter - 8);
            Canvas.SetTop(line, DateTime.Now.TimeOfDay.TotalMinutes * PerMinute - 4);
            Panel.SetZIndex(line, 2);
            AutomationProperties.SetName(line, Loc.T("Plan.Now"));
            Surface.Children.Add(line);
            nowLine = line;
        }

        // Overlapping blocks share the width side by side: each run of blocks that overlap one another gets as many
        // columns as it needs at once, and every block takes the first column that is free when it starts.
        private static Dictionary<Guid, (int Column, int Columns)> Columns(List<DayPlanBlock> sorted)
        {
            Dictionary<Guid, (int, int)> result = new Dictionary<Guid, (int, int)>();
            List<(DayPlanBlock Block, int Column)> run = new List<(DayPlanBlock, int)>();
            List<int> columnEnds = new List<int>();
            int runEnd = -1;

            void CloseRun()
            {
                foreach ((DayPlanBlock block, int column) in run)
                    result[block.Id] = (column, columnEnds.Count);
                run.Clear();
                columnEnds.Clear();
            }

            foreach (DayPlanBlock block in sorted)
            {
                if (block.StartMinute >= runEnd)
                    CloseRun();
                int column = columnEnds.FindIndex(end => end <= block.StartMinute);
                if (column < 0)
                {
                    column = columnEnds.Count;
                    columnEnds.Add(block.EndMinute);
                }
                else
                    columnEnds[column] = block.EndMinute;
                run.Add((block, column));
                runEnd = Math.Max(runEnd, block.EndMinute);
            }
            CloseRun();
            return result;
        }

        private FrameworkElement BlockCard(DayPlanBlock block, double height)
        {
            Color color = ColorOf(block);
            bool done = IsDone(block);

            Border card = new Border
            {
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Tint(color, done ? 0.08 : 0.18)), // opaque: hour lines don't show through
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(1.5),
                Focusable = true,
                FocusVisualStyle = null,
                ClipToBounds = true,
                Tag = block,
                ToolTip = Tip(block),
                ContextMenu = Menu(block)
            };
            ToolTipService.SetInitialShowDelay(card, 500);
            AutomationProperties.SetName(card, Loc.F(done ? "Plan.BlockNameDone" : "Plan.BlockName", block.Title, block.TimeRange()));
            Panel.SetZIndex(card, 1);

            Grid grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(new Rectangle { Fill = new SolidColorBrush(color), Opacity = done ? 0.45 : 1 });

            Brush ink = Res(done ? "TextSub" : "TextMain");
            TextBlock title = new TextBlock
            {
                FontSize = 12,
                Foreground = ink,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextDecorations = done ? TextDecorations.Strikethrough : null,
                Margin = new Thickness(7, height >= 34 ? 3 : 0, 4, 0),
                VerticalAlignment = height >= 34 ? VerticalAlignment.Top : VerticalAlignment.Center
            };
            if (height >= 34)
            {
                // Tall enough for the title (wrapping while there's room) and the time under it.
                title.FontWeight = FontWeights.SemiBold;
                title.Text = block.Title;
                title.TextWrapping = TextWrapping.Wrap;
                title.MaxHeight = Math.Max(16, height - 22);
                StackPanel words = new StackPanel();
                words.Children.Add(title);
                words.Children.Add(new TextBlock
                {
                    Text = block.TimeRange(),
                    FontSize = 11,
                    Foreground = Res("TextSub"),
                    Margin = new Thickness(7, 1, 4, 0)
                });
                Grid.SetColumn(words, 1);
                grid.Children.Add(words);
            }
            else
            {
                // A short block: its start and title on one line.
                title.Inlines.Add(new Run(DayPlanBlock.Clock(block.StartMinute) + "  ") { Foreground = Res("TextSub") });
                title.Inlines.Add(new Run(block.Title) { FontWeight = FontWeights.SemiBold });
                Grid.SetColumn(title, 1);
                grid.Children.Add(title);
            }

            StackPanel marks = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = height >= 34 ? VerticalAlignment.Top : VerticalAlignment.Center,
                Margin = new Thickness(0, height >= 34 ? 3 : 0, 4, 0)
            };
            if (Linked(block) != null)
                marks.Children.Add(Glyph(LinkGlyph, Loc.T("Plan.Mark.Linked")));
            if (reminders.Contains(block.Id))
                marks.Children.Add(Glyph(BellGlyph, Loc.T("Plan.Mark.Reminder")));
            if (height >= 18)
            {
                CheckBox check = new CheckBox
                {
                    IsChecked = done,
                    ToolTip = Loc.T(done ? "Plan.Done" : "Plan.MarkDone"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(2, 0, 0, 0)
                };
                check.Click += (s, e) => SetDone(block, check.IsChecked == true);
                marks.Children.Add(check);
            }
            Grid.SetColumn(marks, 2);
            grid.Children.Add(marks);
            card.Child = grid;

            card.GotKeyboardFocus += (s, e) => card.BorderBrush = Res("Accent");
            card.LostKeyboardFocus += (s, e) => card.BorderBrush = selection.IsSelected(block.Id) ? Res("Accent") : Brushes.Transparent;
            selection.Attach(card, block.Id); // before the drag: a Ctrl or Shift click selects, and neither moves nor sizes
            BlockKeys(card, block);
            AttachBlockDrag(card, block);
            return card;
        }

        // ---- Timeline: dragging blocks ------------------------------------

        // Inside a block: its top and bottom edges resize it, the rest moves it. A double-click edits.
        private void AttachBlockDrag(Border card, DayPlanBlock block)
        {
            card.MouseLeftButtonDown += (s, e) =>
            {
                card.Focus();
                e.Handled = true;
                if (e.ClickCount == 2)
                {
                    Edit(block);
                    return;
                }

                double y = e.GetPosition(card).Y;
                double grip = Grip(card);
                drag = y <= grip ? DragMode.Top : y >= card.ActualHeight - grip ? DragMode.Bottom : DragMode.Move;
                dragging = false;
                dragFrom = e.GetPosition(Surface);
                dragStart = block.StartMinute;
                dragEnd = block.EndMinute;
                card.CaptureMouse();
            };

            card.MouseMove += (s, e) =>
            {
                if (drag == DragMode.None || !card.IsMouseCaptured)
                {
                    double y = e.GetPosition(card).Y;
                    card.Cursor = y <= Grip(card) || y >= card.ActualHeight - Grip(card) ? Cursors.SizeNS : Cursors.SizeAll;
                    return;
                }

                Point at = e.GetPosition(Surface);
                if (!dragging && Math.Abs(at.Y - dragFrom.Y) < SystemParameters.MinimumVerticalDragDistance)
                    return;
                dragging = true;

                int delta = (int)Math.Round((at.Y - dragFrom.Y) / PerMinute);
                int length = block.EndMinute - block.StartMinute;
                switch (drag)
                {
                    case DragMode.Move:
                        dragStart = Clamp(SnapRound(block.StartMinute + delta), 0, DayPlanBlock.DayEnd - length);
                        dragEnd = dragStart + length;
                        break;
                    case DragMode.Top:
                        dragStart = Clamp(SnapRound(block.StartMinute + delta), 0, block.EndMinute - DayPlanBlock.MinLength);
                        break;
                    case DragMode.Bottom:
                        dragEnd = Clamp(SnapRound(block.EndMinute + delta), block.StartMinute + DayPlanBlock.MinLength, DayPlanBlock.DayEnd);
                        break;
                }
                Canvas.SetTop(card, dragStart * PerMinute + 1);
                card.Height = Math.Max((dragEnd - dragStart) * PerMinute - 2, 8);
                ShowPreview(dragStart, dragEnd, onlyLabel: true);
                AutoScroll(e.GetPosition(TimelineScroll).Y);
            };

            card.MouseLeftButtonUp += (s, e) =>
            {
                if (drag == DragMode.None || !card.IsMouseCaptured)
                    return;
                e.Handled = true;
                bool moved = dragging && (dragStart != block.StartMinute || dragEnd != block.EndMinute);
                int start = dragStart, end = dragEnd;
                EndDrag(card);
                if (!moved)
                    return;
                if (DayPlanActions.Move(block, start, end))
                    focusBlock = block.Id;
                Refresh();
            };

            // Taken away mid-drag (another window, Alt+Tab): nothing moves.
            card.LostMouseCapture += (s, e) =>
            {
                if (drag == DragMode.None)
                    return;
                EndDrag(card);
                ShowBlocks();
            };
        }

        // A short block keeps some middle to grab for moving.
        private static double Grip(FrameworkElement card) => Math.Min(EdgeGrip, card.ActualHeight / 4);

        private void EndDrag(UIElement captured)
        {
            drag = DragMode.None;
            dragging = false;
            HidePreview();
            if (captured.IsMouseCaptured)
                captured.ReleaseMouseCapture();
        }

        // ---- Timeline: drawing a new block --------------------------------

        private void Surface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            Point at = e.GetPosition(Surface);
            if (e.OriginalSource != Surface || at.X < Gutter)
                return;
            drag = DragMode.Create;
            dragging = false;
            dragFrom = at;
            dragAnchor = Clamp(SnapDown(Minute(at)), 0, DayPlanBlock.DayEnd - Snap);
            Surface.CaptureMouse();
            e.Handled = true;
        }

        private void Surface_MouseMove(object sender, MouseEventArgs e)
        {
            if (drag != DragMode.Create)
                return;
            Point at = e.GetPosition(Surface);
            if (!dragging && Math.Abs(at.Y - dragFrom.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;
            dragging = true;

            int minute = Clamp(Minute(at), 0, DayPlanBlock.DayEnd);
            if (minute >= dragAnchor)
            {
                dragStart = dragAnchor;
                dragEnd = Math.Min(Math.Max(SnapUp(minute), dragAnchor + Snap), DayPlanBlock.DayEnd);
            }
            else
            {
                dragStart = SnapDown(minute);
                dragEnd = dragAnchor + Snap;
            }
            ShowPreview(dragStart, dragEnd);
            AutoScroll(e.GetPosition(TimelineScroll).Y);
        }

        // A drag opens the editor for that time (a quarter hour at least); a plain click does nothing.
        private void Surface_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (drag != DragMode.Create)
                return;
            bool drawn = dragging;
            int start = dragStart, end = dragEnd;
            EndDrag(Surface);
            if (drawn)
                NewBlock(start, end);
        }

        private void Surface_LostMouseCapture(object sender, MouseEventArgs e)
        {
            if (drag == DragMode.Create)
                EndDrag(Surface);
        }

        // ---- Timeline: dropping a to-do -----------------------------------

        private void Surface_DragOver(object sender, DragEventArgs e)
        {
            e.Handled = true;
            if (!e.Data.GetDataPresent(TodoFormat))
            {
                e.Effects = DragDropEffects.None;
                return;
            }
            int start = DropStart(e.GetPosition(Surface));
            ShowPreview(start, start + DayPlanActions.DefaultLength);
            AutoScroll(e.GetPosition(TimelineScroll).Y);
            e.Effects = DragDropEffects.Copy;
        }

        private void Surface_DragLeave(object sender, DragEventArgs e) => HidePreview();

        private void Surface_Drop(object sender, DragEventArgs e)
        {
            HidePreview();
            if (e.Data.GetData(TodoFormat) is PlanTodo todo)
                Add(todo, DropStart(e.GetPosition(Surface)));
            e.Handled = true;
        }

        private static int DropStart(Point at) => Clamp(SnapDown(Minute(at)), 0, DayPlanBlock.DayEnd - DayPlanActions.DefaultLength);

        // ---- Timeline: helpers --------------------------------------------

        // The time a drag would give. onlyLabel: a block is being moved, which shows itself; just say the time.
        private void ShowPreview(int start, int end, bool onlyLabel = false)
        {
            if (preview == null)
            {
                preview = new Border
                {
                    CornerRadius = new CornerRadius(6),
                    BorderThickness = new Thickness(1),
                    IsHitTestVisible = false,
                    Child = new TextBlock { FontSize = 11, Foreground = Res("Accent"), Margin = new Thickness(8, 3, 8, 0) }
                };
                Panel.SetZIndex(preview, 3);
                Surface.Children.Add(preview);
            }
            preview.Background = onlyLabel ? Brushes.Transparent : Res("AccentTint");
            preview.BorderBrush = onlyLabel ? Brushes.Transparent : Res("Accent");
            ((TextBlock)preview.Child).Text = DayPlanBlock.Clock(start) + "–" + DayPlanBlock.Clock(end);
            ((TextBlock)preview.Child).Margin = onlyLabel ? new Thickness(0) : new Thickness(8, 3, 8, 0);

            if (onlyLabel)
            {
                // In the hour gutter, beside the moving block's top.
                preview.Width = Gutter;
                preview.Height = double.NaN;
                Canvas.SetLeft(preview, 2);
                Canvas.SetTop(preview, start * PerMinute + 8);
            }
            else
            {
                preview.Width = Surface.ActualWidth - Gutter - RightPad;
                preview.Height = Math.Max((end - start) * PerMinute - 2, 8);
                Canvas.SetLeft(preview, Gutter);
                Canvas.SetTop(preview, start * PerMinute + 1);
            }
        }

        private void HidePreview()
        {
            if (preview != null)
                Surface.Children.Remove(preview);
            preview = null;
        }

        // Near the top or bottom edge while dragging, the timeline scrolls on.
        private void AutoScroll(double y)
        {
            const double zone = 28, step = 12;
            if (y < zone)
                TimelineScroll.ScrollToVerticalOffset(TimelineScroll.VerticalOffset - step);
            else if (y > TimelineScroll.ViewportHeight - zone)
                TimelineScroll.ScrollToVerticalOffset(TimelineScroll.VerticalOffset + step);
        }

        private static int Minute(Point at) => (int)Math.Floor(at.Y / PerMinute);
        private static int SnapDown(int minute) => (int)Math.Floor(minute / (double)Snap) * Snap;
        private static int SnapUp(int minute) => (int)Math.Ceiling(minute / (double)Snap) * Snap;
        private static int SnapRound(int minute) => (int)Math.Round(minute / (double)Snap, MidpointRounding.AwayFromZero) * Snap;
        private static int Clamp(int value, int min, int max) => Math.Max(min, Math.Min(value, max));

        // ---- List ---------------------------------------------------------

        // The blocks by start time; an hour or more with nothing planned between them shows as a quiet "free" line.
        private void ShowList()
        {
            ListPanel.Children.Clear();
            if (blocks.Count == 0)
            {
                ListPanel.Children.Add(new TextBlock
                {
                    Text = Loc.T("Plan.List.Empty"),
                    FontSize = 13,
                    Foreground = Res("TextSub"),
                    Margin = new Thickness(8, 6, 0, 0)
                });
                return;
            }

            int reach = -1;
            foreach (DayPlanBlock block in blocks)
            {
                if (reach >= 0 && block.StartMinute - reach >= 60)
                {
                    ListPanel.Children.Add(new TextBlock
                    {
                        Text = Loc.F("Plan.List.Free", DayPlanBlock.Clock(reach), DayPlanBlock.Clock(block.StartMinute)),
                        FontSize = 11,
                        FontStyle = FontStyles.Italic,
                        Foreground = Res("TextSub"),
                        Margin = new Thickness(44, 2, 0, 2)
                    });
                }
                ListPanel.Children.Add(ListRow(block));
                reach = Math.Max(reach, block.EndMinute);
            }
        }

        private FrameworkElement ListRow(DayPlanBlock block)
        {
            bool done = IsDone(block);
            Border row = new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8, 7, 6, 7),
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(1.5),
                Focusable = true,
                FocusVisualStyle = null,
                Tag = block,
                ContextMenu = Menu(block),
                ToolTip = Tip(block)
            };
            ToolTipService.SetInitialShowDelay(row, 500);
            AutomationProperties.SetName(row, Loc.F(done ? "Plan.BlockNameDone" : "Plan.BlockName", block.Title, block.TimeRange()));

            Grid grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            CheckBox check = new CheckBox
            {
                IsChecked = done,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(2, 2, 10, 0)
            };
            AutomationProperties.SetName(check, Loc.T("Plan.Done"));
            check.Click += (s, e) => SetDone(block, check.IsChecked == true);
            grid.Children.Add(check);

            StackPanel time = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
            time.Children.Add(new Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = new SolidColorBrush(ColorOf(block)),
                Margin = new Thickness(0, 1, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            TextBlock range = new TextBlock { Text = block.TimeRange(), FontSize = 13, Foreground = Res("TextSub") };
            Typography.SetNumeralAlignment(range, FontNumeralAlignment.Tabular);
            time.Children.Add(range);
            Grid.SetColumn(time, 1);
            grid.Children.Add(time);

            StackPanel words = new StackPanel();
            words.Children.Add(new TextBlock
            {
                Text = block.Title,
                FontSize = 14,
                Foreground = Res(done ? "TextSub" : "TextMain"),
                TextDecorations = done ? TextDecorations.Strikethrough : null,
                TextWrapping = TextWrapping.Wrap
            });
            List<string> details = Details(block);
            if (!string.IsNullOrWhiteSpace(block.Notes))
                details.Insert(0, block.Notes.Trim().Split('\n')[0].Trim());
            if (details.Count > 0)
            {
                words.Children.Add(new TextBlock
                {
                    Text = string.Join("  ·  ", details),
                    FontSize = 12,
                    Foreground = Res("TextSub"),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(0, 1, 0, 0)
                });
            }
            Grid.SetColumn(words, 2);
            grid.Children.Add(words);

            // Its to-do and reminder marks always; the buttons while the row is hovered or focused, like the to-do
            // lists' row buttons.
            StackPanel marks = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8, 2, 0, 0) };
            if (Linked(block) != null)
                marks.Children.Add(Glyph(LinkGlyph, Loc.T("Plan.Mark.Linked")));
            if (reminders.Contains(block.Id))
                marks.Children.Add(Glyph(BellGlyph, Loc.T("Plan.Mark.Reminder")));
            StackPanel actions = new StackPanel { Orientation = Orientation.Horizontal, Visibility = Visibility.Hidden, Margin = new Thickness(0, -6, 0, 0) };
            marks.Children.Add(actions);
            Button edit = new Button { Style = (Style)FindResource("RowEditStyle"), ToolTip = Loc.T("Common.Edit") };
            AutomationProperties.SetName(edit, Loc.T("Common.Edit"));
            edit.Click += (s, e) => Edit(block);
            Button delete = new Button { Style = (Style)FindResource("RowDeleteStyle") };
            delete.Click += (s, e) => DeleteFromRow(block);
            actions.Children.Add(edit);
            actions.Children.Add(delete);
            Grid.SetColumn(marks, 3);
            grid.Children.Add(marks);

            void UpdateActions() =>
                actions.Visibility = row.IsMouseOver || row.IsKeyboardFocusWithin ? Visibility.Visible : Visibility.Hidden;
            row.MouseEnter += (s, e) => { PaintListRow(row, selection.IsSelected(block.Id)); UpdateActions(); };
            row.MouseLeave += (s, e) => { PaintListRow(row, selection.IsSelected(block.Id)); UpdateActions(); };
            row.IsKeyboardFocusWithinChanged += (s, e) => UpdateActions();
            row.GotKeyboardFocus += (s, e) => PaintListRow(row, selection.IsSelected(block.Id));
            row.LostKeyboardFocus += (s, e) => PaintListRow(row, selection.IsSelected(block.Id));
            selection.Attach(row, block.Id); // first: a Ctrl or Shift click selects, and neither ticks nor edits
            row.MouseLeftButtonDown += (s, e) =>
            {
                row.Focus();
                if (e.ClickCount == 2)
                    Edit(block);
            };
            BlockKeys(row, block);

            row.Child = grid;
            return row;
        }

        // ---- To-dos -------------------------------------------------------

        // The to-dos not on this day's plan, under the list they come from; each can be dragged onto the timeline
        // or added with its + button.
        private void ShowTodos()
        {
            TodoPanel.Children.Clear();
            TodoPanel.Opacity = readable ? 1 : 0.5; // nothing can be planned while the plan can't be read
            List<PlanTodo> open = Choices();
            foreach (IGrouping<string, PlanTodo> group in open.GroupBy(t => t.Where))
            {
                TodoPanel.Children.Add(new TextBlock
                {
                    Text = group.Key.ToUpper(Loc.Culture),
                    FontSize = 10,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Res("TextSub"),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(2, TodoPanel.Children.Count == 0 ? 0 : 14, 0, 4)
                });
                foreach (PlanTodo todo in group)
                    TodoPanel.Children.Add(TodoRow(todo));
            }

            if (open.Count == 0)
                TodoPanel.Children.Add(Hint(Loc.T("Plan.Todos.None")));
            foreach (string list in unusableTodos)
                TodoPanel.Children.Add(Hint(Loc.F("Plan.Todos.Unusable", list)));
        }

        private TextBlock Hint(string text) => new TextBlock
        {
            Text = text,
            FontSize = 12,
            FontStyle = FontStyles.Italic,
            Foreground = Res("TextSub"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 6, 0, 0)
        };

        private FrameworkElement TodoRow(PlanTodo todo)
        {
            Border row = new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8, 6, 4, 6),
                Background = Brushes.Transparent,
                Cursor = readable ? Cursors.SizeAll : null,
                ToolTip = readable ? Loc.T("Plan.Todo.DragTip") : null,
                IsEnabled = readable
            };

            Grid grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            words.Children.Add(new TextBlock
            {
                Text = todo.Text,
                FontSize = 13,
                Foreground = Res("TextMain"),
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 40,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            if (todo.Due != null && todo.Due < date)
            {
                words.Children.Add(new TextBlock
                {
                    Text = Loc.F("Plan.Todo.From", todo.Due.Value.ToString(Loc.T("Format.ShortMonthDay"), Loc.Culture)),
                    FontSize = 11,
                    Foreground = Res("TextSub")
                });
            }
            grid.Children.Add(words);

            Button add = new Button
            {
                Style = (Style)FindResource("HoverIconButtonStyle"),
                Content = AddGlyph,
                ToolTip = Loc.T("Plan.Todo.Add"),
                Focusable = true, // the keyboard's way to plan a to-do
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand
            };
            AutomationProperties.SetName(add, Loc.F("Plan.Todo.AddName", todo.Text));
            add.Click += (s, e) => Add(todo, DayPlanActions.DefaultStart(date));
            Grid.SetColumn(add, 1);
            grid.Children.Add(add);
            row.Child = grid;

            row.MouseEnter += (s, e) => row.Background = Res("BgPanel");
            row.MouseLeave += (s, e) => row.Background = Brushes.Transparent;

            // Dragged past the usual threshold, the to-do travels to the timeline (Surface_Drop).
            Point? pressed = null;
            row.PreviewMouseLeftButtonDown += (s, e) => pressed = add.IsMouseOver ? null : e.GetPosition(row);
            row.PreviewMouseLeftButtonUp += (s, e) => pressed = null;
            row.MouseMove += (s, e) =>
            {
                if (pressed == null || e.LeftButton != MouseButtonState.Pressed)
                    return;
                Vector moved = e.GetPosition(row) - pressed.Value;
                if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance
                    && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
                    return;
                pressed = null;
                DragDrop.DoDragDrop(row, new DataObject(TodoFormat, todo), DragDropEffects.Copy);
            };
            return row;
        }
    }
}
