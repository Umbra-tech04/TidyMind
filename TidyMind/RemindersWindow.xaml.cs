using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TidyMind
{
    public partial class RemindersWindow : Window
    {
        private static RemindersWindow instance;

        public RemindersWindow()
        {
            InitializeComponent();
            RenderReminders();
            Activated += (s, e) => RenderReminders();
        }

        public static void ShowOrFocus()
        {
            if (instance == null)
            {
                instance = new RemindersWindow();
                instance.Show();
            }
            else
            {
                if (instance.WindowState == WindowState.Minimized)
                    instance.WindowState = WindowState.Normal;

                instance.Activate();
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            instance = null;
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
                Close();
        }

        private void RenderReminders()
        {
            RemindersPanel.Children.Clear();

            List<Reminder> reminders = ReminderManager.LoadReminders();
            reminders.Sort((a, b) => a.NextFireTime.CompareTo(b.NextFireTime));

            if (reminders.Count == 0)
            {
                TextBlock empty = new TextBlock();
                empty.Text = "No reminders yet.";
                empty.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#858585"));
                empty.HorizontalAlignment = HorizontalAlignment.Center;
                empty.Margin = new Thickness(0, 20, 0, 0);
                RemindersPanel.Children.Add(empty);
                return;
            }

            foreach (Reminder reminder in reminders)
            {
                RemindersPanel.Children.Add(CreateReminderRow(reminder));
            }
        }

        private Border CreateReminderRow(Reminder reminder)
        {
            Border row = new Border();
            row.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#252526"));
            row.CornerRadius = new CornerRadius(6);
            row.Padding = new Thickness(14, 10, 14, 10);
            row.Margin = new Thickness(0, 0, 0, 8);

            Grid grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel info = new StackPanel();

            TextBlock message = new TextBlock();
            message.Text = reminder.Message;
            message.Foreground = Brushes.White;
            message.FontSize = 14;
            message.FontWeight = FontWeights.Bold;
            message.TextWrapping = TextWrapping.Wrap;
            info.Children.Add(message);

            if (!string.IsNullOrWhiteSpace(reminder.Title))
            {
                TextBlock forText = new TextBlock();
                forText.Text = "For: " + reminder.Title;
                forText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#569CD6"));
                forText.FontSize = 11;
                forText.Margin = new Thickness(0, 4, 0, 0);
                info.Children.Add(forText);
            }

            TextBlock details = new TextBlock();
            details.Text = reminder.NextFireTime.ToString("yyyy-MM-dd HH:mm") + "   •   " + reminder.RepeatType;
            details.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#858585"));
            details.FontSize = 11;
            details.Margin = new Thickness(0, 4, 0, 0);
            info.Children.Add(details);

            Grid.SetColumn(info, 0);
            grid.Children.Add(info);

            Button deleteButton = new Button();
            deleteButton.Content = "Delete";
            deleteButton.Width = 80;
            deleteButton.Height = 30;
            deleteButton.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#8B1A1A"));
            deleteButton.VerticalAlignment = VerticalAlignment.Center;
            deleteButton.Tag = reminder.Id;
            deleteButton.Click += DeleteReminder_Click;
            Grid.SetColumn(deleteButton, 1);
            grid.Children.Add(deleteButton);

            row.Child = grid;
            return row;
        }

        private void DeleteReminder_Click(object sender, RoutedEventArgs e)
        {
            Guid id = (Guid)((Button)sender).Tag;

            MessageBoxResult result = MessageBox.Show(
                "Delete this reminder?",
                "Confirm Delete",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                ReminderManager.DeleteReminder(id);
                RenderReminders();
            }
        }

        private void AddReminderButton_Click(object sender, RoutedEventArgs e)
        {
            AddReminderWindow addReminderWindow = new AddReminderWindow();
            bool? result = addReminderWindow.ShowDialog();

            if (result == true)
                RenderReminders();
        }
    }
}
