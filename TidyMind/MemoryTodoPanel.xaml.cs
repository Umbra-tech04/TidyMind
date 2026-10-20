using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace TidyMind
{
    // A project memory's inline to-do list, above its tabs and project cards. Saved on every change.
    // An empty one is just the add box. The header folds it to one line (Profile.TodoCollapsed).
    public partial class MemoryTodoPanel : UserControl
    {
        private const string ExpandedGlyph = "\uE70D";  // Segoe MDL2: ChevronDown
        private const string CollapsedGlyph = "\uE76C"; // Segoe MDL2: ChevronRight
        private const int FoldAboveOpen = 3;            // no saved choice: folded when more to-dos than this are open

        private Profile memory;
        private Func<bool> saveProfiles;
        private List<TodoEntry> todos = new List<TodoEntry>();
        private bool readable = true;
        private bool collapsed;
        private readonly ItemSelection<TodoEntry> selection;
        private readonly Dictionary<TodoEntry, FrameworkElement> rows = new Dictionary<TodoEntry, FrameworkElement>();
        private readonly Dictionary<TodoEntry, CheckBox> ticks = new Dictionary<TodoEntry, CheckBox>();

        public MemoryTodoPanel()
        {
            InitializeComponent();
            selection = new ItemSelection<TodoEntry>(ListScroll, DeleteSelected,
                (row, on) => SelectionLook.PaintRow((Border)row, on, Brushes.Transparent, Brushes.Transparent));
            selection.ClearOnBlankClick(ListScroll);
            Loc.Track(this, () =>
            {
                if (memory == null)
                    return; // not loaded with a memory yet
                ErrorText.Text = Loc.F("Todo.ListUnreadable", MemoryFiles.Todos(memory));
                Render();
            });
        }

        // saveProfiles: writes the main window's list of memories, where the folded state is kept.
        public void Load(Profile memory, Func<bool> saveProfiles)
        {
            this.memory = memory;
            this.saveProfiles = saveProfiles;
            readable = MemoryTodoService.TryLoad(memory, out todos);
            collapsed = memory.TodoCollapsed ?? todos.Count(t => !t.IsDone) > FoldAboveOpen;

            // An unreadable file is left alone (never saved over), and the list is shown with the reason.
            Input.IsEnabled = readable;
            ErrorText.Visibility = readable ? Visibility.Collapsed : Visibility.Visible;
            ErrorText.Text = Loc.F("Todo.ListUnreadable", MemoryFiles.Todos(memory));

            Render();
        }

        // ---- Folding ------------------------------------------------------

        // Remembered per memory. If profiles.json can't be written, the panel still folds for this session.
        private void Header_Click(object sender, RoutedEventArgs e)
        {
            collapsed = !collapsed;
            memory.TodoCollapsed = collapsed;
            saveProfiles();
            UpdateHeader();
        }

        // The list scrolls itself while it can; at either end the wheel moves on (ProjectView passes it to the cards).
        private void ListScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (!WheelScroll.CanScroll(ListScroll, e.Delta))
                WheelScroll.Forward(e, this);
        }

        // False (after saying so) if the file couldn't be written.
        private bool Save()
        {
            if (!readable)
                return false;
            if (MemoryTodoService.TrySave(memory, todos))
                return true;

            Msg.Show(Loc.F("Todo.SaveFailed", memory.Name, MemoryFiles.Todos(memory)), Loc.T("Common.CouldntSave"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        // ---- Input --------------------------------------------------------

        private void Input_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && Input.Text.Length > 0)
            {
                Input.Clear();
                e.Handled = true;
                return;
            }
            if (e.Key != Key.Enter) return;
            e.Handled = true;

            string text = Input.Text.Trim();
            if (text.Length == 0) return;

            // Not saved: the item isn't added, and the text stays in the box to try again.
            TodoEntry entry = new TodoEntry { Id = Guid.NewGuid(), Text = text };
            todos.Add(entry);
            if (!Save())
            {
                todos.Remove(entry);
                return;
            }
            Input.Clear();
            Render();

            // The last of the open ones, above the done ones.
            Dispatcher.BeginInvoke(new Action(rows[entry].BringIntoView), DispatcherPriority.Loaded);
        }

        private void Input_TextChanged(object sender, TextChangedEventArgs e)
        {
            Placeholder.Visibility = Input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // ---- List ---------------------------------------------------------

        // The open to-dos, then the done ones (TaskOrder); todos.json keeps the order they were added in.
        private void Render()
        {
            ItemList.Children.Clear();
            rows.Clear();
            ticks.Clear();
            selection.BeginRender();
            foreach (TodoEntry entry in TaskOrder.OpenFirst(todos, t => t.IsDone))
            {
                FrameworkElement row = CreateRow(entry);
                rows[entry] = row;
                ItemList.Children.Add(row);
            }
            selection.EndRender();
            UpdateHeader();
        }

        // Ticked or unticked: the list is drawn again with the to-do in its other group. The selection is kept (it is by
        // to-do), the list is not scrolled, and the keyboard goes back to the same box.
        private void Moved(TodoEntry entry, bool keyboard)
        {
            TaskOrder.KeepView(ItemList, () =>
            {
                Render();
                if (keyboard && ticks.TryGetValue(entry, out CheckBox box))
                    box.Focus();
            });
        }

        // Counter, folded preview and layout. With no to-dos there's nothing to fold or count: just the add box (and
        // the reason, if the file couldn't be read).
        private void UpdateHeader()
        {
            bool hasItems = todos.Count > 0;
            bool folded = collapsed && hasItems;

            int done = todos.Count(t => t.IsDone);
            CountText.Text = hasItems ? Loc.F("Todo.Count", done, todos.Count) : "";
            TodoEntry next = todos.FirstOrDefault(t => !t.IsDone);
            NextRun.Text = next?.Text ?? "";
            NextText.Visibility = folded && next != null ? Visibility.Visible : Visibility.Collapsed;

            Header.Visibility = hasItems || !readable ? Visibility.Visible : Visibility.Collapsed;
            Header.IsEnabled = hasItems;
            Arrow.Text = folded ? CollapsedGlyph : ExpandedGlyph;
            Arrow.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
            Header.ToolTip = Loc.T(folded ? "Todo.Unfold" : "Todo.Fold");
            AutomationProperties.SetName(Header, Loc.T(folded ? "Todo.ListFolded" : "Todo.ListOpen"));
            Header.Margin = new Thickness(0, 0, 0, folded ? 0 : 4);

            Body.Visibility = folded ? Visibility.Collapsed : Visibility.Visible;
            Frame.Padding = folded ? new Thickness(10, 4, 10, 4) : new Thickness(10, 8, 10, 10);
        }

        private FrameworkElement CreateRow(TodoEntry entry)
        {
            Grid row = new Grid { MinHeight = 30, Background = Brushes.Transparent };
            // The frame shows the row as selected (ItemSelection); taken first, so a Ctrl or Shift click neither
            // ticks nor edits.
            Border frame = new Border { Child = row, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1) };
            SelectionLook.PaintRow(frame, false, Brushes.Transparent, Brushes.Transparent);
            selection.Attach(frame, entry);
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // The text is edited in place, like the notes (TextEditing.InlineEditor); the checkbox beside it ticks the
            // item. Only the text changes: the item keeps its place and its done state.
            TextBox text = TextEditing.InlineEditor(() => entry.Text, edited => Rename(entry, edited), () => Input.Focus(),
                editable: readable);
            text.FontSize = 13;
            text.VerticalAlignment = VerticalAlignment.Center;
            text.Margin = new Thickness(0, 3, 8, 3);
            PaintDone(text, entry.IsDone);
            Grid.SetColumn(text, 1);
            row.Children.Add(text);

            CheckBox check = new CheckBox
            {
                IsChecked = entry.IsDone,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 3, 8, 3)
            };
            check.Click += (s, e) =>
            {
                // One undo step with the plan blocks that follow the tick.
                using (UndoChange change = Record(UndoParts.DayPlan().Secondary()))
                {
                    entry.IsDone = check.IsChecked == true;
                    if (!Save())
                    {
                        entry.IsDone = !entry.IsDone; // not saved: the tick goes back
                        check.IsChecked = entry.IsDone;
                    }
                    else
                        DayPlanTodos.SyncBlocks(TodoLink.ForMemory(memory, entry.Id), entry.IsDone);
                    change.Commit(Loc.F(entry.IsDone ? "Undo.Ticked" : "Undo.Unticked", entry.Text));
                }
                PaintDone(text, entry.IsDone);
                Moved(entry, check.IsKeyboardFocused);
            };
            ticks[entry] = check;
            check.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Delete)
                {
                    Delete(entry);
                    e.Handled = true;
                }
            };
            row.Children.Add(check);

            // Shown only while the row is hovered or focused, to keep the list calm.
            Button delete = new Button
            {
                Style = (Style)FindResource("RowDeleteStyle"),
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Hidden
            };
            delete.Click += (s, e) => DeleteFromRow(entry);
            Grid.SetColumn(delete, 2);
            row.Children.Add(delete);

            void UpdateDeleteVisibility() =>
                delete.Visibility = row.IsMouseOver || row.IsKeyboardFocusWithin ? Visibility.Visible : Visibility.Hidden;
            row.MouseEnter += (s, e) => UpdateDeleteVisibility();
            row.MouseLeave += (s, e) => UpdateDeleteVisibility();
            row.IsKeyboardFocusWithinChanged += (s, e) => UpdateDeleteVisibility();

            TextCopy.AttachMenu(frame, () => entry.Text);
            return frame;
        }

        // A selected row's × takes the whole selection along (asking first); otherwise just that row goes.
        private void DeleteFromRow(TodoEntry entry)
        {
            if (selection.IsSelected(entry) && selection.Count > 1)
                selection.DeleteSelected();
            else
                Delete(entry);
        }

        // Several: one question, then one save without them (or, if that fails, all of them back).
        private bool DeleteSelected(IReadOnlyList<TodoEntry> entries)
        {
            if (entries.Count > 1 && !BulkDelete.Confirm(Window.GetWindow(this), "Bulk.Delete.Todos", entries.Select(t => t.Text).ToList()))
                return false;
            return Remove(entries);
        }

        private void PaintDone(TextBox text, bool done)
        {
            text.TextDecorations = done ? TextDecorations.Strikethrough : null;
            text.Foreground = (Brush)FindResource(done ? "TextSub" : "TextMain");
        }

        // If saving fails (said so by Save), the old text is kept.
        private bool Rename(TodoEntry entry, string text)
        {
            using UndoChange change = Record();
            string previous = entry.Text;
            entry.Text = text;
            if (Save())
            {
                change.Commit(Loc.F("Undo.RenamedTo", previous, text));
                TourSignals.Raise(TourSignals.TodoEdited);
                return true;
            }
            entry.Text = previous;
            return false;
        }

        // An undoable change to this list (and `more`), in the page's history. Nothing is recorded for a list that
        // couldn't be read (it isn't changed either).
        private UndoChange Record(params UndoPart[] more) =>
            UndoChange.Begin(readable ? UndoService.Page : null, more.Prepend(UndoParts.Todos(memory)).ToArray());

        private void Delete(TodoEntry entry) => Remove(new[] { entry });

        // One item or several, in one save. Focus goes back to the input. If the removal can't be saved, the items
        // stay where they were (false). A day plan block made from one keeps its time and shows its to-do as gone.
        private bool Remove(IReadOnlyList<TodoEntry> entries)
        {
            using UndoChange change = Record();
            List<TodoEntry> before = todos.ToList();
            if (todos.RemoveAll(entries.Contains) == 0)
                return false;
            bool saved = Save();
            if (!saved)
                todos = before;
            else
                change.Commit(entries.Count == 1 ? Loc.F("Undo.Deleted", entries[0].Text) : Loc.Plural("Undo.Deleted.Todos", entries.Count),
                    destructive: true);
            Render();
            Input.Focus();
            return saved;
        }
    }
}
