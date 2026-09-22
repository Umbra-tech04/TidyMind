using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TidyMind
{
    public partial class AddReminderWindow : Window
    {
        private const string MessagePlaceholder = "What should I remind you about?";

        private readonly string title;

        public AddReminderWindow(string title = null)
        {
            InitializeComponent();

            this.title = string.IsNullOrWhiteSpace(title) ? "" : title;

            SubtitleText.Text = string.IsNullOrWhiteSpace(title)
                ? "General reminder (not attached to anything)"
                : "Reminder for: " + title;

            MessageInput.Text = MessagePlaceholder;
            MessageInput.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#858585"));

            ReminderDatePicker.SelectedDate = DateTime.Today;

            PopulateHourAndMinute();
        }

        private void PopulateHourAndMinute()
        {
            for (int hour = 0; hour <= 23; hour++)
                HourBox.Items.Add(new ComboBoxItem { Content = hour.ToString("00") });

            for (int minute = 0; minute <= 55; minute += 5)
                MinuteBox.Items.Add(new ComboBoxItem { Content = minute.ToString("00") });

            HourBox.SelectedIndex = DateTime.Now.Hour;
            MinuteBox.SelectedIndex = DateTime.Now.Minute / 5;
        }

        private void MessageInput_GotFocus(object sender, RoutedEventArgs e)
        {
            if (MessageInput.Text == MessagePlaceholder)
            {
                MessageInput.Text = "";
                MessageInput.Foreground = Brushes.White;
            }
        }

        private void MessageInput_LostFocus(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(MessageInput.Text))
            {
                MessageInput.Text = MessagePlaceholder;
                MessageInput.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#858585"));
            }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            string message = MessageInput.Text == MessagePlaceholder ? "" : MessageInput.Text.Trim();

            if (string.IsNullOrWhiteSpace(message))
            {
                MessageBox.Show("Please enter a reminder message.", "Missing Message",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (ReminderDatePicker.SelectedDate == null)
            {
                MessageBox.Show("Please pick a date.", "Missing Date",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (HourBox.SelectedItem == null || MinuteBox.SelectedItem == null)
            {
                MessageBox.Show("Please pick an hour and a minute.", "Missing Time",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            DateTime date = ReminderDatePicker.SelectedDate.Value;
            int hour = int.Parse((string)((ComboBoxItem)HourBox.SelectedItem).Content);
            int minute = int.Parse((string)((ComboBoxItem)MinuteBox.SelectedItem).Content);

            DateTime fireTime = new DateTime(date.Year, date.Month, date.Day, hour, minute, 0, DateTimeKind.Local);

            string repeatText = ((ComboBoxItem)RepeatBox.SelectedItem).Content.ToString();
            RepeatType repeatType = (RepeatType)Enum.Parse(typeof(RepeatType), repeatText);

            Reminder reminder = new Reminder();
            reminder.Id = Guid.NewGuid();
            reminder.Title = this.title;
            reminder.Message = message;
            reminder.NextFireTime = fireTime;
            reminder.RepeatType = repeatType;

            ReminderManager.AddReminder(reminder);

            DialogResult = true;
            Close();
        }
    }
}
