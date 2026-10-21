using System.Windows;

namespace TidyMind
{
    // The "What's new" note (WhatsNew): its points, in the current language.
    public partial class WhatsNewWindow : Window
    {
        public WhatsNewWindow()
        {
            InitializeComponent();
            Fill();
            Loc.Track(this, Fill);
        }

        private void Fill()
        {
            Title = TitleText.Text = Loc.F("WhatsNew.Title", WhatsNew.NotesVersion);
            PointList.ItemsSource = WhatsNew.Points;
        }

        private void OkButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}
