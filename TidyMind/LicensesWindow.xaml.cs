using System.IO;
using System.Windows;

namespace TidyMind
{
    // Settings > Licenses: the third-party notices, read from the copy built into the app (THIRD-PARTY-NOTICES.txt).
    public partial class LicensesWindow : Window
    {
        public LicensesWindow()
        {
            InitializeComponent();
            using (Stream? notices = typeof(LicensesWindow).Assembly.GetManifestResourceStream("TidyMind.THIRD-PARTY-NOTICES.txt"))
            {
                if (notices != null)
                    using (StreamReader reader = new StreamReader(notices))
                        NoticesText.Text = reader.ReadToEnd().TrimEnd();
            }
        }
    }
}
