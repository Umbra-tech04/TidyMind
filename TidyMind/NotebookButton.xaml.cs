using System.Windows;
using System.Windows.Controls;

namespace TidyMind
{
    // The memory page's way into its notebook (project memories beside the to-do list, collections in the toolbar).
    public partial class NotebookButton : UserControl
    {
        public static readonly DependencyProperty RadiusProperty =
            DependencyProperty.Register(nameof(Radius), typeof(CornerRadius), typeof(NotebookButton), new PropertyMetadata(new CornerRadius(16)));

        // Round like the to-do panel by default; the collection toolbar's buttons are squarer.
        public CornerRadius Radius
        {
            get => (CornerRadius)GetValue(RadiusProperty);
            set => SetValue(RadiusProperty, value);
        }

        private Profile? memory;
        private int written;

        public NotebookButton()
        {
            InitializeComponent();
            Loc.Track(this, ShowCount);
        }

        public void Load(Profile notebookOf)
        {
            memory = notebookOf;
            Refresh();
        }

        // Reads the notebook again (after it was closed, or an undo): how many pages have writing on them.
        public void Refresh()
        {
            written = memory == null ? 0 : MemoryNotebookStore.WrittenPages(memory);
            ShowCount();
        }

        private void ShowCount()
        {
            CountText.Text = written.ToString(Loc.Culture);
            CountText.Visibility = written > 0 ? Visibility.Visible : Visibility.Collapsed;
            OpenButton.ToolTip = written > 0
                ? Loc.F("Notebook.TipPages", Loc.Plural("Notebook.Pages", written))
                : Loc.T("Notebook.Tip");
        }

        // The notebook over the page; its count again once it is closed (only a saved change can alter it).
        public void Open()
        {
            if (memory == null)
                return;
            NotebookWindow? window = NotebookWindow.Open(Window.GetWindow(this), memory);
            if (window?.Changed == true)
                Refresh();
        }

        private void OpenButton_Click(object sender, RoutedEventArgs e) => Open();
    }
}
