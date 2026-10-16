using System;
using System.Windows;
using System.Windows.Controls;

namespace TidyMind
{
    // "{N} selected  [Delete]  [Cancel]": shown by a card grid instead of its search box and Add buttons while
    // anything is selected.
    public partial class SelectionBar : UserControl
    {
        private int shown;

        public SelectionBar()
        {
            InitializeComponent();
            Loc.Track(this, () => Show(shown));
        }

        public event Action DeleteClicked;
        public event Action CancelClicked;

        public void Show(int count)
        {
            shown = count;
            CountText.Text = Loc.F("Selection.Count", count);
        }

        private void Delete_Click(object sender, RoutedEventArgs e) => DeleteClicked?.Invoke();

        private void Cancel_Click(object sender, RoutedEventArgs e) => CancelClicked?.Invoke();
    }
}
