using System.Windows;
using System.Windows.Controls;

namespace TidyMind
{
    // Which kind of item to add: Clothing, Vehicle or Custom (the collection's own layout, CustomLayout). Always the same
    // choices, whatever the collection holds: a collection may mix the kinds, each item keeping its own.
    public partial class EntityTypeSelector : Window
    {
        public string SelectedType { get; private set; }

        public EntityTypeSelector()
        {
            InitializeComponent();
            CustomButton.Tag = "Custom"; // the stored name CollectionView acts on
        }

        // Esc closes, as in the other dialogs (nothing typed here to lose).
        protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Key != System.Windows.Input.Key.Escape || e.Handled) return;
            e.Handled = true;
            Close();
        }

        private void TypeButton_Click(object sender, RoutedEventArgs e)
        {
            SelectedType = (string)((Button)sender).Tag;
            this.DialogResult = true;
            this.Close();
        }
    }
}
