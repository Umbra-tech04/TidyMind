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

        public MemoryTodoPanel()
        {
            InitializeComponent();
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
            ErrorText.Text = "Couldn't read " + MemoryFiles.Todos(memory) + ". Fix or remove that file to use this list.";

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

            MessageBox.Show("Couldn't save the to-do list of '" + memory.Name + "': this file can't be written:\n"
                + MemoryFiles.Todos(memory) + "\n\nYour last change wasn't saved.", "Couldn't Save",
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
            TodoEntry entry = new TodoEntry { Text = text };
            todos.Add(entry);
            if (!Save())
            {
                todos.Remove(entry);
                return;
            }
            Input.Clear();
            Render();

            FrameworkElement added = (FrameworkElement)ItemList.Children[ItemList.Children.Count - 1];
            Dispatcher.BeginInvoke(new Action(added.BringIntoView), DispatcherPriority.Loaded);
        }

        private void Input_TextChanged(object sender, TextChangedEventArgs e)
        {
            Placeholder.Visibility = Input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // ---- List ---------------------------------------------------------

        private void Render()
        {
            ItemList.Children.Clear();
            foreach (TodoEntry entry in todos)
                ItemList.Children.Add(CreateRow(entry));
            UpdateHeader();
        }

        // Counter, folded preview and layout. With no to-dos there's nothing to fold or count: just the add box (and
        // the reason, if the file couldn't be read).
        private void UpdateHeader()
        {
            bool hasItems = todos.Count > 0;
            bool folded = collapsed && hasItems;

            int done = todos.Count(t => t.IsDone);
            CountText.Text = hasItems ? done + "/" + todos.Count + " done" : "";
            TodoEntry next = todos.FirstOrDefault(t => !t.IsDone);
            NextRun.Text = next?.Text ?? "";
            NextText.Visibility = folded && next != null ? Visibility.Visible : Visibility.Collapsed;

            Header.Visibility = hasItems || !readable ? Visibility.Visible : Visibility.Collapsed;
            Header.IsEnabled = hasItems;
            Arrow.Text = folded ? CollapsedGlyph : ExpandedGlyph;
            Arrow.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
            Header.ToolTip = folded ? "Unfold the to-do list" : "Fold the to-do list";
            AutomationProperties.SetName(Header, folded ? "To-do list, folded" : "To-do list, open");
            Header.Margin = new Thickness(0, 0, 0, folded ? 0 : 4);

            Body.Visibility = folded ? Visibility.Collapsed : Visibility.Visible;
            Frame.Padding = folded ? new Thickness(10, 4, 10, 4) : new Thickness(10, 8, 10, 10);
        }

        private FrameworkElement CreateRow(TodoEntry entry)
        {
            Grid row = new Grid { MinHeight = 30, Background = Brushes.Transparent };
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
                entry.IsDone = check.IsChecked == true;
                if (!Save())
                {
                    entry.IsDone = !entry.IsDone; // not saved: the tick goes back
                    check.IsChecked = entry.IsDone;
                }
                PaintDone(text, entry.IsDone);
                UpdateHeader();
            };
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
            delete.Click += (s, e) => Delete(entry);
            Grid.SetColumn(delete, 2);
            row.Children.Add(delete);

            void UpdateDeleteVisibility() =>
                delete.Visibility = row.IsMouseOver || row.IsKeyboardFocusWithin ? Visibility.Visible : Visibility.Hidden;
            row.MouseEnter += (s, e) => UpdateDeleteVisibility();
            row.MouseLeave += (s, e) => UpdateDeleteVisibility();
            row.IsKeyboardFocusWithinChanged += (s, e) => UpdateDeleteVisibility();

            TextCopy.AttachMenu(row, () => entry.Text);
            return row;
        }

        private void PaintDone(TextBox text, bool done)
        {
            text.TextDecorations = done ? TextDecorations.Strikethrough : null;
            text.Foreground = (Brush)FindResource(done ? "TextSub" : "TextMain");
        }

        // If saving fails (said so by Save), the old text is kept.
        private bool Rename(TodoEntry entry, string text)
        {
            string previous = entry.Text;
            entry.Text = text;
            if (Save())
            {
                TourSignals.Raise(TourSignals.TodoEdited);
                return true;
            }
            entry.Text = previous;
            return false;
        }

        // Focus goes back to the input. If the removal can't be saved, the item stays where it was.
        private void Delete(TodoEntry entry)
        {
            int index = todos.IndexOf(entry);
            if (index < 0)
                return;
            todos.RemoveAt(index);
            if (!Save())
                todos.Insert(index, entry);
            Render();
            Input.Focus();
        }
    }
}
