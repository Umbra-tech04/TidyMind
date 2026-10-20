using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace TidyMind
{
    // A notebook of pages, each stored the same way as a project's description (formatted XAML). The same notebook
    // serves a project (in Project Details) and a whole memory (NotebookWindow): its owner hands it the pages and an
    // Id, and on closing takes back the pages if they changed (TakeEdited). Nothing here reads or writes a file.
    public partial class NotebookEditor : UserControl
    {
        // The page last viewed in each notebook while the app runs (not stored), by its owner's Id (a project's or a
        // memory's); a search hit sets it to open there.
        private static readonly Dictionary<Guid, int> lastPageViewed = new Dictionary<Guid, int>();

        private Window host;
        private Guid owner;
        private UndoHistory history;

        private List<NotePage> pages = new List<NotePage>();      // as loaded, so fields this version doesn't know are kept
        private List<string> pageBodies = new List<string>();     // each page's text in storage form, kept while flipping
        private int currentPage;
        private string shownBody = "";                            // the current page as the editor showed it on opening
        private List<NotePage> loadedPages = new List<NotePage>(); // to tell on close whether anything really changed
        private List<string> loadedBodies = new List<string>();
        private ItemSelection<NotePage> pageSelection;

        public NotebookEditor()
        {
            InitializeComponent();
        }

        public static void OpenAtPage(Guid owner, int page) => lastPageViewed[owner] = page;

        // pages: what the notebook shows (never empty; a project from before pages passes its old note as page 1).
        // history: where a deleted page goes, so Ctrl+Z in the window brings it back.
        public void Load(Window window, Guid ownerId, List<NotePage> notebook, UndoHistory undo)
        {
            host = window;
            owner = ownerId;
            history = undo;
            pages = notebook;
            pageBodies = pages.Select(p => p.Body ?? "").ToList();
            loadedPages = pages.ToList();
            loadedBodies = pageBodies.ToList();

            NotesToolbar.Attach(NotesBox);
            RichTextHelper.PasteOutsideTextAsPlain(NotesBox);
            RichTextHelper.ShowFocusOn(NotesFrame, NotesBox);
            RichTextHelper.FollowCaret(NotesBox);

            NotesBox.TextChanged += (s, e) =>
            {
                UpdateNotesPlaceholder();
                if (currentPage == pages.Count - 1)
                    UpdatePageButtons(); // ▶ on the last page depends on whether it's still empty
            };
            host.PreviewKeyDown += FlipPageKeys;

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

            lastPageViewed.TryGetValue(owner, out int last);
            ShowPage(Math.Clamp(last, 0, pages.Count - 1), 0);
        }

        // The label over the pages, if not "Notes" (a memory's notebook calls itself one).
        public void Label(string key)
        {
            NotebookLabel.Text = Loc.T(key);
            Loc.Track(NotebookLabel, () => NotebookLabel.Text = Loc.T(key));
        }

        public RichTextBox Editor => NotesBox;

        // Esc: an open page list closes first. True if it did (the window then stays open).
        public bool CloseList()
        {
            if (!PageListPopup.IsOpen)
                return false;
            PageListPopup.IsOpen = false;
            return true;
        }

        // On closing: the pages to store if they really changed (blank pages added here and left at the end dropped
        // first), else null so nothing is written.
        public List<NotePage>? TakeEdited()
        {
            KeepCurrentPage();
            DropEmptyNewPages(0);
            if (!PagesEdited(pages.Count))
                return null;
            for (int i = 0; i < pages.Count; i++)
                pages[i].Body = pageBodies[i];
            return pages;
        }

        // What the notebook shows now, unsaved edits included, as copies (Export for transfer); null if unchanged.
        // Nothing is dropped from the notebook itself.
        public List<NotePage>? AsShown()
        {
            KeepCurrentPage();
            int kept = PagesToKeep(0);
            if (!PagesEdited(kept))
                return null;
            return pages.Take(kept).Select((page, i) => new NotePage { Body = pageBodies[i], ExtensionData = page.ExtensionData }).ToList();
        }

        // Whether the first `count` pages differ from the notebook as loaded (an empty page added here and left blank
        // isn't a change).
        private bool PagesEdited(int count) =>
            !pages.Take(count).SequenceEqual(loadedPages) || !pageBodies.Take(count).SequenceEqual(loadedBodies);

        // A change made here, said in the window's toast when it's a delete.
        private void Did(string label, bool destructive, Action undo, Action redo)
        {
            LocalStep step = new LocalStep(label, destructive, undo, redo);
            history.Push(step);
            if (destructive)
                UndoHost.Announce(step);
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
            lastPageViewed[owner] = index;

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

        // Pages added here and left blank at the end go again, so they don't pile up: never the only page, never one
        // up to keepThrough (the page being turned to), never a page that was already stored.
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
        // stored like any edit (TakeEdited), and the cursor stays in it; when every page goes, page 1 stays, emptied.
        private bool DeletePages(IReadOnlyList<NotePage> doomed)
        {
            KeepCurrentPage();
            List<int> indexes = doomed.Select(p => pages.IndexOf(p)).Where(i => i >= 0).OrderBy(i => i).ToList();
            if (indexes.Count == 0)
                return false;
            bool all = indexes.Count == pages.Count;

            if (pages.Count == 1)
            {
                if (CurrentPageIsBlank() || Msg.Show(host, Loc.T("ProjectWindow.ClearPageConfirm"), Loc.T("ProjectWindow.ClearPageCaption"),
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
                if (!PageIsBlank(indexes[0]) && Msg.Show(host, Loc.F("ProjectWindow.DeletePageConfirm", indexes[0] + 1), Loc.T("Common.ConfirmDelete"),
                        MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    return false;
            }
            else if (!BulkDelete.Confirm(host, "Bulk.Delete.Pages", indexes.Select(PageTitle).ToList(),
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
