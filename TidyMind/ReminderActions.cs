using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;

namespace TidyMind
{
    // Editing and deleting reminders, shared by the Reminders window and the Home dashboard so both ask the
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

        // Several (a selection), asked once, each deleted the way one is (its entry and its scheduled task). One that
        // can't be is kept and named afterwards. `names`: what the list shows for each. True if any went.
        public static bool ConfirmDelete(Window owner, IReadOnlyList<Guid> ids, IReadOnlyList<string> names)
        {
            if (ids.Count == 1)
                return ConfirmDelete(ids[0]);
            if (!BulkDelete.Confirm(owner, "Bulk.Delete.Reminders", names))
                return false;

            List<string> kept = new List<string>();
            for (int i = 0; i < ids.Count; i++)
            {
                if (!TryDelete(ids[i]))
                    kept.Add(names[i]);
            }
            if (kept.Count > 0)
                BulkDelete.ReportKept(owner, kept, Loc.T("Reminder.DeleteManyFailed"));
            return kept.Count < ids.Count;
        }

        public static bool ConfirmDelete(Guid id)
        {
            MessageBoxResult result = Msg.Show(Loc.T("Reminder.DeleteConfirm"), Loc.T("Common.ConfirmDelete"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes)
                return false;

            if (TryDelete(id))
                return true;
            Msg.Show(Loc.T("Reminder.DeleteFailed"), Loc.T("Common.CouldntDelete"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        private static bool TryDelete(Guid id)
        {
            try
            {
                ReminderManager.DeleteReminder(id);
                return true;
            }
            catch (Exception e) when (e is IOException || e is JsonException || e is UnauthorizedAccessException
                                      || e is COMException)
            {
                return false;
            }
        }
    }
}
