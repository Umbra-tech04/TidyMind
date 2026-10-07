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

        private Guid? highlightedId;
        private Border highlightedRow;

        private RemindersWindow()
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

        // Marks one reminder (opened from the dashboard) until the window closes; kept across re-renders.
        public static void Highlight(Guid id)
        {
            if (instance == null) return;

            instance.highlightedId = id;
            instance.RenderReminders();

            // Scrolled to once, here: re-renders on every activation shouldn't keep yanking the list back.
            Border row = instance.highlightedRow;
            if (row != null)
                instance.Dispatcher.BeginInvoke(new Action(row.BringIntoView), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        public static bool IsOpen => instance != null;

        public static void CloseIfOpen() => instance?.Close();

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
            highlightedRow = null;

            List<Reminder> reminders = ReminderManager.LoadReminders();
            reminders.Sort((a, b) => a.NextFireTime.CompareTo(b.NextFireTime));

            if (reminders.Count == 0)
            {
                TextBlock empty = new TextBlock();
                empty.Text = "No reminders yet.";
                empty.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#8A8A87"));
                empty.HorizontalAlignment = HorizontalAlignment.Center;
                empty.Margin = new Thickness(0, 20, 0, 0);
                RemindersPanel.Children.Add(empty);
                return;
            }

            foreach (Reminder reminder in reminders)
            {
                Border row = CreateReminderRow(reminder);
                RemindersPanel.Children.Add(row);

                if (reminder.Id == highlightedId)
                {
                    row.Background = (Brush)FindResource("AccentSoft");
                    row.BorderBrush = (Brush)FindResource("Accent");
                    highlightedRow = row;
                }
            }
        }

        private Border CreateReminderRow(Reminder reminder)
        {
            Border row = new Border();
            row.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFFFFF"));
            row.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E0E0DE"));
            row.BorderThickness = new Thickness(1);
            row.CornerRadius = new CornerRadius(6);
            row.Padding = new Thickness(14, 10, 14, 10);
            row.Margin = new Thickness(0, 0, 0, 8);

            Grid grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel info = new StackPanel();

            info.Children.Add(TextCopy.Selectable(reminder.Message, 14, (Brush)FindResource("TextMain"), FontWeights.Bold));

            if (!string.IsNullOrWhiteSpace(reminder.Title))
            {
                TextBox forText = TextCopy.Selectable("For: " + reminder.Title, 11, (Brush)FindResource("Accent"));
                forText.Margin = new Thickness(0, 4, 0, 0);
                info.Children.Add(forText);
            }

            TextBox details = TextCopy.Selectable(reminder.NextFireTime.ToString("yyyy-MM-dd HH:mm") + "   •   " + reminder.RepeatType,
                11, (Brush)FindResource("TextSub"));
            details.Margin = new Thickness(0, 4, 0, 0);
            info.Children.Add(details);

            Grid.SetColumn(info, 0);
            grid.Children.Add(info);

            Button deleteButton = new Button();
            deleteButton.Content = "Delete";
            deleteButton.Width = 80;
            deleteButton.Height = 30;
            deleteButton.Style = (Style)Application.Current.FindResource("GhostButtonStyle");
            deleteButton.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#C0392B"));
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
            if (ReminderActions.ConfirmDelete((Guid)((Button)sender).Tag))
                RenderReminders();
        }

        private void AddReminderButton_Click(object sender, RoutedEventArgs e)
        {
            // Activated re-renders the list once the dialog hands focus back.
            new AddReminderWindow { Owner = this }.ShowDialog();
        }
    }
}
