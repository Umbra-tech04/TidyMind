using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TidyMind
{
    public partial class AddReminderWindow : Window
    {
        // Shown in the empty message box (in the language of the moment the window opened).
        private readonly string MessagePlaceholder = Loc.T("Reminder.Form.Placeholder");

        private readonly string title;
        private readonly Reminder editing; // null when adding a new one

        // A new reminder's Id, fixed for this window: saving again after a failure updates it rather than adding a
        // second one.
        private readonly Guid newId = Guid.NewGuid();

        // The message as the window opened with it, to tell whether closing would throw typed text away.
        private string openedMessage = "";

        private string TypedMessage => MessageInput.Text == MessagePlaceholder ? "" : MessageInput.Text;

        // Edit mode: the same form, filled in with an existing reminder; saving updates it (same Id) instead of adding.
        public AddReminderWindow(Reminder existing) : this(existing.Title)
        {
            editing = existing;

            Title = Loc.T("Reminder.Form.EditTitle");
            if (!string.IsNullOrWhiteSpace(existing.Title)) // a day plan block's reminder is named in the current language
                SubtitleText.Text = Loc.F("Reminder.Form.For", DayPlanReminders.ShownTitle(existing, DayPlanReminders.BlockIds()));
            HeadingText.Text = Loc.T("Reminder.Form.EditTitle");
            SaveButton.Content = Loc.T("Reminder.Form.SaveChanges");

            MessageInput.Text = existing.Message;
            MessageInput.Foreground = (Brush)FindResource("TextMain");

            ReminderDatePicker.SelectedDate = existing.NextFireTime.Date;
            HourBox.SelectedIndex = existing.NextFireTime.Hour;
            MinuteBox.SelectedIndex = existing.NextFireTime.Minute;
            // A repeat type this version doesn't know is treated as a one-off, as when it fires.
            RepeatBox.SelectedIndex = Enum.IsDefined(existing.RepeatType) ? (int)existing.RepeatType : (int)RepeatType.Once;
            openedMessage = TypedMessage;
        }

        // date: pre-selects that day (from the calendar's right-click "Add Reminder").
        public AddReminderWindow(string title = null, DateTime? date = null)
        {
            InitializeComponent();

            this.title = string.IsNullOrWhiteSpace(title) ? "" : title;

            SubtitleText.Text = string.IsNullOrWhiteSpace(title)
                ? Loc.T("Reminder.Form.General")
                : Loc.F("Reminder.Form.For", title);

            MessageInput.Text = MessagePlaceholder;
            MessageInput.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#8A8A87"));

            // A future day starts at 09:00; today keeps the "next minute" default.
            DateTime initial = ReminderForm.DefaultTime(date);
            ReminderDatePicker.SelectedDate = initial.Date;
            ReminderForm.FillTimeBoxes(HourBox, MinuteBox, initial);
            ReminderForm.FillRepeatBox(RepeatBox);

            // Esc closes like X (asking first if a message was typed).
            KeyDown += (s, e) =>
            {
                if (e.Key != Key.Escape) return;
                e.Handled = true;
                Close();
            };
        }

        // Closing without saving (X, Esc) asks first when the message was typed or changed; Save doesn't.
        protected override void OnClosing(CancelEventArgs e)
        {
            if (DialogResult != true && TypedMessage != openedMessage
                && Msg.Show(this, Loc.T("Common.DiscardChanges"), Loc.T("Common.UnsavedChanges"), MessageBoxButton.YesNo,
                    MessageBoxImage.Question) != MessageBoxResult.Yes)
                e.Cancel = true;

            base.OnClosing(e);
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
                Msg.Show(this, problem, problemTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            reminder.Id = editing?.Id ?? newId;
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
                Msg.Show(this, Loc.T("Reminder.SaveFailed"), Loc.T("Common.CouldntSave"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            DialogResult = true;
            Close();
        }
    }
}
