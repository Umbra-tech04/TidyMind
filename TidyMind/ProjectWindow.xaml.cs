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
    public partial class ProjectWindow : Window
    {
        private Project currentProject;

        public ProjectWindow(Project project)
        {
            InitializeComponent();

            currentProject = project;

            NameBox.Text = currentProject.Name;
            DescriptionBox.Text = currentProject.Description;
            StatusBox.SelectedItem = currentProject.Status;

            if (currentProject.Tasks == null)
            {
                currentProject.Tasks = new List<TaskItem>();
            }

            foreach (TaskItem task in currentProject.Tasks)
            {
                TaskList.Items.Add(task);
            }

            UpdateNoteDisplay();
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            currentProject.Name = NameBox.Text;
            currentProject.Description = DescriptionBox.Text;
            currentProject.Status = (ProjectStatus)StatusBox.SelectedItem;

            this.Close();
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

        private void TaskList_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            var item = ItemsControl.ContainerFromElement(TaskList, e.OriginalSource as DependencyObject) as ListBoxItem;
            if (item == null) return;

            TaskItem task = (TaskItem)item.DataContext;

            ContextMenu menu = new ContextMenu();

            MenuItem renameItem = new MenuItem();
            renameItem.Header = "Rename";
            renameItem.Click += (s, args) =>
            {
                string newTitle = Microsoft.VisualBasic.Interaction.InputBox(
                    "New task name:", "Rename Task", task.Title);
                if (string.IsNullOrWhiteSpace(newTitle) || newTitle == task.Title) return;
                task.Title = newTitle;
                TaskList.Items.Refresh();
            };

            MenuItem deleteItem = new MenuItem();
            deleteItem.Header = "Delete";
            deleteItem.Click += (s, args) =>
            {
                currentProject.Tasks.Remove(task);
                TaskList.Items.Remove(task);
            };

            menu.Items.Add(renameItem);
            menu.Items.Add(deleteItem);
            menu.IsOpen = true;
        }

        private void NoteButton_Click(object sender, RoutedEventArgs e)
        {
            string note = Microsoft.VisualBasic.Interaction.InputBox(
                "Write your note:",
                "Note",
                currentProject.Notes ?? "");

            currentProject.Notes = note;
            UpdateNoteDisplay();
        }

        private void UpdateNoteDisplay()
        {
            if (string.IsNullOrWhiteSpace(currentProject.Notes))
            {
                NoteText.Text = "No notes";
                NoteText.Foreground = System.Windows.Media.Brushes.Gray;
                NoteButton.Content = "Add Note";
            }
            else
            {
                NoteText.Text = currentProject.Notes;
                NoteText.Foreground = System.Windows.Media.Brushes.Black;
                NoteButton.Content = "Edit Note";
            }
        }
    }
}