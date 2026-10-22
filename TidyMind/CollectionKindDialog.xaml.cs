using System.Windows;
using System.Windows.Input;

namespace TidyMind
{
    // What a new collection holds: the ready-made item types (clothing, vehicles), or items laid out the user's own way
    // from blocks (a custom collection, CustomLayout). Esc or X: no collection.
    public partial class CollectionKindDialog : Window
    {
        public bool Custom { get; private set; }

        public CollectionKindDialog()
        {
            InitializeComponent();
            Loaded += (s, e) => StandardButton.Focus();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Key != Key.Escape || e.Handled) return;
            e.Handled = true;
            Close();
        }

        private void Standard_Click(object sender, RoutedEventArgs e) => Choose(false);

        private void Custom_Click(object sender, RoutedEventArgs e) => Choose(true);

        private void Choose(bool custom)
        {
            Custom = custom;
            DialogResult = true;
        }
    }
}
