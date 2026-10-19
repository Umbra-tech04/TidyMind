using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace TidyMind
{
    public partial class QuickNotesWindow : Window
    {
        private static readonly string NotesFilePath = AppPaths.Data("quicknotes.txt");

        private static QuickNotesWindow instance;

        private bool isLoading;
        private bool unreadable;

        private readonly DispatcherTimer saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        private bool savePending;
        private bool warnedSaveFailed;

        private QuickNotesWindow()
        {
            InitializeComponent();
            saveTimer.Tick += (s, e) => SaveNow();
            LoadNotes();
            Loaded += (s, e) => FocusNotesTextBox();
        }

        public static bool IsOpen => instance != null;

        // Where the keyboard was when the note opened (a window of TidyMind and the element in it): given back when
        // the note is closed from itself (Esc, its X), so the user carries on where they were. Nothing is reloaded.
        private Window returnWindow;
        private UIElement returnFocus;
        private bool closingWhileActive;

        // An ordinary window: owned by the main window, never Topmost. Another application in front puts it behind
        // that application together with TidyMind; switching back to TidyMind (taskbar, Alt+Tab) brings it back with
        // the main window. Windows also hides it while the main window is minimized and brings it back on restore
        // (it has no taskbar button of its own to restore it from).
        public static void ShowOrFocus()
        {
            if (instance == null)
            {
                Window active = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
                instance = new QuickNotesWindow
                {
                    Owner = Application.Current.MainWindow,
                    returnWindow = active,
                    returnFocus = active != null && Keyboard.FocusedElement is UIElement focused && active.IsAncestorOf(focused) ? focused : null
                };
                instance.Show();
            }
            else
            {
                if (instance.WindowState == WindowState.Minimized)
                    instance.WindowState = WindowState.Normal;

                instance.Activate();
                instance.FocusNotesTextBox();
            }
        }

        public static void CloseIfOpen() => instance?.Close();

        private void FocusNotesTextBox()
        {
            NotesTextBox.Focus();
            Keyboard.Focus(NotesTextBox);
            NotesTextBox.CaretIndex = NotesTextBox.Text.Length;
        }

        // Closing never throws typed text away. If it can't be saved, the window stays open with the text in it and
        // says so; it closes once a save works.
        protected override void OnClosing(CancelEventArgs e)
        {
            if (savePending && !TrySave())
            {
                e.Cancel = true;
                Msg.Show(this, Loc.F("Notes.SaveFailedOnClose", NotesFilePath), Loc.T("Common.CouldntSave"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            base.OnClosing(e);
            closingWhileActive = !e.Cancel && IsActive;
        }

        // A close that can't be refused (TidyMind itself closing) still mustn't lose the text: it goes to a separate
        // file instead, and the message says where.
        protected override void OnClosed(System.EventArgs e)
        {
            if (savePending && !TrySave())
                RescueUnsavedText();
            base.OnClosed(e);
            instance = null;

            // Closed from itself: once Windows has activated the owner, back to where the note was opened from.
            if (closingWhileActive)
                Dispatcher.BeginInvoke(new Action(ReturnToPrevious), DispatcherPriority.Input);
        }

        // The window and element from before, if they are still there and can take the keyboard (the project they
        // showed may have been closed or deleted, a dialog may be in the way, the window may be minimized): then
        // whatever Windows activated stays, and nothing else changes.
        private void ReturnToPrevious()
        {
            Window window = returnWindow;
            if (window == null || !window.IsVisible || window.WindowState == WindowState.Minimized
                || !Application.Current.Windows.OfType<Window>().Contains(window)
                || !IsWindowEnabled(new WindowInteropHelper(window).Handle))
                return;
            window.Activate();
            if (returnFocus != null && returnFocus.IsVisible && returnFocus.IsEnabled && window.IsAncestorOf(returnFocus))
                Keyboard.Focus(returnFocus);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool IsWindowEnabled(IntPtr hWnd);

        private void RescueUnsavedText()
        {
            string name = "quicknotes-unsaved-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt";
            foreach (string path in new[] { AppPaths.Data(name), Path.Combine(Path.GetTempPath(), name) })
            {
                if (AtomicFile.TryWriteAllText(path, NotesTextBox.Text))
                {
                    Msg.Show(Loc.F("Notes.Rescued", path), Loc.T("Common.CouldntSave"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }
            Msg.Show(Loc.T("Notes.Lost"), Loc.T("Common.CouldntSave"), MessageBoxButton.OK, MessageBoxImage.Error);
        }

        // An unreadable file is never saved over: the box says why and can't be typed in.
        private void LoadNotes()
        {
            isLoading = true;

            try
            {
                if (File.Exists(NotesFilePath))
                    NotesTextBox.Text = File.ReadAllText(NotesFilePath);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                unreadable = true;
                NotesTextBox.IsReadOnly = true;
                string problem = e.Message;
                NotesTextBox.Text = Loc.F("Notes.Unreadable", problem);
                Loc.Track(this, () => NotesTextBox.Text = Loc.F("Notes.Unreadable", problem));
            }

            isLoading = false;
        }

        // Saved shortly after typing pauses rather than on every keystroke: each save waits for the disk (so a power
        // cut can't lose it), which would make typing stutter. Closing the window saves whatever is still pending.
        private void NotesTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (isLoading || unreadable) return;

            savePending = true;
            saveTimer.Stop();
            saveTimer.Start();
        }

        private bool TrySave()
        {
            saveTimer.Stop();
            if (!AtomicFile.TryWriteAllText(NotesFilePath, NotesTextBox.Text))
                return false;

            savePending = false;
            warnedSaveFailed = false;
            return true;
        }

        private void SaveNow()
        {
            if (!savePending || TrySave()) return;

            if (!warnedSaveFailed)
            {
                // Once per failing stretch, not on every pause in typing; the next pause tries again.
                warnedSaveFailed = true;
                Msg.Show(Loc.T("Notes.SaveFailed"), Loc.T("Common.CouldntSave"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
                Close();
        }
    }
}
