using System;
using System.Collections.Generic;
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
        private enum DueGroup { Overdue, Today, Tomorrow }

        private List<QuickTodo> todos = new List<QuickTodo>();
        private bool showDone;

        // A to-do was ticked or unticked here (and the day plan blocks linked to it with it).
        public event Action DoneChanged;

        // Ticked while this list is on screen: they stay put (struck through), also across refreshes, until the
        // list is created again — so a mis-click can be undone where it happened.
        private readonly HashSet<Guid> completedHere = new HashSet<Guid>();

        // The host refreshes on window activation; this covers the date changing while the app stays in front.
        private readonly DispatcherTimer midnight = new DispatcherTimer();

        private readonly ItemSelection<Guid> selection;

        public QuickTodoListControl()
        {
            InitializeComponent();
            selection = new ItemSelection<Guid>(TodoList, DeleteSelected,
                (row, on) => SelectionLook.PaintRow((Border)row, on, Brushes.Transparent, Brushes.Transparent));
            selection.ClearOnBlankClick(TodoList);
            midnight.Tick += (s, e) => { Refresh(); ScheduleMidnight(); };
            Loc.Track(this, () =>
            {
                Render();
                if (StatusText.Visibility == Visibility.Visible)
                    ShowUnreadable();
            });
            Loaded += (s, e) => { Refresh(); ScheduleMidnight(); };
            Unloaded += (s, e) => midnight.Stop();

            // A refresh put off while a row was being edited (Refresh) runs once focus moves on to something else.
            // Not when the window merely loses focus (no new focus): the row is still the one being edited then.
            TodoList.LostKeyboardFocus += (s, e) =>
            {
                if (refreshAfterEdit && e.NewFocus != null)
                    Dispatcher.BeginInvoke(new Action(Refresh), DispatcherPriority.Input);
            };
        }

        private bool refreshAfterEdit;

        // Rows aren't rebuilt while one is being edited (its text box is the window's focus, also while the window
        // is in the background and comes back to it): that would take the caret, and the keys typed next would go
        // nowhere. The refresh waits until the edit is over (the text is saved when it ends, so nothing is missed).
        public void Refresh()
        {
            if (IsEditingRow())
            {
                refreshAfterEdit = true;
                return;
            }

            refreshAfterEdit = false;
            todos = QuickTodoService.GetCurrentList(DateTime.Today);
            Render();
        }

        private bool IsEditingRow()
        {
            Window window = Window.GetWindow(this);
            return window != null && FocusManager.GetFocusedElement(window) is Visual focused
                && focused != TodoList && TodoList.IsAncestorOf(focused); // the list itself has it after a selecting click
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
            selection.BeginRender();
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

            selection.EndRender();
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
            DoneToggle.Content = showDone ? Loc.T("Todo.HideDone") : Loc.F("Todo.ShowDone", hiddenDone);

            EmptyText.Visibility = TodoList.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyText.Text = Loc.T(hiddenDone > 0 ? "Todo.AllDone" : "Todo.Empty");
        }

        private bool IsShown(QuickTodo todo) => !todo.IsDone || showDone || completedHere.Contains(todo.Id);

        private FrameworkElement CreateRow(QuickTodo todo, DateTime today)
        {
            Grid row = new Grid { MinHeight = 30, Background = Brushes.Transparent };
            // The frame shows the row as selected (ItemSelection); taken first, so a Ctrl or Shift click neither
            // ticks nor edits.
            Border frame = new Border { Child = row, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1) };
            SelectionLook.PaintRow(frame, false, Brushes.Transparent, Brushes.Transparent);
            selection.Attach(frame, todo.Id);
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // The text is edited in place, like the notes (TextEditing.InlineEditor): click into it to fix a letter.
            // The checkbox beside it ticks the to-do.
            TextBox text = TextEditing.InlineEditor(() => todo.Text, edited => Rename(todo, edited), () => Input.Focus());
            text.FontSize = 13;
            text.VerticalAlignment = VerticalAlignment.Center;
            text.Margin = new Thickness(0, 4, 8, 4);
            Grid.SetColumn(text, 1);

            bool overdue = GroupOf(todo, today) == DueGroup.Overdue;
            TextBlock due = new TextBlock
            {
                Text = DueLabel(todo.DueDate, today),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };
            Grid.SetColumn(due, 2);

            PaintDone(text, due, overdue, todo.IsDone);

            CheckBox check = new CheckBox
            {
                IsChecked = todo.IsDone,
                VerticalAlignment = VerticalAlignment.Center,
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
            row.Children.Add(text);
            row.Children.Add(due);

            // Shown only while the row is hovered or focused, to keep the list calm. (Delete on the checkbox also works.)
            Button delete = new Button
            {
                Style = (Style)FindResource("RowDeleteStyle"),
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Hidden
            };
            delete.Click += (s, e) => DeleteFromRow(todo);
            Grid.SetColumn(delete, 3);

            // The row's menu (outside the text, which has its own Cut / Copy / Paste). Editing is clicking the text.
            ContextMenu menu = new ContextMenu();
            MenuItem remove = new MenuItem { Header = Loc.T("Common.Delete") };
            remove.Click += (s, e) => DeleteTodo(todo); // the same as the row's "×"
            menu.Items.Add(remove);
            menu.Items.Add(new Separator());
            menu.Items.Add(TextCopy.CopyItem(() => todo.Text));
            frame.ContextMenu = menu;
            row.Children.Add(delete);

            void UpdateDeleteVisibility() =>
                delete.Visibility = row.IsMouseOver || row.IsKeyboardFocusWithin ? Visibility.Visible : Visibility.Hidden;
            row.MouseEnter += (s, e) => UpdateDeleteVisibility();
            row.MouseLeave += (s, e) => UpdateDeleteVisibility();
            row.IsKeyboardFocusWithinChanged += (s, e) => UpdateDeleteVisibility();

            return frame;
        }

        // Stored by Id (quicktodos.json is re-read, so a change made elsewhere meanwhile isn't lost). If it can't be
        // saved, the list says the file is unreadable and the old text is kept.
        private bool Rename(QuickTodo todo, string text)
        {
            if (!QuickTodoService.Rename(todo.Id, text))
            {
                ShowUnreadable();
                return false;
            }
            todo.Text = text;
            return true;
        }

        // Restyles the row in place (no rebuild), so the checkbox keeps keyboard focus.
        private void SetDone(QuickTodo todo, CheckBox check, TextBox text, TextBlock due, bool overdue)
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
            DoneChanged?.Invoke();
        }

        private void DeleteTodo(QuickTodo todo) => Remove(new[] { todo.Id });

        // A selected row's × takes the whole selection along (asking first); otherwise just that row goes.
        private void DeleteFromRow(QuickTodo todo)
        {
            if (selection.IsSelected(todo.Id) && selection.Count > 1)
                selection.DeleteSelected();
            else
                DeleteTodo(todo);
        }

        // Several: one question, then the same removal as one.
        private bool DeleteSelected(IReadOnlyList<Guid> ids)
        {
            List<string> names = ids.Select(id => todos.First(t => t.Id == id).Text).ToList();
            if (ids.Count > 1 && !BulkDelete.Confirm(Window.GetWindow(this), "Bulk.Delete.Todos", names))
                return false;
            return Remove(ids);
        }

        // One write for all of them: if quicktodos.json can't be read or written, none go and the list says so. A
        // day plan block made from one keeps its time and shows its to-do as gone. False if none went.
        private bool Remove(IReadOnlyCollection<Guid> ids)
        {
            if (!QuickTodoService.Delete(ids))
            {
                ShowUnreadable();
                return false;
            }

            todos.RemoveAll(t => ids.Contains(t.Id));
            Render();
            return true;
        }

        private void DoneToggle_Click(object sender, RoutedEventArgs e)
        {
            showDone = !showDone;
            Render();
        }

        // ---- Helpers ------------------------------------------------------

        // The day label is red only while an overdue todo is still open.
        private void PaintDone(TextBox text, TextBlock due, bool overdue, bool done)
        {
            text.TextDecorations = done ? TextDecorations.Strikethrough : null;
            text.Foreground = (Brush)FindResource(done ? "TextSub" : "TextMain");
            due.Foreground = (Brush)FindResource(overdue && !done ? "Danger" : "TextSub");
        }

        private void ShowUnreadable()
        {
            StatusText.Text = Loc.T("Todo.Unreadable");
            StatusText.Visibility = Visibility.Visible;
        }

        private static string DueLabel(DateTime due, DateTime today)
        {
            switch ((due.Date - today).Days)
            {
                case 0: return Loc.T("Common.Today");
                case 1: return Loc.T("Common.Tomorrow");
                case -1: return Loc.T("Todo.FromYesterday");
                default: return Loc.F("Todo.FromDate", due.ToString(Loc.T("Format.ShortMonthDay"), Loc.Culture));
            }
        }
    }
}
