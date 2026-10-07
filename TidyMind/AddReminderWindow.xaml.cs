using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TidyMind
{
    public partial class AddReminderWindow : Window
    {
        private const string MessagePlaceholder = "What should I remind you about?";

        private readonly string title;
        private readonly Reminder editing; // null when adding a new one

        // Edit mode: the same form, filled in with an existing reminder; saving updates it (same Id) instead of adding.
        public AddReminderWindow(Reminder existing) : this(existing.Title)
        {
            editing = existing;

            Title = "Edit Reminder";
            HeadingText.Text = "Edit Reminder";
            SaveButton.Content = "Save Changes";

            MessageInput.Text = existing.Message;
            MessageInput.Foreground = (Brush)FindResource("TextMain");

            ReminderDatePicker.SelectedDate = existing.NextFireTime.Date;
            HourBox.SelectedIndex = existing.NextFireTime.Hour;
            MinuteBox.SelectedIndex = existing.NextFireTime.Minute;
            RepeatBox.SelectedIndex = (int)existing.RepeatType;
        }

        // date: pre-selects that day (from the calendar's right-click "Add Reminder").
        public AddReminderWindow(string title = null, DateTime? date = null)
        {
            InitializeComponent();

            this.title = string.IsNullOrWhiteSpace(title) ? "" : title;

            SubtitleText.Text = string.IsNullOrWhiteSpace(title)
                ? "General reminder (not attached to anything)"
                : "Reminder for: " + title;

            MessageInput.Text = MessagePlaceholder;
            MessageInput.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#8A8A87"));

            // A future day starts at 09:00; today keeps the "next minute" default.
            DateTime initial = ReminderForm.DefaultTime(date);
            ReminderDatePicker.SelectedDate = initial.Date;
            ReminderForm.FillTimeBoxes(HourBox, MinuteBox, initial);
            ReminderForm.FillRepeatBox(RepeatBox);
        }

        private void MessageInput_GotFocus(object sender, RoutedEventArgs e)
        {
            if (MessageInput.Text == MessagePlaceholder)
            {
                MessageInput.Text = "";
                MessageInput.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1C1B19"));
            }
        }

        private void MessageInput_LostFocus(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(MessageInput.Text))
            {
                MessageInput.Text = MessagePlaceholder;
                MessageInput.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#8A8A87"));
            }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            string message = MessageInput.Text == MessagePlaceholder ? "" : MessageInput.Text;

            Reminder reminder = ReminderForm.TryCreate(message, ReminderDatePicker.SelectedDate, HourBox, MinuteBox, RepeatBox,
                out string problemTitle, out string problem);
            if (reminder == null)
            {
                MessageBox.Show(problem, problemTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            reminder.Id = editing?.Id ?? Guid.NewGuid();
            reminder.Title = this.title;

            // The window stays open on failure, so nothing typed is lost and saving can be tried again.
            try
            {
                if (editing != null)
                    ReminderManager.UpdateReminder(reminder);
                else
                    ReminderManager.AddReminder(reminder);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException
                                       || ex is COMException)
            {
                MessageBox.Show("Couldn't save the reminder: reminders.json can't be read or written, or Windows "
                    + "Task Scheduler didn't accept it.", "Couldn't Save", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            DialogResult = true;
            Close();
        }
    }
}
