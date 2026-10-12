using System;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

        public static void ShowOrFocus()
        {
            if (instance == null)
            {
                // Owned by the main window: Windows hides it while the main window is minimized and brings it
                // back on restore (it has no taskbar button of its own to restore it from).
                instance = new QuickNotesWindow { Owner = Application.Current.MainWindow };
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
                MessageBox.Show(this, "Couldn't save your notes: this file can't be written:\n" + NotesFilePath
                    + "\n\nThe window stays open so nothing is lost. Close it again once the file can be written.",
                    "Couldn't Save", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            base.OnClosing(e);
        }

        // A close that can't be refused (TidyMind itself closing) still mustn't lose the text: it goes to a separate
        // file instead, and the message says where.
        protected override void OnClosed(System.EventArgs e)
        {
            if (savePending && !TrySave())
                RescueUnsavedText();
            base.OnClosed(e);
            instance = null;
        }

        private void RescueUnsavedText()
        {
            string name = "quicknotes-unsaved-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt";
            foreach (string path in new[] { AppPaths.Data(name), Path.Combine(Path.GetTempPath(), name) })
            {
                if (AtomicFile.TryWriteAllText(path, NotesTextBox.Text))
                {
                    MessageBox.Show("Couldn't save your notes to quicknotes.txt, so they were saved here instead:\n" + path,
                        "Couldn't Save", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }
            MessageBox.Show("Couldn't save your notes anywhere: neither quicknotes.txt nor a separate file could be "
                + "written.", "Couldn't Save", MessageBoxButton.OK, MessageBoxImage.Error);
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
                NotesTextBox.Text = "Couldn't read quicknotes.txt: " + e.Message + "\n\nThe file was left as it is. "
                    + "Close this window and open it again once the file can be read.";
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
                MessageBox.Show("Couldn't save: quicknotes.txt can't be written. Your notes are still here and will be "
                    + "saved as soon as it works again.", "Couldn't Save", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
                Close();
        }
    }
}
