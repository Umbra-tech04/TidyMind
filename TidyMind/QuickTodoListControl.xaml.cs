using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace TidyMind
{
    // Quick to-dos with an input to add more, grouped overdue → today → tomorrow, each labelled with its day.
    // Group and label come from DateTime.Today on every render, so an unticked "Today" turns into
    // "from yesterday" by itself once the date changes.
    public partial class QuickTodoListControl : UserControl
    {
        private const string UnreadableMessage = "Couldn't save: quicktodos.json can't be read.";

        private enum DueGroup { Overdue, Today, Tomorrow }

        private List<QuickTodo> todos = new List<QuickTodo>();
        private bool showDone;

        // Ticked while this list is on screen: they stay put (struck through), also across refreshes, until the
        // list is created again — so a mis-click can be undone where it happened.
        private readonly HashSet<Guid> completedHere = new HashSet<Guid>();

        // The host refreshes on window activation; this covers the date changing while the app stays in front.
        private readonly DispatcherTimer midnight = new DispatcherTimer();

        public QuickTodoListControl()
        {
            InitializeComponent();
            midnight.Tick += (s, e) => { Refresh(); ScheduleMidnight(); };
            Loaded += (s, e) => { Refresh(); ScheduleMidnight(); };
            Unloaded += (s, e) => midnight.Stop();
        }

        public void Refresh()
        {
            todos = QuickTodoService.GetCurrentList(DateTime.Today);
            Render();
        }

        private void ScheduleMidnight()
        {
            midnight.Stop();
            midnight.Interval = DateTime.Today.AddDays(1) - DateTime.Now + TimeSpan.FromSeconds(1);
            midnight.Start();
        }

        // ---- Adding -------------------------------------------------------

        private void Input_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                AddTodo();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && Input.Text.Length > 0)
            {
                Input.Clear();
                e.Handled = true;
            }
        }

        private void AddButton_Click(object sender, RoutedEventArgs e) => AddTodo();

        private void Input_TextChanged(object sender, TextChangedEventArgs e)
        {
            Placeholder.Visibility = Input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (Input.Text.Length > 0)
                StatusText.Visibility = Visibility.Collapsed;
        }

        private void AddTodo()
        {
            string text = Input.Text.Trim();
            if (text.Length == 0) return;

            DateTime today = DateTime.Today;
            QuickTodo todo = QuickTodoService.Add(text, TomorrowButton.IsChecked == true ? today.AddDays(1) : today);
            if (todo == null)
            {
                ShowUnreadable();
                return;
            }

            Input.Clear();
            todos.Add(todo);
            Render();
            Input.Focus();
        }

        // ---- List ---------------------------------------------------------

        private void Render()
        {
            TodoList.Children.Clear();
            DateTime today = DateTime.Today;

            // Within a group: finished ones last (except those ticked just now, which keep their place),
            // overdue ones oldest first, then in the order they were added.
            List<QuickTodo> shown = todos.Where(IsShown)
                .OrderBy(t => GroupOf(t, today))
                .ThenBy(t => t.IsDone && !completedHere.Contains(t.Id))
                .ThenBy(t => t.DueDate)
                .ThenBy(t => t.CreatedDate)
                .ToList();

            // No group headers — every row carries its day label; a hairline separates the groups.
            for (int i = 0; i < shown.Count; i++)
            {
                if (i > 0 && GroupOf(shown[i], today) != GroupOf(shown[i - 1], today))
                    TodoList.Children.Add(GroupDivider());
                TodoList.Children.Add(CreateRow(shown[i], today));
            }

            UpdateFooter();
        }

        private static FrameworkElement GroupDivider()
        {
            return new Border
            {
                Height = 1,
                Background = new SolidColorBrush(Color.FromRgb(0xEC, 0xEC, 0xEA)),
                Margin = new Thickness(2, 6, 2, 6),
                SnapsToDevicePixels = true
            };
        }

        private static DueGroup GroupOf(QuickTodo todo, DateTime today)
        {
            if (todo.DueDate.Date < today) return DueGroup.Overdue;
            return todo.DueDate.Date == today ? DueGroup.Today : DueGroup.Tomorrow;
        }

        private void UpdateFooter()
        {
            int hiddenDone = todos.Count(t => !IsShown(t));
            DoneToggle.Visibility = showDone || hiddenDone > 0 ? Visibility.Visible : Visibility.Collapsed;
            DoneToggle.Content = showDone ? "Hide done" : "Show done (" + hiddenDone + ")";

            EmptyText.Visibility = TodoList.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyText.Text = hiddenDone > 0 ? "All done for now." : "Nothing on the list.";
        }

        private bool IsShown(QuickTodo todo) => !todo.IsDone || showDone || completedHere.Contains(todo.Id);

        private FrameworkElement CreateRow(QuickTodo todo, DateTime today)
        {
            Grid row = new Grid { MinHeight = 30, Background = Brushes.Transparent };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            TextBlock text = new TextBlock { Text = todo.Text, TextWrapping = TextWrapping.Wrap, FontSize = 13 };

            bool overdue = GroupOf(todo, today) == DueGroup.Overdue;
            TextBlock due = new TextBlock
            {
                Text = DueLabel(todo.DueDate, today),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };
            Grid.SetColumn(due, 1);

            PaintDone(text, due, overdue, todo.IsDone);

            // The text is the checkbox's content, so clicking the words ticks it too.
            CheckBox check = new CheckBox
            {
                Content = text,
                IsChecked = todo.IsDone,
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 4, 8, 4)
            };
            check.Click += (s, e) => SetDone(todo, check, text, due, overdue);
            check.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Delete)
                {
                    DeleteTodo(todo);
                    e.Handled = true;
                }
            };
            row.Children.Add(check);
            row.Children.Add(due);

            // Shown only while the row is hovered or focused, to keep the list calm. (Delete on the checkbox also works.)
            Button delete = new Button
            {
                Style = (Style)FindResource("RowDeleteStyle"),
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Hidden
            };
            delete.Click += (s, e) => DeleteTodo(todo);
            Grid.SetColumn(delete, 2);

            ContextMenu menu = new ContextMenu();
            MenuItem edit = new MenuItem { Header = "Edit" };
            edit.Click += (s, e) => BeginEdit(todo, row, check);
            MenuItem remove = new MenuItem { Header = "Delete" };
            remove.Click += (s, e) => DeleteTodo(todo); // the same as the row's "×"
            menu.Items.Add(edit);
            menu.Items.Add(remove);
            menu.Items.Add(new Separator());
            menu.Items.Add(TextCopy.CopyItem(() => todo.Text));
            row.ContextMenu = menu;
            row.Children.Add(delete);

            void UpdateDeleteVisibility() =>
                delete.Visibility = row.IsMouseOver || row.IsKeyboardFocusWithin ? Visibility.Visible : Visibility.Hidden;
            row.MouseEnter += (s, e) => UpdateDeleteVisibility();
            row.MouseLeave += (s, e) => UpdateDeleteVisibility();
            row.IsKeyboardFocusWithinChanged += (s, e) => UpdateDeleteVisibility();

            return row;
        }

        // Swaps the row's text for a box with the same text: Enter or clicking away saves, Escape cancels.
        // An emptied box cancels too; removing a to-do is what Delete is for.
        private void BeginEdit(QuickTodo todo, Grid row, CheckBox check)
        {
            TextBox editor = new TextBox
            {
                Text = todo.Text,
                FontSize = 13,
                Height = 28,
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 1, 8, 1)
            };
            check.Visibility = Visibility.Collapsed;
            row.Children.Add(editor);

            bool finished = false;
            void Finish(bool save)
            {
                if (finished) return;
                finished = true;

                string text = editor.Text.Trim();
                if (save && text.Length > 0 && text != todo.Text)
                {
                    if (QuickTodoService.Rename(todo.Id, text))
                    {
                        // By Id: a refresh while editing may already have swapped the list for a fresh copy.
                        QuickTodo current = todos.FirstOrDefault(t => t.Id == todo.Id);
                        if (current != null)
                            current.Text = text;
                    }
                    else
                        ShowUnreadable();
                }

                // Deferred: this can run from inside a Render (a refresh pulling the editor out of the list).
                Dispatcher.BeginInvoke(new Action(Render));
            }

            editor.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter || e.Key == Key.Escape)
                {
                    Finish(e.Key == Key.Enter);
                    e.Handled = true;
                }
            };
            // Focused only after the context menu has closed and handed focus back to where it was; until the box
            // has had focus once, losing it means nothing.
            bool focused = false;
            editor.GotKeyboardFocus += (s, e) => focused = true;
            editor.LostKeyboardFocus += (s, e) =>
            {
                if (focused)
                    Finish(true);
            };
            editor.Loaded += (s, e) => Dispatcher.BeginInvoke(new Action(() =>
            {
                editor.Focus();
                editor.SelectAll();
            }), DispatcherPriority.Input);
        }

        // Restyles the row in place (no rebuild), so the checkbox keeps keyboard focus.
        private void SetDone(QuickTodo todo, CheckBox check, TextBlock text, TextBlock due, bool overdue)
        {
            bool done = check.IsChecked == true;
            if (!QuickTodoService.SetDone(todo.Id, done))
            {
                check.IsChecked = !done;
                ShowUnreadable();
                return;
            }

            todo.IsDone = done;
            todo.CompletedDate = done ? DateTime.Now : (DateTime?)null;
            if (done)
                completedHere.Add(todo.Id);

            PaintDone(text, due, overdue, done);
            UpdateFooter();
        }

        private void DeleteTodo(QuickTodo todo)
        {
            if (!QuickTodoService.Delete(todo.Id))
            {
                ShowUnreadable();
                return;
            }

            todos.Remove(todo);
            Render();
        }

        private void DoneToggle_Click(object sender, RoutedEventArgs e)
        {
            showDone = !showDone;
            Render();
        }

        // ---- Helpers ------------------------------------------------------

        // The day label is red only while an overdue todo is still open.
        private void PaintDone(TextBlock text, TextBlock due, bool overdue, bool done)
        {
            text.TextDecorations = done ? TextDecorations.Strikethrough : null;
            text.Foreground = (Brush)FindResource(done ? "TextSub" : "TextMain");
            due.Foreground = (Brush)FindResource(overdue && !done ? "Danger" : "TextSub");
        }

        private void ShowUnreadable()
        {
            StatusText.Text = UnreadableMessage;
            StatusText.Visibility = Visibility.Visible;
        }

        private static string DueLabel(DateTime due, DateTime today)
        {
            switch ((due.Date - today).Days)
            {
                case 0: return "Today";
                case 1: return "Tomorrow";
                case -1: return "from yesterday";
                default: return "from " + due.ToString("MMM d", CultureInfo.InvariantCulture);
            }
        }
    }
}
