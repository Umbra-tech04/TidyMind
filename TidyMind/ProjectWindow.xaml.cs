using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace TidyMind
{
    public partial class ProjectWindow : Window
    {
        private Project currentProject;
        private string loadedDescription;
        private string loadedNotes;

        public ProjectWindow(Project project)
        {
            InitializeComponent();

            currentProject = project;

            NameBox.Text = currentProject.Name;
            SetUpDescriptionEditor();

            if (currentProject.Tasks == null)
                currentProject.Tasks = new List<TaskItem>();

            foreach (TaskItem task in currentProject.Tasks)
                TaskList.Items.Add(task);

            Files.Load(currentProject.Attachments);
            SetUpNotesEditor();
        }

        // Taken out in this window: the owner deletes their stored copies once it has saved the project without them.
        public IReadOnlyList<Attachment> RemovedAttachments => Files.Removed;

        private void SaveButton_Click(object sender, RoutedEventArgs e) => Close();

        // Tasks already change the project live, so every way of closing saves the rest too — otherwise the
        // title-bar X would keep the tasks but silently drop the description and the note.
        protected override void OnClosing(CancelEventArgs e)
        {
            // Closing stops a file still being copied in (nothing half-copied is kept), so ask first.
            if (Files.IsCopying && MessageBox.Show(this, "Files are still being added. Stop adding them and close?",
                    "Files Still Copying", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }

            base.OnClosing(e);
            Files.Close();
            currentProject.Attachments = Files.Attachments;

            currentProject.Name = NameBox.Text;

            // Compared with the editor as loaded, not the stored string: re-saving an untouched null or
            // plain-text description or note would change its storage form and count as an edit (LastModified).
            string description = RichTextHelper.ToStorableString(DescriptionBox.Document);
            if (description != loadedDescription)
                currentProject.Description = description;

            string notes = RichTextHelper.ToStorableString(NotesBox.Document);
            if (notes != loadedNotes)
                currentProject.Notes = notes;
        }

        // ---- Description editor -------------------------------------------

        private void SetUpDescriptionEditor()
        {
            RichTextHelper.FromStorableString(DescriptionBox.Document, currentProject.Description);
            loadedDescription = RichTextHelper.ToStorableString(DescriptionBox.Document);

            DescriptionToolbar.Attach(DescriptionBox);
            RichTextHelper.PasteOutsideTextAsPlain(DescriptionBox);
            ShowFocusOn(EditorFrame, DescriptionBox);
            DescriptionBox.PreviewMouseWheel += DescriptionBox_PreviewMouseWheel;
        }

        // The frame shows focus like the other inputs do.
        private void ShowFocusOn(Border frame, RichTextBox editor)
        {
            Brush rest = frame.BorderBrush;
            editor.IsKeyboardFocusWithinChanged += (s, e) =>
                frame.BorderBrush = editor.IsKeyboardFocusWithin ? (Brush)FindResource("Accent") : rest;
        }

        // At either end of its own scroll range the editor passes the wheel on to the window, so it doesn't
        // swallow scrolling (same as the task list).
        private void DescriptionBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            bool canScroll = e.Delta < 0
                ? DescriptionBox.VerticalOffset + DescriptionBox.ViewportHeight < DescriptionBox.ExtentHeight - 0.5
                : DescriptionBox.VerticalOffset > 0.5;
            if (canScroll) return;

            e.Handled = true;
            MouseWheelEventArgs forward = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = UIElement.MouseWheelEvent };
            ((UIElement)VisualTreeHelper.GetParent(DescriptionBox)).RaiseEvent(forward);
        }

        // ---- Tasks --------------------------------------------------------

        private void AddTaskButton_Click(object sender, RoutedEventArgs e)
        {
            AddTask();
            TaskInput.Focus();
        }

        // Enter adds the task and keeps the cursor in the box, so several can be typed in a row.
        private void TaskInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            AddTask();
        }

        private void AddTask()
        {
            if (string.IsNullOrWhiteSpace(TaskInput.Text)) return;

            TaskItem newTask = new TaskItem();
            newTask.Title = TaskInput.Text;
            currentProject.Tasks.Add(newTask);
            TaskList.Items.Add(newTask);
            TaskInput.Clear();
        }

        // The list scrolls itself while it has rows to show in that direction; at either end the wheel moves on to
        // the window (like the description editor).
        private void TaskList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (TaskList.Template.FindName("Rows", TaskList) is ScrollViewer rows &&
                (e.Delta < 0 ? rows.VerticalOffset < rows.ScrollableHeight - 0.5 : rows.VerticalOffset > 0.5))
                return;

            e.Handled = true;
            var forward = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = UIElement.MouseWheelEvent };
            ((UIElement)sender).RaiseEvent(forward);
        }

        private void TaskList_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            var item = ItemsControl.ContainerFromElement(TaskList, e.OriginalSource as DependencyObject) as ListBoxItem;
            if (item == null) return;

            TaskItem task = (TaskItem)item.DataContext;

            ContextMenu menu = new ContextMenu();

            MenuItem renameItem = new MenuItem();
            renameItem.Header = "Rename";
            renameItem.Click += (s, args) => RenameTask(task);

            MenuItem deleteItem = new MenuItem();
            deleteItem.Header = "Delete";
            deleteItem.Click += (s, args) => DeleteTask(task);

            menu.Items.Add(renameItem);
            menu.Items.Add(deleteItem);
            menu.Items.Add(new Separator());
            menu.Items.Add(TextCopy.CopyItem(() => task.Title));
            menu.IsOpen = true;
        }

        // A row's hover buttons: the same Rename / Delete as its right-click menu.
        private void TaskRename_Click(object sender, RoutedEventArgs e) => RenameTask((TaskItem)((FrameworkElement)sender).DataContext);

        private void TaskDelete_Click(object sender, RoutedEventArgs e) => DeleteTask((TaskItem)((FrameworkElement)sender).DataContext);

        private void RenameTask(TaskItem task)
        {
            string newTitle = InputDialog.Prompt(this, "Rename Task", "Enter a new name for this task.",
                task.Title, "Rename");
            if (string.IsNullOrWhiteSpace(newTitle) || newTitle == task.Title) return;
            task.Title = newTitle;
            TaskList.Items.Refresh();
        }

        private void DeleteTask(TaskItem task)
        {
            currentProject.Tasks.Remove(task);
            TaskList.Items.Remove(task);
        }

        private void TaskList_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && TaskList.SelectedItem is TaskItem task)
            {
                TextCopy.Copy(task.Title);
                e.Handled = true;
            }
        }

        // ---- Notes --------------------------------------------------------

        // Stored the same way as the description (formatted XAML); notes from before formatting load as plain text.
        private void SetUpNotesEditor()
        {
            RichTextHelper.FromStorableString(NotesBox.Document, currentProject.Notes);
            loadedNotes = RichTextHelper.ToStorableString(NotesBox.Document);

            NotesToolbar.Attach(NotesBox);
            RichTextHelper.PasteOutsideTextAsPlain(NotesBox);
            ShowFocusOn(NotesFrame, NotesBox);

            NotesBox.TextChanged += (s, e) => UpdateNotesPlaceholder();
            UpdateNotesPlaceholder();
        }

        // "Start writing..." only on a blank page: one empty line, not just a page of blank lines.
        private void UpdateNotesPlaceholder()
        {
            FlowDocument page = NotesBox.Document;
            bool blank = page.Blocks.Count <= 1
                && new TextRange(page.ContentStart, page.ContentEnd).Text.TrimEnd('\r', '\n').Length == 0;
            NotesPlaceholder.Visibility = blank ? Visibility.Visible : Visibility.Collapsed;
        }

        // The page never scrolls itself (it grows instead), but its built-in scroll area still marks the wheel as
        // handled, so the window wouldn't scroll while the mouse is over it: pass the wheel on.
        private void NotesBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            e.Handled = true;
            var forward = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = UIElement.MouseWheelEvent };
            ((UIElement)sender).RaiseEvent(forward);
        }
    }
}