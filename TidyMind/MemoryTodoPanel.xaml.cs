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
    // A project memory's inline to-do list, above its tabs and project cards. Saved on every change.
    // Shown while it has items; an empty one stays out of the way until "+ To-Do List" reveals it.
    public partial class MemoryTodoPanel : UserControl
    {
        private string memoryName;
        private List<TodoEntry> todos = new List<TodoEntry>();
        private bool readable = true;

        public MemoryTodoPanel()
        {
            InitializeComponent();
        }

        public void Load(string memory)
        {
            memoryName = memory;
            readable = MemoryTodoService.TryLoad(memory, out todos);

            // An unreadable file is left alone (never saved over), and the list is shown with the reason.
            Input.IsEnabled = readable;
            ErrorText.Visibility = readable ? Visibility.Collapsed : Visibility.Visible;
            ErrorText.Text = "Couldn't read " + MemoryTodoService.FileName(memory) + ". Fix or remove that file to use this list.";

            Render();
            Visibility = todos.Count > 0 || !readable ? Visibility.Visible : Visibility.Collapsed;
        }

        // "+ To-Do List": show the list and put the cursor in it.
        public void Reveal()
        {
            Visibility = Visibility.Visible;
            Dispatcher.BeginInvoke(new Action(() => Input.Focus()), DispatcherPriority.Input);
        }

        private void Save()
        {
            if (readable && !MemoryTodoService.TrySave(memoryName, todos))
                MessageBox.Show("Couldn't save: " + MemoryTodoService.FileName(memoryName) + " can't be written. "
                    + "Your last change wasn't saved.", "Couldn't Save", MessageBoxButton.OK, MessageBoxImage.Warning);
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

            todos.Add(new TodoEntry { Text = text });
            Save();
            Input.Clear();
            Render();

            FrameworkElement added = (FrameworkElement)ItemList.Children[ItemList.Children.Count - 1];
            Dispatcher.BeginInvoke(new Action(added.BringIntoView), DispatcherPriority.Loaded);
        }

        private void Input_TextChanged(object sender, TextChangedEventArgs e)
        {
            Placeholder.Visibility = Input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // Revealed but left empty: tuck it away again.
        private void Input_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (readable && todos.Count == 0 && Input.Text.Length == 0)
                Visibility = Visibility.Collapsed;
        }

        // ---- List ---------------------------------------------------------

        private void Render()
        {
            ItemList.Children.Clear();
            foreach (TodoEntry entry in todos)
                ItemList.Children.Add(CreateRow(entry));
            UpdateCount();
        }

        private void UpdateCount()
        {
            int done = todos.Count(t => t.IsDone);
            CountText.Text = todos.Count == 0 ? "" : done + "/" + todos.Count + " done";
        }

        private FrameworkElement CreateRow(TodoEntry entry)
        {
            Grid row = new Grid { MinHeight = 30, Background = Brushes.Transparent };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            TextBlock text = new TextBlock { Text = entry.Text, TextWrapping = TextWrapping.Wrap, FontSize = 13 };
            PaintDone(text, entry.IsDone);

            // The text is the checkbox's content, so clicking the words ticks it too.
            CheckBox check = new CheckBox
            {
                Content = text,
                IsChecked = entry.IsDone,
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 3, 8, 3)
            };
            check.Click += (s, e) =>
            {
                entry.IsDone = check.IsChecked == true;
                Save();
                PaintDone(text, entry.IsDone);
                UpdateCount();
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
            Grid.SetColumn(delete, 1);
            row.Children.Add(delete);

            void UpdateDeleteVisibility() =>
                delete.Visibility = row.IsMouseOver || row.IsKeyboardFocusWithin ? Visibility.Visible : Visibility.Hidden;
            row.MouseEnter += (s, e) => UpdateDeleteVisibility();
            row.MouseLeave += (s, e) => UpdateDeleteVisibility();
            row.IsKeyboardFocusWithinChanged += (s, e) => UpdateDeleteVisibility();

            TextCopy.AttachMenu(row, () => entry.Text);
            return row;
        }

        private void PaintDone(TextBlock text, bool done)
        {
            text.TextDecorations = done ? TextDecorations.Strikethrough : null;
            text.Foreground = (Brush)FindResource(done ? "TextSub" : "TextMain");
        }

        // Focus goes back to the input, so the list stays open even when this was the last item.
        private void Delete(TodoEntry entry)
        {
            todos.Remove(entry);
            Save();
            Render();
            Input.Focus();
        }
    }
}
