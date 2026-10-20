using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TidyMind
{
    public partial class RemindersWindow : Window
    {
        private static RemindersWindow instance;

        private Guid? highlightedId;
        private Border highlightedRow;

        // By Id: every render reads reminders.json again.
        private readonly ItemSelection<Guid> selection;
        private readonly Dictionary<Guid, string> shownNames = new Dictionary<Guid, string>();

        private RemindersWindow()
        {
            InitializeComponent();
            selection = new ItemSelection<Guid>(RemindersPanel, DeleteSelected, (row, on) => PaintRow((Border)row, on));
            selection.ClearOnBlankClick(RemindersPanel);
            selection.SelectionChanged += UpdateSelectionBar;
            SelectionBar.DeleteClicked += () => selection.DeleteSelected();
            SelectionBar.CancelClicked += selection.ClearSelection;
            RenderReminders();
            Activated += (s, e) => RenderReminders();
            Loc.Track(this, RenderReminders);
        }

        public static void ShowOrFocus()
        {
            if (instance == null)
            {
                instance = new RemindersWindow();
                instance.Show();
            }
            else
            {
                if (instance.WindowState == WindowState.Minimized)
                    instance.WindowState = WindowState.Normal;

                instance.Activate();
            }
        }

        // Marks one reminder (opened from the dashboard) until the window closes; kept across re-renders.
        public static void Highlight(Guid id)
        {
            if (instance == null) return;

            instance.highlightedId = id;
            instance.RenderReminders();

            // Scrolled to once, here: re-renders on every activation shouldn't keep yanking the list back.
            Border row = instance.highlightedRow;
            if (row != null)
                instance.Dispatcher.BeginInvoke(new Action(row.BringIntoView), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        public static bool IsOpen => instance != null;

        public static void CloseIfOpen() => instance?.Close();

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            instance = null;
        }

        // Esc closes the window; with reminders selected, it drops the selection first.
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape)
                return;
            if (selection.Count > 0)
            {
                selection.ClearSelection();
                e.Handled = true;
            }
            else
                Close();
        }

        private void UpdateSelectionBar()
        {
            bool any = selection.Count > 0;
            SelectionBar.Show(selection.Count);
            SelectionBar.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            AddReminderButton.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        }

        private static readonly Brush RowEdge = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xDE));

        // Selected: the accent fill and edge. Else white, or for the one the dashboard opened its soft fill alone,
        // so it can still be told apart from a selected row.
        private void PaintRow(Border row, bool on)
        {
            Brush rest = row == highlightedRow ? (Brush)FindResource("AccentSoft") : Brushes.White;
            SelectionLook.PaintRow(row, on, rest, RowEdge);
        }

        // One question for all of them; each goes the way one does (ReminderActions).
        private bool DeleteSelected(IReadOnlyList<Guid> ids)
        {
            bool deleted = ReminderActions.ConfirmDelete(this, ids, ids.Select(id => shownNames[id]).ToList());
            RenderReminders();
            return deleted;
        }

        // A row's Delete on one of several selected reminders deletes all of them.
        private void DeleteFromRow(Guid id)
        {
            if (selection.IsSelected(id) && selection.Count > 1)
                selection.DeleteSelected();
            else if (ReminderActions.ConfirmDelete(id))
                RenderReminders();
        }

        private void RenderReminders()
        {
            RemindersPanel.Children.Clear();
            selection.BeginRender();
            shownNames.Clear();
            highlightedRow = null;

            // Unreadable: said so in place of the list, and adding is off (it would fail on the same file anyway).
            // Re-checked on every activation, so a fixed file shows up as soon as the window comes back.
            List<Reminder> reminders;
            string problem = null;
            try
            {
                reminders = ReminderManager.LoadReminders();
                ReminderManager.RepairTasksInBackground(); // throttled; shown reminders should also fire
            }
            catch (Exception e) when (e is IOException || e is JsonException || e is UnauthorizedAccessException)
            {
                reminders = new List<Reminder>();
                problem = e.Message;
            }
            AddReminderButton.IsEnabled = problem == null;
            reminders.Sort((a, b) => a.NextFireTime.CompareTo(b.NextFireTime));

            if (reminders.Count == 0)
            {
                TextBlock empty = new TextBlock();
                empty.Text = problem == null ? Loc.T("Reminders.Empty") : Loc.F("Reminders.Unreadable", problem);
                empty.TextWrapping = TextWrapping.Wrap;
                empty.TextAlignment = TextAlignment.Center;
                empty.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#8A8A87"));
                empty.HorizontalAlignment = HorizontalAlignment.Center;
                empty.Margin = new Thickness(0, 20, 0, 0);
                RemindersPanel.Children.Add(empty);
                selection.EndRender();
                return;
            }

            HashSet<Guid>? blockIds = DayPlanReminders.BlockIds();
            Dictionary<Guid, Deadline>? deadlines = VehicleReminders.DeadlineIndex();
            foreach (Reminder reminder in reminders)
            {
                string message = VehicleReminders.ShownMessage(reminder, deadlines);
                Border row = CreateReminderRow(reminder, message, blockIds);
                RemindersPanel.Children.Add(row);
                shownNames[reminder.Id] = message;

                if (reminder.Id == highlightedId)
                {
                    highlightedRow = row;
                    PaintRow(row, false);
                }
                selection.Attach(row, reminder.Id);
            }
            selection.EndRender();
        }

        private Border CreateReminderRow(Reminder reminder, string message, ISet<Guid>? blockIds)
        {
            Border row = new Border();
            row.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFFFFF"));
            row.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E0E0DE"));
            row.BorderThickness = new Thickness(1);
            row.CornerRadius = new CornerRadius(6);
            row.Padding = new Thickness(14, 10, 14, 10);
            row.Margin = new Thickness(0, 0, 0, 8);

            Grid grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel info = new StackPanel();

            info.Children.Add(TextCopy.Selectable(message, 14, (Brush)FindResource("TextMain"), FontWeights.Bold));

            if (!string.IsNullOrWhiteSpace(reminder.Title))
            {
                TextBox forText = TextCopy.Selectable(Loc.F("Reminders.For", DayPlanReminders.ShownTitle(reminder, blockIds)), 11,
                    (Brush)FindResource("Accent"));
                forText.Margin = new Thickness(0, 4, 0, 0);
                info.Children.Add(forText);
            }

            TextBox details = TextCopy.Selectable(reminder.NextFireTime.ToString(Loc.T("Format.DateTime"), Loc.Culture) + "   •   "
                + ReminderForm.RepeatLabel(reminder.RepeatType), 11, (Brush)FindResource("TextSub"));
            details.Margin = new Thickness(0, 4, 0, 0);
            info.Children.Add(details);

            Grid.SetColumn(info, 0);
            grid.Children.Add(info);

            Button deleteButton = new Button();
            deleteButton.Content = Loc.T("Common.Delete");
            deleteButton.MinWidth = 80;
            deleteButton.Height = 30;
            deleteButton.Style = (Style)Application.Current.FindResource("GhostButtonStyle");
            deleteButton.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#C0392B"));
            deleteButton.VerticalAlignment = VerticalAlignment.Center;
            deleteButton.Tag = reminder.Id;
            deleteButton.Click += DeleteReminder_Click;
            Grid.SetColumn(deleteButton, 1);
            grid.Children.Add(deleteButton);

            row.Child = grid;

            // The same Delete as the button (the texts have their own Copy menu); on one of several selected
            // reminders, ItemSelection puts the menu for all of them in its place.
            MenuItem delete = new MenuItem { Header = Loc.T("Common.Delete") };
            delete.Click += (s, e) => DeleteFromRow(reminder.Id);
            row.ContextMenu = new ContextMenu { Items = { delete, new Separator(), TextCopy.CopyItem(() => reminder.Message) } };
            return row;
        }

        private void DeleteReminder_Click(object sender, RoutedEventArgs e) => DeleteFromRow((Guid)((Button)sender).Tag);

        private void AddReminderButton_Click(object sender, RoutedEventArgs e)
        {
            // Activated re-renders the list once the dialog hands focus back.
            new AddReminderWindow { Owner = this }.ShowDialog();
        }
    }
}
