using System.Windows;
using System.Windows.Controls;

namespace TidyMind
{
    // Which kind of item to add: Clothing, Vehicle or Custom (the collection's own layout, CustomLayout). Custom is only
    // there for a collection that has no items yet, so nothing in it is ever turned into something else.
    public partial class EntityTypeSelector : Window
    {
        public string SelectedType { get; private set; }

        public EntityTypeSelector(bool customAllowed = true)
        {
            InitializeComponent();
            CustomButton.Tag = "Custom"; // the stored name CollectionView acts on
            if (!customAllowed)
            {
                CustomButton.IsEnabled = false;
                CustomText.Text = Loc.T("Entity.Type.Custom.NeedsEmpty");
            }
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
