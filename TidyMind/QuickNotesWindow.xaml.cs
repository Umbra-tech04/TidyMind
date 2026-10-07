using System;
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

        protected override void OnClosed(System.EventArgs e)
        {
            SaveNow();
            base.OnClosed(e);
            instance = null;
        }

        private void LoadNotes()
        {
            isLoading = true;

            if (File.Exists(NotesFilePath))
                NotesTextBox.Text = File.ReadAllText(NotesFilePath);

            isLoading = false;
        }

        // Saved shortly after typing pauses rather than on every keystroke: each save waits for the disk (so a power
        // cut can't lose it), which would make typing stutter. Closing the window saves whatever is still pending.
        private void NotesTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (isLoading) return;

            savePending = true;
            saveTimer.Stop();
            saveTimer.Start();
        }

        private void SaveNow()
        {
            saveTimer.Stop();
            if (!savePending) return;

            if (AtomicFile.TryWriteAllText(NotesFilePath, NotesTextBox.Text))
            {
                savePending = false;
                warnedSaveFailed = false;
            }
            else if (!warnedSaveFailed)
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
