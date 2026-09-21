using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace TidyMind
{
    public partial class QuickNotesWindow : Window
    {
        private const string NotesFilePath = "quicknotes.txt";

        private static QuickNotesWindow instance;

        private bool isLoading;

        private QuickNotesWindow()
        {
            InitializeComponent();
            LoadNotes();
            Loaded += (s, e) => FocusNotesTextBox();
        }

        public static void ShowOrFocus()
        {
            if (instance == null)
            {
                instance = new QuickNotesWindow();
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

        private void FocusNotesTextBox()
        {
            NotesTextBox.Focus();
            Keyboard.Focus(NotesTextBox);
            NotesTextBox.CaretIndex = NotesTextBox.Text.Length;
        }

        protected override void OnClosed(System.EventArgs e)
        {
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

        private void NotesTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (isLoading) return;

            File.WriteAllText(NotesFilePath, NotesTextBox.Text);
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
                Close();
        }
    }
}
