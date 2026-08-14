using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TidyMind
{
    public partial class ProfileWindow : Window
    {
        private List<Profile> profiles;

        public string SelectedProfileName { get; private set; }

        public ProfileWindow()
        {
            InitializeComponent();
            profiles = ProfileManager.LoadProfiles();
            RenderProfiles();
        }

        private void RenderProfiles()
        {
            ProfilePanel.Children.Clear();

            foreach (Profile profile in profiles)
            {
                Button card = new Button();
                card.Width = 120;
                card.Height = 120;
                card.Margin = new Thickness(10);
                card.Background = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString(profile.Color));
                card.Tag = profile.Name;
                card.Click += ProfileCard_Click;

                TextBlock name = new TextBlock();
                name.Text = profile.Name;
                name.Foreground = Brushes.White;
                name.FontSize = 14;
                name.FontWeight = FontWeights.Bold;
                name.HorizontalAlignment = HorizontalAlignment.Center;
                name.VerticalAlignment = VerticalAlignment.Center;
                name.TextWrapping = TextWrapping.Wrap;
                name.TextAlignment = TextAlignment.Center;

                card.Content = name;
                ProfilePanel.Children.Add(card);
            }
        }

        private void ProfileCard_Click(object sender, RoutedEventArgs e)
        {
            Button clicked = (Button)sender;
            SelectedProfileName = (string)clicked.Tag;
            this.DialogResult = true;
            this.Close();
        }

        private void AddProfileButton_Click(object sender, RoutedEventArgs e)
        {
            string name = Microsoft.VisualBasic.Interaction.InputBox(
                "Profile name:",
                "Add Profile",
                "");

            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            string[] colors = {
                "#E74C3C", "#E67E22", "#F1C40F",
                "#2ECC71", "#3498DB", "#9B59B6"
            };

            string color = colors[profiles.Count % colors.Count()];

            Profile newProfile = new Profile();
            newProfile.Name = name;
            newProfile.Color = color;

            profiles.Add(newProfile);
            ProfileManager.SaveProfiles(profiles);
            RenderProfiles();
        }
    }
}