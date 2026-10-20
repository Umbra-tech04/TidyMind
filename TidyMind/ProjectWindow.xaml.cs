using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace TidyMind
{
    public partial class ProjectWindow : Window
    {
        private Project currentProject;
        private string loadedDescription;

        // The page last viewed in each project while the app runs (not stored); a search hit sets it to open there.
        private static readonly Dictionary<Guid, int> lastPageViewed = new Dictionary<Guid, int>();

        private List<NotePage> pages = new List<NotePage>();      // as loaded, so fields this version doesn't know are kept
        private List<string> pageBodies = new List<string>();     // each page's text in storage form, kept while flipping
        private int currentPage;
        private string shownBody = "";                            // the current page as the editor showed it on opening
        private List<NotePage> loadedPages = new List<NotePage>(); // to tell on close whether anything really changed
        private List<string> loadedBodies = new List<string>();
        private ItemSelection<NotePage> pageSelection;

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
            SetUpNotebook();

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

        public static void OpenAtPage(Guid projectId, int page) => lastPageViewed[projectId] = page;

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
            KeepCurrentPage();
            int kept = PagesToKeep(0);
            if (PagesEdited(kept))
                copy.NotePages = pages.Take(kept)
                    .Select((page, i) => new NotePage { Body = pageBodies[i], ExtensionData = page.ExtensionData }).ToList();
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

        // Whether the first `count` pages differ from the notebook as loaded (an empty page added here and left blank
        // isn't a change).
        private bool PagesEdited(int count) =>
            !pages.Take(count).SequenceEqual(loadedPages) || !pageBodies.Take(count).SequenceEqual(loadedBodies);

        // Esc closes like Save and the X (every way of closing saves); an open page list closes first.
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Key != Key.Escape || e.Handled) return;
            e.Handled = true;
            if (PageListPopup.IsOpen)
                PageListPopup.IsOpen = false;
            else if (TaskList.SelectedItems.Count > 1)
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
            KeepCurrentPage();
            DropEmptyNewPages(0);
            if (PagesEdited(pages.Count))
            {
                for (int i = 0; i < pages.Count; i++)
                    pages[i].Body = pageBodies[i];
                currentProject.NotePages = pages;
            }
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
            ShowFocusOn(EditorFrame, DescriptionBox);
            FollowCaret(DescriptionBox);
        }

        // The frame shows focus like the other inputs do.
        private void ShowFocusOn(Border frame, RichTextBox editor)
        {
            Brush rest = frame.BorderBrush;
            editor.IsKeyboardFocusWithinChanged += (s, e) =>
                frame.BorderBrush = editor.IsKeyboardFocusWithin ? (Brush)FindResource("Accent") : rest;
        }

        // The editors (description, notes page) are as tall as their text and never scroll themselves; the wheel
        // passes through them to the page. Typing or moving the cursor past the bottom of the window scrolls the page
        // along, as a scrolling editor would. After layout, so the caret's place is up to date.
        private static void FollowCaret(RichTextBox editor)
        {
            editor.SelectionChanged += (s, e) =>
            {
                if (!editor.IsKeyboardFocused)
                    return;
                editor.Dispatcher.BeginInvoke(new Action(() =>
                {
                    Rect caret = editor.CaretPosition.GetCharacterRect(LogicalDirection.Forward);
                    if (!caret.IsEmpty)
                        editor.BringIntoView(new Rect(caret.X, caret.Y - 8, Math.Max(caret.Width, 1), caret.Height + 16));
                }), System.Windows.Threading.DispatcherPriority.Loaded);
            };
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

        // ---- Notes --------------------------------------------------------

        // A notebook of pages, each stored the same way as the description (formatted XAML). A project from before
        // pages shows its old note as page 1; nothing is stored in the new form until something is edited (OnClosing).
        private void SetUpNotebook()
        {
            pages = currentProject.PagesOrLegacyNote();
            pageBodies = pages.Select(p => p.Body ?? "").ToList();
            loadedPages = pages.ToList();
            loadedBodies = pageBodies.ToList();

            NotesToolbar.Attach(NotesBox);
            RichTextHelper.PasteOutsideTextAsPlain(NotesBox);
            ShowFocusOn(NotesFrame, NotesBox);
            FollowCaret(NotesBox);

            NotesBox.TextChanged += (s, e) =>
            {
                UpdateNotesPlaceholder();
                if (currentPage == pages.Count - 1)
                    UpdatePageButtons(); // ▶ on the last page depends on whether it's still empty
            };
            PreviewKeyDown += FlipPageKeys;

            // Several pages in the ☰ list, as in the other lists; the keys work while the keyboard is in the list, so
            // Delete and Ctrl+A in the editor stay the editor's.
            pageSelection = new ItemSelection<NotePage>(PageListPanel, DeletePages, PaintPageRow);
            pageSelection.ClearOnBlankClick(PageListPanel);
            pageSelection.SelectionChanged += () =>
            {
                PageSelectionBar.Show(pageSelection.Count);
                PageSelectionBar.Visibility = pageSelection.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            };
            PageSelectionBar.DeleteClicked += () => pageSelection.DeleteSelected();
            PageSelectionBar.CancelClicked += pageSelection.ClearSelection;

            lastPageViewed.TryGetValue(currentProject.Id, out int last);
            ShowPage(Math.Clamp(last, 0, pages.Count - 1), 0);
        }

        // direction: 1 turning forward, -1 back, 0 no page turn (opening, after a delete).
        private void ShowPage(int index, int direction)
        {
            currentPage = index;
            RichTextHelper.FromStorableString(NotesBox.Document, pageBodies[index]);
            shownBody = RichTextHelper.ToStorableString(NotesBox.Document);

            // A fresh undo history per page, so Ctrl+Z can't bring back another page's text.
            NotesBox.IsUndoEnabled = false;
            NotesBox.IsUndoEnabled = true;
            lastPageViewed[currentProject.Id] = index;

            PageIndicator.Text = (index + 1) + " / " + pages.Count;
            UpdatePageButtons();
            UpdateNotesPlaceholder();

            // The page turn: the new page slides in from the side it comes from. Only the sheet's rendering moves
            // (no layout), so typing, the caret and undo work as usual while it plays.
            if (direction != 0)
            {
                Duration turn = TimeSpan.FromMilliseconds(180);
                PageSheet.RenderTransform.BeginAnimation(TranslateTransform.XProperty,
                    new DoubleAnimation(direction * 60, 0, turn) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
                PageSheet.BeginAnimation(OpacityProperty, new DoubleAnimation(0.2, 1, turn));
            }
        }

        private void UpdatePageButtons()
        {
            SetEnabled(PrevPageButton, currentPage > 0);
            SetEnabled(NextPageButton, currentPage < pages.Count - 1 || !CurrentPageIsBlank());
            // With one page the button clears it instead (nothing to do on an empty one).
            bool single = pages.Count == 1;
            SetEnabled(DeletePageButton, !single || !CurrentPageIsBlank());
            DeletePageButton.ToolTip = Loc.T(single ? "ProjectWindow.ClearPage" : "ProjectWindow.DeletePage");
            System.Windows.Automation.AutomationProperties.SetName(DeletePageButton, (string)DeletePageButton.ToolTip);
        }

        private static void SetEnabled(Button button, bool enabled)
        {
            button.IsEnabled = enabled;
            button.Opacity = enabled ? 1 : 0.4;
        }

        // Checked on every key typed on the last page, so it stops at the first visible character instead of reading
        // the whole page: a long page with text costs next to nothing, and only a (short) blank one is read through.
        private bool CurrentPageIsBlank()
        {
            for (TextPointer at = NotesBox.Document.ContentStart; at != null; at = at.GetNextContextPosition(LogicalDirection.Forward))
            {
                if (at.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text
                    && !string.IsNullOrWhiteSpace(at.GetTextInRun(LogicalDirection.Forward)))
                    return false;
            }
            return true;
        }

        // Pages added in this window and left blank at the end go again, so they don't pile up: never the only page,
        // never one up to keepThrough (the page being turned to), never a page that was already stored.
        private void DropEmptyNewPages(int keepThrough)
        {
            int kept = PagesToKeep(keepThrough);
            pages.RemoveRange(kept, pages.Count - kept);
            pageBodies.RemoveRange(kept, pageBodies.Count - kept);
        }

        // How many pages stay once those are dropped (Export for transfer asks without dropping them).
        private int PagesToKeep(int keepThrough)
        {
            int count = pages.Count;
            while (count - 1 > keepThrough && !loadedPages.Contains(pages[count - 1])
                && RichTextHelper.ToPlainText(pageBodies[count - 1]).Trim().Length == 0)
                count--;
            return count;
        }

        // Compared with the page as the editor showed it, not the stored string: an untouched page re-saved in
        // storage form would otherwise count as an edit.
        private void KeepCurrentPage()
        {
            string body = RichTextHelper.ToStorableString(NotesBox.Document);
            if (body == shownBody) return;
            pageBodies[currentPage] = body;
            shownBody = body;
        }

        private void FlipTo(int index)
        {
            if (index < 0 || index >= pages.Count || index == currentPage) return;
            int from = currentPage;
            KeepCurrentPage();
            DropEmptyNewPages(index);
            ShowPage(index, index > from ? 1 : -1);
        }

        // On the last page ▶ starts a new one, unless that page is still empty (no run of blank pages).
        private void TurnForward()
        {
            if (currentPage < pages.Count - 1)
            {
                FlipTo(currentPage + 1);
                return;
            }
            if (CurrentPageIsBlank()) return;

            KeepCurrentPage();
            pages.Add(new NotePage());
            pageBodies.Add("");
            ShowPage(pages.Count - 1, 1);
            NotesBox.Focus();
            TourSignals.Raise(TourSignals.NotePageAdded);
        }

        private void PrevPage_Click(object sender, RoutedEventArgs e) => FlipTo(currentPage - 1);

        private void NextPage_Click(object sender, RoutedEventArgs e) => TurnForward();

        private void FlipPageKeys(object sender, KeyEventArgs e)
        {
            if (Keyboard.Modifiers != ModifierKeys.Control || (e.Key != Key.PageUp && e.Key != Key.PageDown)) return;
            if (e.Key == Key.PageUp)
                FlipTo(currentPage - 1);
            else
                TurnForward();
            e.Handled = true;
        }

        private void DeletePage_Click(object sender, RoutedEventArgs e) => DeletePages(new[] { pages[currentPage] });

        // One page (the 🗑 button, or one selected in the list): only a page with writing on it asks first. Several:
        // one question for all of them. Then the page after the current one shows if the current one went (the one
        // before if it was the last). There's always at least one page: the only page is cleared instead, which is
        // stored like any edit (OnClosing), and the cursor stays in it; when every page goes, page 1 stays, emptied.
        private bool DeletePages(IReadOnlyList<NotePage> doomed)
        {
            KeepCurrentPage();
            List<int> indexes = doomed.Select(p => pages.IndexOf(p)).Where(i => i >= 0).OrderBy(i => i).ToList();
            if (indexes.Count == 0)
                return false;
            bool all = indexes.Count == pages.Count;

            if (pages.Count == 1)
            {
                if (CurrentPageIsBlank() || Msg.Show(this, Loc.T("ProjectWindow.ClearPageConfirm"), Loc.T("ProjectWindow.ClearPageCaption"),
                        MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    return false;

                PageListPopup.IsOpen = false;
                NotePage only = pages[0];
                string text = pageBodies[0];
                NotesBox.Document.Blocks.Clear();
                NotesBox.Focus();
                // A redo first keeps what was written on the page since the undo, for the next undo.
                Did(Loc.T("Undo.PageCleared"), true, () => SetBody(only, text), () => text = SetBody(only, "") ?? text);
                return true;
            }

            if (indexes.Count == 1)
            {
                if (!PageIsBlank(indexes[0]) && Msg.Show(this, Loc.F("ProjectWindow.DeletePageConfirm", indexes[0] + 1), Loc.T("Common.ConfirmDelete"),
                        MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    return false;
            }
            else if (!BulkDelete.Confirm(this, "Bulk.Delete.Pages", indexes.Select(PageTitle).ToList(),
                         all ? Loc.T("ProjectWindow.DeleteAllPagesNote") : null))
                return false;

            // Where the current page is now, or the page that followed it.
            int shown = currentPage;
            int showNext = Enumerable.Range(0, currentPage).Count(i => !indexes.Contains(i));
            string label = indexes.Count == 1 ? Loc.F("Undo.Deleted", PageTitle(indexes[0])) : Loc.Plural("Undo.Deleted.Pages", indexes.Count);
            NotePage first = pages[0];
            string? firstBody = all ? pageBodies[0] : null;
            if (all)
            {
                indexes.RemoveAt(0);
                pageBodies[0] = "";
            }
            // The pages and their text, kept for an undo; a redo takes their text again first (it may have been written on
            // since the undo).
            List<(NotePage Page, int At)> gone = indexes.Select(i => (pages[i], i)).ToList();
            Dictionary<NotePage, string> texts = indexes.ToDictionary(i => pages[i], i => pageBodies[i]);
            for (int k = indexes.Count - 1; k >= 0; k--)
            {
                pages.RemoveAt(indexes[k]);
                pageBodies.RemoveAt(indexes[k]);
            }
            ShowPage(Math.Min(showNext, pages.Count - 1), 0);

            pageSelection.ClearSelection();
            if (PageListPopup.IsOpen)
                ShowPageList();

            // Undone, the pages are back at their places (with what page 1 said, when all went), the page shown before shows.
            Did(label, true, () =>
            {
                KeepCurrentPage();
                foreach ((NotePage page, int at) in gone)
                {
                    pages.Insert(Math.Min(at, pages.Count), page);
                    pageBodies.Insert(Math.Min(at, pageBodies.Count), texts[page]);
                }
                int firstAt = pages.IndexOf(first);
                if (firstBody != null && firstAt >= 0)
                    pageBodies[firstAt] = firstBody;
                ShowPage(Math.Clamp(shown, 0, pages.Count - 1), 0);
                if (PageListPopup.IsOpen)
                    ShowPageList();
            }, () =>
            {
                KeepCurrentPage();
                foreach ((NotePage page, _) in gone)
                {
                    int at = pages.IndexOf(page);
                    if (at >= 0 && pages.Count > 1)
                    {
                        texts[page] = pageBodies[at];
                        pages.RemoveAt(at);
                        pageBodies.RemoveAt(at);
                    }
                }
                int firstAt = pages.IndexOf(first);
                if (firstBody != null && firstAt >= 0)
                {
                    firstBody = pageBodies[firstAt];
                    pageBodies[firstAt] = "";
                }
                ShowPage(Math.Clamp(showNext, 0, pages.Count - 1), 0);
                if (PageListPopup.IsOpen)
                    ShowPageList();
            });
            return true;
        }

        // An undo or redo of emptying the only page: its text, and the page shown again from it. Returns what the page
        // said before (null if it's gone).
        private string? SetBody(NotePage page, string body)
        {
            KeepCurrentPage();
            int at = pages.IndexOf(page);
            if (at < 0)
                return null;
            string was = pageBodies[at];
            pageBodies[at] = body;
            ShowPage(at, 0);
            return was;
        }

        private bool PageIsBlank(int index) => RichTextHelper.ToPlainText(pageBodies[index]).Trim().Length == 0;

        // "N. <its first line>", in the list and in the question before several are deleted.
        private string PageTitle(int index)
        {
            string firstLine = RichTextHelper.ToPlainText(pageBodies[index]).Split('\n')
                .Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0) ?? Loc.T("ProjectWindow.EmptyPage");
            return (index + 1) + ". " + firstLine;
        }

        // When the list last closed because of a press on ☰ itself (Environment.TickCount64; see PageList_Click).
        private long pageListClosedByButtonAt;

        // The list closes on any press outside it, ☰ included; that same press then arrives at ☰ as a click. A
        // selection doesn't outlive the list.
        private void PageListPopup_Closed(object sender, EventArgs e)
        {
            Point at = Mouse.GetPosition(PageListButton);
            if (new Rect(PageListButton.RenderSize).Contains(at))
                pageListClosedByButtonAt = Environment.TickCount64;
            pageSelection.ClearSelection();
        }

        // ☰ toggles: the click that just closed the list doesn't open it again.
        private void PageList_Click(object sender, RoutedEventArgs e)
        {
            if (Environment.TickCount64 - pageListClosedByButtonAt < 500)
            {
                pageListClosedByButtonAt = 0;
                return;
            }

            KeepCurrentPage();
            ShowPageList();
            PageListPopup.IsOpen = true;
        }

        // One row per page; a click jumps there, Ctrl or Shift+click selects (taken before the click).
        private void ShowPageList()
        {
            pageSelection.BeginRender();
            PageListRows.Children.Clear();
            for (int i = 0; i < pages.Count; i++)
            {
                int page = i;
                Button row = new Button
                {
                    Content = PageTitle(i),
                    Style = (Style)FindResource("PageRowStyle"),
                    FontWeight = i == currentPage ? FontWeights.SemiBold : FontWeights.Normal
                };
                pageSelection.Attach(row, pages[i]);
                row.Click += (s, args) =>
                {
                    PageListPopup.IsOpen = false;
                    FlipTo(page);
                };
                PageListRows.Children.Add(row);
            }
            pageSelection.EndRender();
        }

        private static void PaintPageRow(FrameworkElement row, bool on)
        {
            if (on)
            {
                row.SetValue(Control.BackgroundProperty, SelectionLook.RowFill);
                row.SetValue(Control.BorderBrushProperty, SelectionLook.RowEdge);
            }
            else
            {
                row.ClearValue(Control.BackgroundProperty);
                row.ClearValue(Control.BorderBrushProperty);
            }
        }

        // "Start writing..." only on a blank page: one empty line, not just a page of blank lines.
        private void UpdateNotesPlaceholder()
        {
            FlowDocument page = NotesBox.Document;
            bool blank = page.Blocks.Count <= 1
                && new TextRange(page.ContentStart, page.ContentEnd).Text.TrimEnd('\r', '\n').Length == 0;
            NotesPlaceholder.Visibility = blank ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}