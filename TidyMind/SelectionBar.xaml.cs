using System;
using System.Windows;
using System.Windows.Controls;

namespace TidyMind
{
    // "{N} selected  [Delete]  [Cancel]": shown by a card grid instead of its search box and Add buttons while
    // anything is selected.
    public partial class SelectionBar : UserControl
    {
        public SelectionBar()
        {
            InitializeComponent();
        }

        public event Action DeleteClicked;
        public event Action CancelClicked;

        public void Show(int count)
        {
            CountText.Text = count + " selected";
        }

        private void Delete_Click(object sender, RoutedEventArgs e) => DeleteClicked?.Invoke();

        private void Cancel_Click(object sender, RoutedEventArgs e) => CancelClicked?.Invoke();
    }
}
