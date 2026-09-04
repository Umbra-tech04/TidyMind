using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TidyMind
{
    public partial class ProfileWindow : Window
    {
        private List<Profile> profiles;

        public string SelectedProfileName { get; private set; }
        public ProfileType SelectedProfileType { get; private set; }

        public ProfileWindow()
        {
            InitializeComponent();
            profiles = ProfileManager.LoadProfiles();
            RenderProfiles();
        }

        private void RenderProfiles()
        {
            ProjectPanel.Children.Clear();
            CollectionPanel.Children.Clear();

            foreach (Profile profile in profiles)
            {
                Button card = CreateCard(profile);

                if (profile.Type == ProfileType.Collection)
                    CollectionPanel.Children.Add(card);
                else
                    ProjectPanel.Children.Add(card);
            }
        }

        private Button CreateCard(Profile profile)
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

            ContextMenu menu = new ContextMenu();

            MenuItem renameItem = new MenuItem();
            renameItem.Header = "Rename";
            renameItem.Tag = profile.Name;
            renameItem.Click += RenameProfile_Click;

            MenuItem deleteItem = new MenuItem();
            deleteItem.Header = "Delete";
            deleteItem.Tag = profile.Name;
            deleteItem.Click += DeleteProfile_Click;

            menu.Items.Add(renameItem);
            menu.Items.Add(deleteItem);
            card.ContextMenu = menu;

            return card;
        }

        private void ProfileCard_Click(object sender, RoutedEventArgs e)
        {
            Button clicked = (Button)sender;
            SelectedProfileName = (string)clicked.Tag;
            Profile profile = profiles.FirstOrDefault(p => p.Name == SelectedProfileName);
            SelectedProfileType = profile.Type;
            this.DialogResult = true;
            this.Close();
        }

        private void RenameProfile_Click(object sender, RoutedEventArgs e)
        {
            string oldName = (string)((MenuItem)sender).Tag;
            Profile profile = profiles.FirstOrDefault(p => p.Name == oldName);
            if (profile == null) return;

            string newName = Microsoft.VisualBasic.Interaction.InputBox(
                "New name:", "Rename Memory", oldName);

            if (string.IsNullOrWhiteSpace(newName) || newName == oldName) return;

            string oldFile = oldName + ".json";
            string newFile = newName + ".json";
            if (File.Exists(oldFile))
                File.Move(oldFile, newFile);

            profile.Name = newName;
            ProfileManager.SaveProfiles(profiles);
            RenderProfiles();
        }

        private void DeleteProfile_Click(object sender, RoutedEventArgs e)
        {
            string name = (string)((MenuItem)sender).Tag;
            Profile profile = profiles.FirstOrDefault(p => p.Name == name);
            if (profile == null) return;

            var result = MessageBox.Show(
                $"Delete '{name}'? This will delete all its data.",
                "Confirm Delete",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                string filePath = name + ".json";
                if (File.Exists(filePath))
                    File.Delete(filePath);

                profiles.Remove(profile);
                ProfileManager.SaveProfiles(profiles);
                RenderProfiles();
            }
        }

        private void AddProjectButton_Click(object sender, RoutedEventArgs e)
        {
            AddProfile(ProfileType.Project);
        }

        private void AddCollectionButton_Click(object sender, RoutedEventArgs e)
        {
            AddProfile(ProfileType.Collection);
        }

        private void AddProfile(ProfileType type)
        {
            string name = Microsoft.VisualBasic.Interaction.InputBox(
                "Memory name:", "Add Memory", "");

            if (string.IsNullOrWhiteSpace(name)) return;

            string[] colors = {
                "#E74C3C", "#E67E22", "#F1C40F",
                "#2ECC71", "#3498DB", "#9B59B6"
            };

            string color = colors[profiles.Count % colors.Length];

            Profile newProfile = new Profile();
            newProfile.Name = name;
            newProfile.Color = color;
            newProfile.Type = type;

            profiles.Add(newProfile);
            ProfileManager.SaveProfiles(profiles);
            RenderProfiles();
        }
    }
}