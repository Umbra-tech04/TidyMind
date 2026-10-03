using System;
using System.Windows;

namespace TidyMind
{
    public partial class SettingsWindow : Window
    {
        private readonly Action onExportAll;

        public SettingsWindow(Action onExportAll)
        {
            InitializeComponent();
            this.onExportAll = onExportAll;
        }

        private void ExportAllButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
            onExportAll?.Invoke();
        }
    }
}
