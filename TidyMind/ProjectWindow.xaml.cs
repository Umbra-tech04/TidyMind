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

        public ProjectWindow(Project project)
        {
            InitializeComponent();

            currentProject = project;

            NameBox.Text = currentProject.Name;
            StatusBox.SelectedItem = currentProject.Status;
            SetUpDescriptionEditor();

            if (currentProject.Tasks == null)
                currentProject.Tasks = new List<TaskItem>();

            foreach (TaskItem task in currentProject.Tasks)
                TaskList.Items.Add(task);

            UpdateNoteDisplay();
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e) => Close();

        // Tasks and the note already change the project live, so every way of closing saves the rest too —
        // otherwise the title-bar X would keep the tasks but silently drop the description.
        protected override void OnClosing(CancelEventArgs e)
        {
            base.OnClosing(e);

            currentProject.Name = NameBox.Text;
            currentProject.Status = (ProjectStatus)StatusBox.SelectedItem;

            // Compared with the editor as loaded, not the stored string: re-saving an untouched null or
            // plain-text description would change its storage form and count as an edit (LastModified).
            string description = RichTextHelper.ToStorableString(DescriptionBox.Document);
            if (description != loadedDescription)
                currentProject.Description = description;
        }

        // ---- Description editor -------------------------------------------

        private static readonly string[] TextColors =
        {
            "#1C1B19", "#2E7BC4", "#27AE60", "#F2994A", "#EB5757", "#9B51E0", "#8A8A87"
        };

        private void SetUpDescriptionEditor()
        {
            RichTextHelper.FromStorableString(DescriptionBox.Document, currentProject.Description);
            loadedDescription = RichTextHelper.ToStorableString(DescriptionBox.Document);

            foreach (string hex in TextColors)
                ColorSwatches.Children.Add(CreateColorSwatch(hex));

            // The frame shows focus like the other inputs do.
            DescriptionBox.IsKeyboardFocusWithinChanged += (s, e) =>
                EditorFrame.BorderBrush = (Brush)FindResource(DescriptionBox.IsKeyboardFocusWithin ? "Accent" : "Border");

            // Formatting copied inside the app (WPF rich text) is kept; anything pasted from outside comes in as plain
            // text, so web pages or Word can't drag in their own fonts and sizes.
            DataObject.AddPastingHandler(DescriptionBox, (s, e) =>
            {
                if (e.DataObject.GetDataPresent(DataFormats.Xaml) || !e.DataObject.GetDataPresent(DataFormats.UnicodeText))
                    return;
                DataObject plain = new DataObject();
                plain.SetData(DataFormats.UnicodeText, e.DataObject.GetData(DataFormats.UnicodeText));
                e.DataObject = plain;
            });

            DescriptionBox.PreviewMouseWheel += DescriptionBox_PreviewMouseWheel;

            // Ctrl+B / Ctrl+I / Ctrl+U are built into RichTextBox; they don't move the selection, so refresh the
            // toolbar after any editing command (handledEventsToo: the editor marks its own commands handled).
            DescriptionBox.AddHandler(CommandManager.ExecutedEvent,
                new ExecutedRoutedEventHandler((s, e) => UpdateFormatState()), true);

            UpdateFormatState();
        }

        private FrameworkElement CreateColorSwatch(string hex)
        {
            Border swatch = new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(11),
                Margin = new Thickness(3),
                Cursor = Cursors.Hand,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)),
                ToolTip = hex == TextColors[0] ? "Default" : null
            };
            CardEffects.AttachClick(swatch, () =>
            {
                DescriptionBox.Selection.ApplyPropertyValue(TextElement.ForegroundProperty, swatch.Background);
                ColorPopup.IsOpen = false;
                DescriptionBox.Focus();
                UpdateFormatState();
            });
            return swatch;
        }

        private void BoldButton_Click(object sender, RoutedEventArgs e) => Format(EditingCommands.ToggleBold);
        private void ItalicButton_Click(object sender, RoutedEventArgs e) => Format(EditingCommands.ToggleItalic);
        private void UnderlineButton_Click(object sender, RoutedEventArgs e) => Format(EditingCommands.ToggleUnderline);

        // Same commands as Ctrl+B / Ctrl+I / Ctrl+U, so the buttons and the shortcuts behave identically.
        private void Format(RoutedUICommand command)
        {
            command.Execute(null, DescriptionBox);
            DescriptionBox.Focus();
            UpdateFormatState();
        }

        private void ColorButton_Click(object sender, RoutedEventArgs e)
        {
            ColorPopup.IsOpen = !ColorPopup.IsOpen;
            ColorButton.IsChecked = ColorPopup.IsOpen;
        }

        private void ColorPopup_Closed(object sender, EventArgs e) => ColorButton.IsChecked = false;

        private void DescriptionBox_SelectionChanged(object sender, RoutedEventArgs e) => UpdateFormatState();

        // Toolbar mirrors the formatting at the caret / across the selection (mixed counts as off).
        private void UpdateFormatState()
        {
            TextSelection selection = DescriptionBox.Selection;

            BoldButton.IsChecked = Equals(selection.GetPropertyValue(TextElement.FontWeightProperty), FontWeights.Bold);
            ItalicButton.IsChecked = Equals(selection.GetPropertyValue(TextElement.FontStyleProperty), FontStyles.Italic);
            UnderlineButton.IsChecked = selection.GetPropertyValue(Inline.TextDecorationsProperty) is TextDecorationCollection decorations
                && decorations.Any(d => d.Location == TextDecorationLocation.Underline);

            ColorBar.Fill = selection.GetPropertyValue(TextElement.ForegroundProperty) as Brush ?? (Brush)FindResource("TextSub");
        }

        // Below its max height the editor has nothing to scroll, so pass the wheel on to the window's scroll area
        // instead of letting the editor swallow it.
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

        private void TaskList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            e.Handled = true;
            var event2 = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta);
            event2.RoutedEvent = UIElement.MouseWheelEvent;
            ((UIElement)sender).RaiseEvent(event2);
        }

        private void TaskList_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            var item = ItemsControl.ContainerFromElement(TaskList, e.OriginalSource as DependencyObject) as ListBoxItem;
            if (item == null) return;

            TaskItem task = (TaskItem)item.DataContext;

            ContextMenu menu = new ContextMenu();

            MenuItem renameItem = new MenuItem();
            renameItem.Header = "Rename";
            renameItem.Click += (s, args) =>
            {
                string newTitle = InputDialog.Prompt(this, "Rename Task", "Enter a new name for this task.",
                    task.Title, "Rename");
                if (string.IsNullOrWhiteSpace(newTitle) || newTitle == task.Title) return;
                task.Title = newTitle;
                TaskList.Items.Refresh();
            };

            MenuItem deleteItem = new MenuItem();
            deleteItem.Header = "Delete";
            deleteItem.Click += (s, args) =>
            {
                currentProject.Tasks.Remove(task);
                TaskList.Items.Remove(task);
            };

            menu.Items.Add(renameItem);
            menu.Items.Add(deleteItem);
            menu.Items.Add(new Separator());
            menu.Items.Add(TextCopy.CopyItem(() => task.Title));
            menu.IsOpen = true;
        }

        private void TaskList_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && TaskList.SelectedItem is TaskItem task)
            {
                TextCopy.Copy(task.Title);
                e.Handled = true;
            }
        }

        private void NoteButton_Click(object sender, RoutedEventArgs e)
        {
            string note = InputDialog.Prompt(this, "Project Note", "Write anything you want to remember about this project.",
                currentProject.Notes ?? "", "Save", multiline: true, allowEmpty: true);
            if (note == null) return;

            currentProject.Notes = note;
            UpdateNoteDisplay();
        }

        private void UpdateNoteDisplay()
        {
            if (string.IsNullOrWhiteSpace(currentProject.Notes))
            {
                NoteText.Text = "No notes";
                NoteText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#8A8A87"));
                NoteButton.Content = "Add Note";
            }
            else
            {
                NoteText.Text = currentProject.Notes;
                NoteText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1C1B19"));
                NoteButton.Content = "Edit Note";
            }
        }
    }
}