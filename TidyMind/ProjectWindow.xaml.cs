using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace TidyMind
{
    /// <summary>
    /// Interaction logic for ProjectWindow.xaml
    /// </summary>
    public partial class ProjectWindow : Window
    {
        private Project currentProject;

        public ProjectWindow(Project project)
        {
            InitializeComponent();

            currentProject = project;

            NameBox.Text = currentProject.Name;
            DescriptionBox.Text = currentProject.Description;
            StatusBox.Text = currentProject.Status;

            if (currentProject.Tasks == null)
            {
                currentProject.Tasks = new List<TaskItem>();
            }

            foreach (TaskItem task in currentProject.Tasks)
            {
                TaskList.Items.Add(task);
            }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            currentProject.Name = NameBox.Text;
            currentProject.Description = DescriptionBox.Text;
            currentProject.Status = StatusBox.Text;

            this.Close();
        }

        private void DeleteTaskButton_Click(object sender, RoutedEventArgs e)
        {
            if (TaskList.SelectedItem != null)
            {
                TaskItem selectedTask = (TaskItem)TaskList.SelectedItem;

                currentProject.Tasks.Remove(selectedTask);
                TaskList.Items.Remove(selectedTask);
            }
        }

        private void AddTaskButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(TaskInput.Text))
            {
                return;
            }

            TaskItem newTask = new TaskItem();
            newTask.Title = TaskInput.Text;

            currentProject.Tasks.Add(newTask);
            TaskList.Items.Add(newTask);

            TaskInput.Clear();
        }
    }
}
