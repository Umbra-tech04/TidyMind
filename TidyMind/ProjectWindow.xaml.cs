using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
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
            SetUpDescriptionEditor();

            if (currentProject.Tasks == null)
                currentProject.Tasks = new List<TaskItem>();

            // The open tasks, then the done ones (TaskOrder): what is stored keeps its order.
            foreach (TaskItem task in TaskOrder.OpenFirst(currentProject.Tasks, t => t.IsDone))
                TaskList.Items.Add(task);

            Files.Load(currentProject.Attachments);
            // A project from before pages shows its old note as page 1; nothing is stored in the new form until
            // something is edited (OnClosing).
            Notebook.Load(this, currentProject.Id, currentProject.PagesOrLegacyNote(), local);

            // Ctrl+Z here takes back what was done in this window (tasks, pages, files), which is stored only when it
            // closes; once closed, all of it is one step of the project page.
            Files.History = local;
            UndoHost.Attach(this, () => new[] { local }, bottom: 80); // above the footer with Save
            Closed += (s, e) => local.End();
        }

        private readonly UndoHistory local = new UndoHistory("project window");

        // A change made in this window, said in its toast when it's a delete.
        private void Did(string label, bool destructive, Action undo, Action redo)
        {
            LocalStep step = new LocalStep(label, destructive, undo, redo);
            local.Push(step);
            if (destructive)
                UndoHost.Announce(step);
        }

        public static void OpenAtPage(Guid projectId, int page) => NotebookEditor.OpenAtPage(projectId, page);

        // Taken out in this window: the owner deletes their stored copies once it has saved the project without them.
        public IReadOnlyList<Attachment> RemovedAttachments => Files.Removed;

        private void SaveButton_Click(object sender, RoutedEventArgs e) => Close();

        private void TransferButton_Click(object sender, RoutedEventArgs e) => TransferUI.ExportProject(this, ProjectAsShown());

        // A copy of the project with what this window shows now (name, description, notebook, files), edits not yet
        // saved included; the project itself is only changed on closing (OnClosing). Files still being copied in
        // aren't attached yet, so they aren't in it.
        internal Project ProjectAsShown()
        {
            Project copy = JsonSerializer.Deserialize<Project>(JsonSerializer.Serialize(currentProject));
            string name = NameBox.Text.Trim();
            if (name.Length > 0)
                copy.Name = name;
            if (EditedDescription() is string description)
                copy.Description = description;
            if (Notebook.AsShown() is List<NotePage> pages)
                copy.NotePages = pages;
            copy.Attachments = Files.Attachments;
            return copy;
        }

        // The description as stored, if it was edited (null if not). Compared with the editor as loaded, not the
        // stored string: re-saving an untouched null or plain-text description would change its storage form and
        // count as an edit (LastModified).
        private string EditedDescription()
        {
            string description = RichTextHelper.ToStorableString(DescriptionBox.Document);
            return description != loadedDescription ? description : null;
        }

        // Esc closes like Save and the X (every way of closing saves); an open page list closes first.
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Key != Key.Escape || e.Handled) return;
            e.Handled = true;
            if (Notebook.CloseList())
                return;
            if (TaskList.SelectedItems.Count > 1)
                TaskList.UnselectAll(); // several tasks selected: Esc drops the selection first
            else
                Close();
        }

        // Tasks already change the project live, so every way of closing saves the rest too — otherwise the
        // title-bar X would keep the tasks but silently drop the description and the note.
        protected override void OnClosing(CancelEventArgs e)
        {
            // Closing stops a file still being copied in (nothing half-copied is kept), so ask first.
            if (Files.IsCopying && Msg.Show(this, Loc.T("ProjectWindow.StillCopying"), Loc.T("ProjectWindow.StillCopyingCaption"),
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }

            // Every way of closing saves, so an empty name would be saved too: the window stays open and says why.
            string name = NameBox.Text.Trim();
            if (name.Length == 0)
            {
                e.Cancel = true;
                NameError.Visibility = Visibility.Visible;
                NameBox.Focus();
                return;
            }

            base.OnClosing(e);
            Files.Close();
            currentProject.Attachments = Files.Attachments;

            currentProject.Name = name;

            if (EditedDescription() is string description)
                currentProject.Description = description;

            // Only a real change stores the pages; the old Notes field is never touched, so an old note shown as
            // page 1 stays where older versions look for it.
            if (Notebook.TakeEdited() is List<NotePage> pages)
                currentProject.NotePages = pages;
        }

        private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (NameBox.Text.Trim().Length > 0)
                NameError.Visibility = Visibility.Collapsed;
        }

        // ---- Description editor -------------------------------------------

        private void SetUpDescriptionEditor()
        {
            RichTextHelper.FromStorableString(DescriptionBox.Document, currentProject.Description);
            loadedDescription = RichTextHelper.ToStorableString(DescriptionBox.Document);

            DescriptionToolbar.Attach(DescriptionBox);
            RichTextHelper.PasteOutsideTextAsPlain(DescriptionBox);
            RichTextHelper.ShowFocusOn(EditorFrame, DescriptionBox);
            RichTextHelper.FollowCaret(DescriptionBox);
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
            TaskList.Items.Insert(TaskOrder.OpenFirst(currentProject.Tasks, t => t.IsDone).IndexOf(newTask), newTask); // last of the open ones
            TaskInput.Clear();
        }

        // A tick or an untick (the box has changed the task already): the task goes to the other group, at its own place
        // among that group's tasks as stored. Only that row moves; the others keep their rows, so the selection of the
        // rest, the scroll position and the keyboard (on the same box, which is not scrolled to) stay as they were.
        private void TaskCheck_Click(object sender, RoutedEventArgs e)
        {
            TaskItem task = (TaskItem)((FrameworkElement)sender).DataContext;
            bool done = task.IsDone;
            Did(Loc.F(done ? "Undo.Ticked" : "Undo.Unticked", task.Title), false,
                () => SetDone(task, !done), () => SetDone(task, done));
            MoveToGroup(task, sender);
        }

        // An undo or redo of a tick: the task's row is drawn again (TaskItem has no change notification) in its group.
        private void SetDone(TaskItem task, bool done)
        {
            task.IsDone = done;
            int at = TaskList.Items.IndexOf(task);
            if (at < 0)
                return;
            bool selected = TaskList.SelectedItems.Contains(task);
            TaskOrder.KeepView(TaskList, () =>
            {
                TaskList.Items.RemoveAt(at);
                TaskList.Items.Insert(TaskOrder.OpenFirst(currentProject.Tasks, t => t.IsDone).IndexOf(task), task);
                if (selected)
                    TaskList.SelectedItems.Add(task);
            });
        }

        private void MoveToGroup(TaskItem task, object sender)
        {
            int from = TaskList.Items.IndexOf(task);
            int to = TaskOrder.OpenFirst(currentProject.Tasks, t => t.IsDone).IndexOf(task);
            if (from < 0 || from == to)
                return;
            bool selected = TaskList.SelectedItems.Contains(task);
            bool keyboard = ((CheckBox)sender).IsKeyboardFocused;
            TaskOrder.KeepView(TaskList, () =>
            {
                TaskList.Items.RemoveAt(from);
                TaskList.Items.Insert(to, task);
                if (selected)
                    TaskList.SelectedItems.Add(task);
                if (keyboard)
                {
                    TaskList.UpdateLayout();
                    if (TaskList.ItemContainerGenerator.ContainerFromItem(task) is DependencyObject row && FindCheckBox(row) is CheckBox box)
                        box.Focus();
                }
            });
        }

        private static CheckBox FindCheckBox(DependencyObject parent)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                if (child is CheckBox box || (box = FindCheckBox(child)) != null)
                    return box;
            }
            return null;
        }

        // Where the next Shift+click run starts: the task clicked last.
        private TaskItem taskAnchor;

        // Several tasks at once, as in Explorer: Ctrl+click toggles one, Shift+click selects the run from the last
        // clicked task (Ctrl+Shift adds it), Ctrl+A (the list's own) all. Taken before the row's checkbox, so neither
        // ticks it. A plain click still ticks, and drops the selection; so does a click below the rows.
        private void TaskList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var item = ItemsControl.ContainerFromElement(TaskList, e.OriginalSource as DependencyObject) as ListBoxItem;
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            if (item == null || !(ctrl || shift))
            {
                if (item != null)
                    taskAnchor = (TaskItem)item.DataContext;
                if (!ItemSelection.OnRowButton(e.OriginalSource as DependencyObject, TaskList))
                    TaskList.UnselectAll();
                return;
            }
            if (ItemSelection.OnRowButton(e.OriginalSource as DependencyObject, item))
                return; // the row's own ✎ and ×

            TaskItem task = (TaskItem)item.DataContext;
            e.Handled = true;
            item.Focus(); // so Delete, Esc and Ctrl+A reach the list
            if (!shift)
            {
                item.IsSelected = !item.IsSelected;
                taskAnchor = task;
                return;
            }

            int to = TaskList.Items.IndexOf(task);
            // From the last clicked task (or, with none clicked yet, the selected one).
            int from = TaskList.Items.IndexOf(taskAnchor ?? TaskList.SelectedItem);
            if (from < 0)
            {
                from = to;
                taskAnchor = task;
            }
            if (!ctrl)
                TaskList.SelectedItems.Clear();
            for (int i = Math.Min(from, to); i <= Math.Max(from, to); i++)
            {
                if (!TaskList.SelectedItems.Contains(TaskList.Items[i]))
                    TaskList.SelectedItems.Add(TaskList.Items[i]);
            }
        }

        private List<TaskItem> SelectedTasks() => TaskList.Items.Cast<TaskItem>().Where(t => TaskList.SelectedItems.Contains(t)).ToList();

        private void TaskList_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            var item = ItemsControl.ContainerFromElement(TaskList, e.OriginalSource as DependencyObject) as ListBoxItem;
            if (item == null) return;

            TaskItem task = (TaskItem)item.DataContext;

            // On one of several selected tasks: the menu for all of them.
            if (item.IsSelected && TaskList.SelectedItems.Count > 1)
            {
                MenuItem deleteAll = new MenuItem { Header = Loc.F("Selection.DeleteSelected", TaskList.SelectedItems.Count) };
                deleteAll.Click += (s, args) => DeleteTasks(SelectedTasks());
                MenuItem clear = new MenuItem { Header = Loc.T("Selection.Clear") };
                clear.Click += (s, args) => TaskList.UnselectAll();
                new ContextMenu { Items = { deleteAll, clear }, IsOpen = true };
                return;
            }

            ContextMenu menu = new ContextMenu();

            MenuItem renameItem = new MenuItem();
            renameItem.Header = Loc.T("Common.Rename");
            renameItem.Click += (s, args) => RenameTask(task);

            MenuItem deleteItem = new MenuItem();
            deleteItem.Header = Loc.T("Common.Delete");
            deleteItem.Click += (s, args) => DeleteTask(task);

            menu.Items.Add(renameItem);
            menu.Items.Add(deleteItem);
            menu.Items.Add(new Separator());
            menu.Items.Add(TextCopy.CopyItem(() => task.Title));
            menu.IsOpen = true;
        }

        // A row's hover buttons: the same Rename / Delete as its right-click menu.
        private void TaskRename_Click(object sender, RoutedEventArgs e) => RenameTask((TaskItem)((FrameworkElement)sender).DataContext);

        // On one of several selected tasks, the × deletes all of them (asking first).
        private void TaskDelete_Click(object sender, RoutedEventArgs e)
        {
            TaskItem task = (TaskItem)((FrameworkElement)sender).DataContext;
            if (TaskList.SelectedItems.Contains(task) && TaskList.SelectedItems.Count > 1)
                DeleteTasks(SelectedTasks());
            else
                DeleteTask(task);
        }

        private void RenameTask(TaskItem task)
        {
            string newTitle = InputDialog.Prompt(this, Loc.T("ProjectWindow.RenameTask"), Loc.T("ProjectWindow.RenameTaskPrompt"),
                task.Title, Loc.T("Common.Rename"));
            if (string.IsNullOrWhiteSpace(newTitle) || newTitle == task.Title) return;
            string oldTitle = task.Title;
            Retitle(task, newTitle);
            Did(Loc.F("Undo.RenamedTo", oldTitle, newTitle), false, () => Retitle(task, oldTitle), () => Retitle(task, newTitle));
        }

        private void Retitle(TaskItem task, string title)
        {
            task.Title = title;
            // TaskItem has no change notification: that one row is rebuilt (Refresh would rebuild every row).
            int index = TaskList.Items.IndexOf(task);
            if (index < 0)
                return;
            bool selected = TaskList.SelectedItem == task;
            TaskList.Items.RemoveAt(index);
            TaskList.Items.Insert(index, task);
            if (selected)
                TaskList.SelectedIndex = index;
        }

        private void DeleteTask(TaskItem task) => Remove(new List<TaskItem> { task });

        // Several: one question for all of them, then each goes the way one does. The keyboard stays in the list, on
        // the task now where the first deleted one was.
        private void DeleteTasks(List<TaskItem> tasks)
        {
            if (tasks.Count == 0)
                return;
            if (tasks.Count > 1 && !BulkDelete.Confirm(this, "Bulk.Delete.Tasks", tasks.Select(t => t.Title).ToList()))
                return;
            int first = tasks.Min(t => TaskList.Items.IndexOf(t));
            Remove(tasks);
            if (TaskList.Items.Count > 0
                && TaskList.ItemContainerGenerator.ContainerFromIndex(Math.Min(first, TaskList.Items.Count - 1)) is ListBoxItem row)
            {
                row.IsSelected = true; // so a second Delete takes it
                row.Focus();
            }
        }

        // One step in this window's history: undone, each task is back at its place (as stored, and in its group).
        private void Remove(List<TaskItem> tasks)
        {
            List<(TaskItem Task, int At)> places = tasks.Select(t => (t, currentProject.Tasks.IndexOf(t))).Where(p => p.Item2 >= 0)
                .OrderBy(p => p.Item2).ToList();
            if (places.Count == 0)
                return;
            TakeOut(tasks);
            Did(places.Count == 1 ? Loc.F("Undo.Deleted", places[0].Task.Title) : Loc.Plural("Undo.Deleted.Tasks", places.Count), true,
                () => PutBack(places), () => TakeOut(places.Select(p => p.Task).ToList()));
        }

        private void TakeOut(List<TaskItem> tasks)
        {
            foreach (TaskItem task in tasks)
            {
                currentProject.Tasks.Remove(task);
                TaskList.Items.Remove(task);
            }
        }

        private void PutBack(List<(TaskItem Task, int At)> places)
        {
            foreach ((TaskItem task, int at) in places)
                currentProject.Tasks.Insert(Math.Min(at, currentProject.Tasks.Count), task);
            // In the order shown, each at its place there: the rows before it are all in the list by then.
            List<TaskItem> order = TaskOrder.OpenFirst(currentProject.Tasks, t => t.IsDone).ToList();
            TaskOrder.KeepView(TaskList, () =>
            {
                foreach (TaskItem task in places.Select(p => p.Task).OrderBy(order.IndexOf))
                    TaskList.Items.Insert(Math.Min(order.IndexOf(task), TaskList.Items.Count), task);
            });
        }

        // Delete: the selected tasks (one goes at once, like its ×; several ask first). Ctrl+C: their text, a line each.
        private void TaskList_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete && Keyboard.Modifiers == ModifierKeys.None && TaskList.SelectedItems.Count > 0)
            {
                DeleteTasks(SelectedTasks());
                e.Handled = true;
            }
            else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && TaskList.SelectedItems.Count > 0)
            {
                TextCopy.Copy(string.Join(Environment.NewLine, SelectedTasks().Select(t => t.Title)));
                e.Handled = true;
            }
        }
    }
}