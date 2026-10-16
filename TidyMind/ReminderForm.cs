using System;
using System.Windows.Controls;

namespace TidyMind
{
    // The reminder form's rules, shared by AddReminderWindow and the calendar's day dialog: what the time and
    // repeat boxes offer, and what makes a reminder valid. Each window keeps its own layout.
    public static class ReminderForm
    {
        public static void FillTimeBoxes(ComboBox hourBox, ComboBox minuteBox, DateTime initial)
        {
            hourBox.Items.Clear();
            minuteBox.Items.Clear();
            for (int hour = 0; hour <= 23; hour++)
                hourBox.Items.Add(new ComboBoxItem { Content = hour.ToString("00") });
            for (int minute = 0; minute <= 59; minute++)
                minuteBox.Items.Add(new ComboBoxItem { Content = minute.ToString("00") });

            hourBox.SelectedIndex = initial.Hour;
            minuteBox.SelectedIndex = initial.Minute;
        }

        // Items in RepeatType order, so SelectedIndex is the RepeatType.
        public static void FillRepeatBox(ComboBox repeatBox, RepeatType initial = RepeatType.Once)
        {
            repeatBox.Items.Clear();
            foreach (RepeatType type in Enum.GetValues(typeof(RepeatType)))
                repeatBox.Items.Add(new ComboBoxItem { Content = RepeatLabel(type) });
            repeatBox.SelectedIndex = (int)initial;
        }

        // How a repeat is called on screen ("Once", "Daily"…). The stored value is the RepeatType's number.
        public static string RepeatLabel(RepeatType type) =>
            Enum.IsDefined(type) ? Loc.T("Repeat." + type) : type.ToString();

        // Where a new reminder's time starts: a later day at 09:00, today the next minute (so saving without
        // changes still lands in the future).
        public static DateTime DefaultTime(DateTime? date)
        {
            if (date.HasValue && date.Value.Date > DateTime.Today)
                return date.Value.Date.AddHours(9);
            return DateTime.Now.AddMinutes(1);
        }

        // The reminder the form describes (Id and Title left for the caller), or null with what's wrong.
        public static Reminder TryCreate(string message, DateTime? date, ComboBox hourBox, ComboBox minuteBox,
            ComboBox repeatBox, out string problemTitle, out string problem)
        {
            problemTitle = problem = null;

            if (string.IsNullOrWhiteSpace(message))
            {
                problemTitle = Loc.T("Reminder.Problem.MessageTitle");
                problem = Loc.T("Reminder.Problem.Message");
                return null;
            }
            if (date == null)
            {
                problemTitle = Loc.T("Reminder.Problem.DateTitle");
                problem = Loc.T("Reminder.Problem.Date");
                return null;
            }
            if (hourBox.SelectedIndex < 0 || minuteBox.SelectedIndex < 0)
            {
                problemTitle = Loc.T("Reminder.Problem.TimeTitle");
                problem = Loc.T("Reminder.Problem.Time");
                return null;
            }

            DateTime d = date.Value;
            DateTime fireTime = new DateTime(d.Year, d.Month, d.Day, hourBox.SelectedIndex, minuteBox.SelectedIndex, 0, DateTimeKind.Local);
            if (fireTime <= DateTime.Now)
            {
                problemTitle = Loc.T("Reminder.Problem.PastTitle");
                problem = Loc.T("Reminder.Problem.Past");
                return null;
            }

            return new Reminder
            {
                Message = message.Trim(),
                NextFireTime = fireTime,
                RepeatType = repeatBox.SelectedIndex < 0 ? RepeatType.Once : (RepeatType)repeatBox.SelectedIndex
            };
        }
    }
}
