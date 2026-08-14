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

        public MainWindow(string profileName)
        {
            InitializeComponent();

            this.profileName = profileName;
            this.Title = profileName + " - TidyMind";

            string fileName = profileName + ".json";

            if (File.Exists(fileName))
            {
                string json = File.ReadAllText(fileName);
                List<Project> projects = JsonSerializer.Deserialize<List<Project>>(json);
                foreach (Project project in projects)
                {
                    ProjectList.Items.Add(project);
                }
            }
        }

        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(ProjectInput.Text))
            {
                return;
            }

            Project newProject = new Project();
            newProject.Name = ProjectInput.Text;
            ProjectList.Items.Add(newProject);
            ProjectInput.Clear();

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
                    ProjectList.Items.Remove(ProjectList.SelectedItem);
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
                    ProjectList.Items.Refresh();
                    SaveProjects();
                }
            }
        }

        private void SwitchProfileButton_Click(object sender, RoutedEventArgs e)
        {
            ((App)Application.Current).SwitchProfile(this);
        }

        private void ProjectList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (ProjectList.SelectedItem != null)
            {
                Project selectedProject = (Project)ProjectList.SelectedItem;

                ProjectWindow projectWindow = new ProjectWindow(selectedProject);
                projectWindow.ShowDialog();

                ProjectList.Items.Refresh();
                SaveProjects();
            }
        }

        private void SaveProjects()
        {
            string fileName = profileName + ".json";
            var projects = ProjectList.Items.Cast<Project>().ToList();
            string json = JsonSerializer.Serialize(projects);
            File.WriteAllText(fileName, json);
        }
    }
}