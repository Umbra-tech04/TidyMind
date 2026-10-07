using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;

namespace TidyMind
{
    // Editing and deleting one reminder, shared by the Reminders window and the Home dashboard so both ask the
    // same question and do the same thing. Each returns whether something changed, so the caller can redraw.
    public static class ReminderActions
    {
        // A new general reminder (not attached to a project or item) with that day already picked: a later day
        // starts at 09:00, today at the next minute. Used by the calendar's and the dashboard's day menus.
        public static bool Add(DependencyObject owner, DateTime date)
        {
            return ShowOwned(owner, new AddReminderWindow(null, date));
        }

        // Opens the reminder form filled in with the stored reminder (read fresh by Id, not from a stale copy).
        public static bool Edit(DependencyObject owner, Guid id)
        {
            Reminder reminder = ReminderManager.TryLoadReminders().Find(r => r.Id == id);
            if (reminder == null)
                return false;

            return ShowOwned(owner, new AddReminderWindow(reminder));
        }

        private static bool ShowOwned(DependencyObject owner, Window window)
        {
            Window ownerWindow = owner as Window ?? Window.GetWindow(owner);
            if (ownerWindow != null)
                window.Owner = ownerWindow;
            return window.ShowDialog() == true;
        }

        public static bool ConfirmDelete(Guid id)
        {
            MessageBoxResult result = MessageBox.Show(
                "Delete this reminder?",
                "Confirm Delete",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes)
                return false;

            try
            {
                ReminderManager.DeleteReminder(id);
                return true;
            }
            catch (Exception e) when (e is IOException || e is JsonException || e is UnauthorizedAccessException
                                      || e is COMException)
            {
                MessageBox.Show("Couldn't delete the reminder: reminders.json can't be read or written, or Windows "
                    + "Task Scheduler didn't accept the change.", "Couldn't Delete",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
        }
    }
}
