using System.Windows;
using System.Windows.Controls;

namespace TidyMind
{
    public partial class EntityTypeSelector : Window
    {
        public string SelectedType { get; private set; }

        public EntityTypeSelector()
        {
            InitializeComponent();
        }

        private void TypeButton_Click(object sender, RoutedEventArgs e)
        {
            SelectedType = (string)((Button)sender).Tag;
            this.DialogResult = true;
            this.Close();
        }
    }
}
