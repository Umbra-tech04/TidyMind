using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.IO;
using System.Text.Json;

namespace TidyMind
{
    public partial class MainWindow : Window
    {
        private string profileName;
        private List<Project> allProjects;

        public MainWindow(string profileName)
        {
            InitializeComponent();

            this.profileName = profileName;
            this.Title = profileName + " - TidyMind";
            ProfileTitle.Text = profileName;

            string fileName = profileName + ".json";

            if (File.Exists(fileName))
            {
                string json = File.ReadAllText(fileName);
                allProjects = JsonSerializer.Deserialize<List<Project>>(json);
            }
            else
            {
                allProjects = new List<Project>();
            }

            RenderProjectList();

            this.Activated += MainWindow_Activated;
            this.Deactivated += MainWindow_Deactivated;
        }

        private void RenderProjectList()
        {
            ProjectList.Items.Clear();

            string filter = SearchBox.Text == "Search..." ? "" : SearchBox.Text.Trim();

            foreach (Project project in allProjects)
            {
                if (!string.IsNullOrEmpty(filter) &&
                    project.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                ProjectList.Items.Add(project);
            }
        }

        private void SearchBox_GotFocus(object sender, RoutedEventArgs e)
        {
            if (SearchBox.Text == "Search...")
            {
                SearchBox.Text = "";
                SearchBox.Foreground = Brushes.White;
            }
        }

        private void SearchBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(SearchBox.Text))
            {
                SearchBox.Text = "Search...";
                SearchBox.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#858585"));
            }
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (SearchBox.Text == "Search...") return;
            RenderProjectList();
        }

        private void MainWindow_Activated(object sender, EventArgs e)
        {
            QuickNotesButton.Visibility = Visibility.Visible;
        }

        private void MainWindow_Deactivated(object sender, EventArgs e)
        {
            QuickNotesButton.Visibility = Visibility.Collapsed;
        }

        private void ProjectInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
                AddButton_Click(sender, e);
        }

        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(ProjectInput.Text))
                return;

            Project newProject = new Project();
            newProject.Name = ProjectInput.Text;
            allProjects.Add(newProject);
            ProjectInput.Clear();
            RenderProjectList();
            SaveProjects();
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            if (ProjectList.SelectedItem != null)
            {
                MessageBoxResult result = MessageBox.Show(
                    "Are you sure you want to delete this project?",
                    "Confirm Delete",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    allProjects.Remove((Project)ProjectList.SelectedItem);
                    RenderProjectList();
                    SaveProjects();
                }
            }
        }

        private void EditButton_Click(object sender, RoutedEventArgs e)
        {
            if (ProjectList.SelectedItem != null)
            {
                Project selectedProject = (Project)ProjectList.SelectedItem;

                string newName = Microsoft.VisualBasic.Interaction.InputBox(
                    "Enter the new project name:",
                    "Edit Project",
                    selectedProject.Name);

                if (newName != "")
                {
                    selectedProject.Name = newName;
                    RenderProjectList();
                    SaveProjects();
                }
            }
        }

        private void SwitchProfileButton_Click(object sender, RoutedEventArgs e)
        {
            ((App)Application.Current).SwitchProfile(this);
        }

        private void RemindersButton_Click(object sender, RoutedEventArgs e)
        {
            RemindersWindow.ShowOrFocus();
        }

        private void QuickNotesButton_Click(object sender, RoutedEventArgs e)
        {
            QuickNotesWindow.ShowOrFocus();
        }

        private void ProjectList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (ProjectList.SelectedItem != null)
            {
                Project selectedProject = (Project)ProjectList.SelectedItem;

                ProjectWindow projectWindow = new ProjectWindow(selectedProject);
                projectWindow.ShowDialog();

                RenderProjectList();
                SaveProjects();
            }
        }

        private void ProjectList_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            var item = ItemsControl.ContainerFromElement(ProjectList, e.OriginalSource as DependencyObject) as ListBoxItem;
            if (item == null) return;

            ProjectList.SelectedItem = item.DataContext;

            ContextMenu menu = new ContextMenu();

            MenuItem renameItem = new MenuItem();
            renameItem.Header = "Rename";
            renameItem.Click += (s, args) => EditButton_Click(s, args);

            MenuItem deleteItem = new MenuItem();
            deleteItem.Header = "Delete";
            deleteItem.Click += (s, args) => DeleteButton_Click(s, args);

            MenuItem reminderItem = new MenuItem();
            reminderItem.Header = "Add Reminder";
            reminderItem.Click += (s, args) =>
            {
                Project selectedProject = (Project)item.DataContext;
                AddReminderWindow reminderWindow = new AddReminderWindow(selectedProject.Name);
                reminderWindow.ShowDialog();
            };

            menu.Items.Add(renameItem);
            menu.Items.Add(deleteItem);
            menu.Items.Add(reminderItem);
            menu.IsOpen = true;
        }

        private void SaveProjects()
        {
            string fileName = profileName + ".json";
            string json = JsonSerializer.Serialize(allProjects);
            File.WriteAllText(fileName, json);
        }
    }
}