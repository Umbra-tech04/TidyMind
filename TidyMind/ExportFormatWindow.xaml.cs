using System.Windows;

namespace TidyMind
{
    public partial class ExportFormatWindow : Window
    {
        public ExportFormat SelectedFormat { get; private set; }

        public ExportFormatWindow()
        {
            InitializeComponent();
        }

        private void ExcelButton_Click(object sender, RoutedEventArgs e)
        {
            ChooseFormat(ExportFormat.Excel);
        }

        private void DocxButton_Click(object sender, RoutedEventArgs e)
        {
            ChooseFormat(ExportFormat.Docx);
        }

        private void PdfButton_Click(object sender, RoutedEventArgs e)
        {
            ChooseFormat(ExportFormat.Pdf);
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void ChooseFormat(ExportFormat format)
        {
            SelectedFormat = format;
            DialogResult = true;
            Close();
        }
    }
}
