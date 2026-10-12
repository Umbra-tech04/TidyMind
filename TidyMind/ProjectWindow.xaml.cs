using System;
using System.Collections.Generic;
using System.ComponentModel;
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
            SetUpNotebook();
        }

        public static void OpenAtPage(Guid projectId, int page) => lastPageViewed[projectId] = page;

        // Taken out in this window: the owner deletes their stored copies once it has saved the project without them.
        public IReadOnlyList<Attachment> RemovedAttachments => Files.Removed;

        private void SaveButton_Click(object sender, RoutedEventArgs e) => Close();

        // Esc closes like Save and the X (every way of closing saves); an open page list closes first.
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Key != Key.Escape || e.Handled) return;
            e.Handled = true;
            if (PageListPopup.IsOpen)
                PageListPopup.IsOpen = false;
            else
                Close();
        }

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

            // Compared with the editor as loaded, not the stored string: re-saving an untouched null or
            // plain-text description or note would change its storage form and count as an edit (LastModified).
            string description = RichTextHelper.ToStorableString(DescriptionBox.Document);
            if (description != loadedDescription)
                currentProject.Description = description;

            // Only a real change stores the pages (an empty page added here and left blank isn't one); the old Notes
            // field is never touched, so an old note shown as page 1 stays where older versions look for it.
            KeepCurrentPage();
            DropEmptyNewPages(0);
            if (!pages.SequenceEqual(loadedPages) || !pageBodies.SequenceEqual(loadedBodies))
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
            DescriptionBox.PreviewMouseWheel += Editor_PreviewMouseWheel;
        }

        // The frame shows focus like the other inputs do.
        private void ShowFocusOn(Border frame, RichTextBox editor)
        {
            Brush rest = frame.BorderBrush;
            editor.IsKeyboardFocusWithinChanged += (s, e) =>
                frame.BorderBrush = editor.IsKeyboardFocusWithin ? (Brush)FindResource("Accent") : rest;
        }

        // At either end of its own scroll range an editor (description, notes page) passes the wheel on to the
        // window, so it doesn't swallow scrolling (same as the task list).
        private void Editor_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            RichTextBox editor = (RichTextBox)sender;
            bool canScroll = e.Delta < 0
                ? editor.VerticalOffset + editor.ViewportHeight < editor.ExtentHeight - 0.5
                : editor.VerticalOffset > 0.5;
            if (canScroll) return;

            e.Handled = true;
            MouseWheelEventArgs forward = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = UIElement.MouseWheelEvent };
            ((UIElement)VisualTreeHelper.GetParent(editor)).RaiseEvent(forward);
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
            NotesBox.PreviewMouseWheel += Editor_PreviewMouseWheel;

            NotesBox.TextChanged += (s, e) =>
            {
                UpdateNotesPlaceholder();
                if (currentPage == pages.Count - 1)
                    UpdatePageButtons(); // ▶ on the last page depends on whether it's still empty
            };
            PreviewKeyDown += FlipPageKeys;

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
            NotesBox.ScrollToHome();
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
            SetEnabled(DeletePageButton, pages.Count > 1);
        }

        private static void SetEnabled(Button button, bool enabled)
        {
            button.IsEnabled = enabled;
            button.Opacity = enabled ? 1 : 0.4;
        }

        private bool CurrentPageIsBlank() =>
            new TextRange(NotesBox.Document.ContentStart, NotesBox.Document.ContentEnd).Text.Trim().Length == 0;

        // Pages added in this window and left blank at the end go again, so they don't pile up: never the only page,
        // never one up to keepThrough (the page being turned to), never a page that was already stored.
        private void DropEmptyNewPages(int keepThrough)
        {
            while (pages.Count - 1 > keepThrough && !loadedPages.Contains(pages[^1])
                && RichTextHelper.ToPlainText(pageBodies[^1]).Trim().Length == 0)
            {
                pages.RemoveAt(pages.Count - 1);
                pageBodies.RemoveAt(pageBodies.Count - 1);
            }
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

        // There's always at least one page; only a page with writing on it asks first. Then the next page shows
        // (the previous one if this was the last).
        private void DeletePage_Click(object sender, RoutedEventArgs e)
        {
            if (pages.Count <= 1) return;
            if (!CurrentPageIsBlank() && MessageBox.Show(this, "Delete page " + (currentPage + 1) + " and everything written on it?",
                    "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            pages.RemoveAt(currentPage);
            pageBodies.RemoveAt(currentPage);
            ShowPage(Math.Min(currentPage, pages.Count - 1), 0);
        }

        // One row per page, "N. <its first line>"; a click jumps there.
        private void PageList_Click(object sender, RoutedEventArgs e)
        {
            KeepCurrentPage();
            PageListRows.Children.Clear();
            for (int i = 0; i < pages.Count; i++)
            {
                int page = i;
                string firstLine = RichTextHelper.ToPlainText(pageBodies[i]).Split('\n')
                    .Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0) ?? "(empty)";
                Button row = new Button
                {
                    Content = (i + 1) + ". " + firstLine,
                    Style = (Style)FindResource("PageRowStyle"),
                    FontWeight = i == currentPage ? FontWeights.SemiBold : FontWeights.Normal
                };
                row.Click += (s, args) =>
                {
                    PageListPopup.IsOpen = false;
                    FlipTo(page);
                };
                PageListRows.Children.Add(row);
            }
            PageListPopup.IsOpen = true;
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