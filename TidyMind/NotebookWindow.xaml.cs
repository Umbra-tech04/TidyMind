using System.ComponentModel;
using System.Windows;
using System.Windows.Input;

namespace TidyMind
{
    // A memory's notebook (MemoryNotebookStore), over the memory's page until closed. Like Project Details, Ctrl+Z here
    // takes back what was done in the window, every way of closing saves, and what changed is one undo step of the page.
    public partial class NotebookWindow : Window
    {
        private readonly Profile memory;
        private readonly MemoryNotebook notebook;
        private readonly UndoHistory local = new UndoHistory("notebook window");

        // True once the notebook was saved with a change (the caller then shows its page count again).
        public bool Changed { get; private set; }

        // notebook: as read (MemoryNotebookStore.TryLoad). Open is the way in; the harness opens one without waiting.
        internal NotebookWindow(Profile memory, MemoryNotebook notebook)
        {
            InitializeComponent();
            this.memory = memory;
            this.notebook = notebook;

            // The memory's name is its own; the window's title around it follows the language.
            TitleText.Text = memory.Name;
            Title = Loc.F("Notebook.WindowTitle", memory.Name);
            Loc.Track(this, () => Title = Loc.F("Notebook.WindowTitle", memory.Name));
            Notebook.Label("Notebook.Title");
            Notebook.Load(this, memory.Id, notebook.PagesToShow(), local);

            UndoHost.Attach(this, () => new[] { local }, bottom: 80); // above the footer with Save
            Closed += (s, e) => local.End();
        }

        // Opens the memory's notebook until it is closed. A notebook file that can't be read is said, and nothing
        // opens (it is never saved over). Returns the window once closed, or null if it didn't open.
        public static NotebookWindow? Open(Window owner, Profile memory)
        {
            if (!MemoryNotebookStore.TryLoad(memory, out MemoryNotebook notebook, out string problem))
            {
                Msg.Show(owner, Loc.F("Notebook.Unreadable", MemoryFiles.Notebook(memory), problem), Loc.T("Notebook.Title"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }
            NotebookWindow window = new NotebookWindow(memory, notebook) { Owner = owner };
            window.ShowDialog();
            return window;
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e) => Close();

        // Esc closes like Save and the X; an open page list closes first.
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Key != Key.Escape || e.Handled) return;
            e.Handled = true;
            if (!Notebook.CloseList())
                Close();
        }

        // Only a real change is written (an untouched notebook leaves its file, or its absence, as it was). If it can't
        // be written, the file stays as it was and the window stays open unless the changes are given up.
        protected override void OnClosing(CancelEventArgs e)
        {
            base.OnClosing(e);
            if (e.Cancel || !(Notebook.Edited() is List<NotePage> pages))
                return;

            List<NotePage> before = notebook.Pages;
            notebook.Pages = pages;
            using (UndoChange change = UndoChange.Begin(UndoService.Page, UndoParts.Notebook(memory)))
            {
                if (MemoryNotebookStore.TrySave(memory, notebook))
                {
                    Changed = true;
                    change.Commit(Loc.F("Undo.NotebookChanged", memory.Name));
                    return;
                }
            }
            notebook.Pages = before;
            if (Msg.Show(this, Loc.F("Notebook.SaveFailed", MemoryFiles.Notebook(memory)), Loc.T("Notebook.Title"),
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                e.Cancel = true;
        }
    }
}
