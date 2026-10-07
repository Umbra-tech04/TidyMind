using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace TidyMind
{
    // Right-click menus for a day and its entries, shared by the Home dashboard's week and the calendar's month
    // list, so a reminder, a note or a day offers the same actions wherever it's shown. Edit and Delete go through
    // the same code as everywhere else (ReminderActions, DayEditDialog); whenever something changed, `changed`
    // runs so the host can redraw.
    public static class AgendaMenus
    {
        public static ContextMenu ForReminder(DependencyObject owner, Guid id, DateTime date, string copyText, Action changed)
        {
            return ForEntry(owner, date, copyText, changed,
                edit: () => ReminderActions.Edit(owner, id),
                delete: () => ReminderActions.ConfirmDelete(id));
        }

        public static ContextMenu ForNote(DependencyObject owner, DateTime date, string copyText, Action changed)
        {
            return ForEntry(owner, date, copyText, changed,
                edit: () => DayEditDialog.Edit(owner, date),
                delete: () => ConfirmDeleteNote(date));
        }

        // The day itself (its date, or anywhere that isn't one of its entries): add a reminder, copy the day.
        public static ContextMenu ForDay(DependencyObject owner, DateTime date, string copyText, Action changed)
        {
            ContextMenu menu = new ContextMenu();
            menu.Items.Add(AddReminderItem(owner, date, changed));
            if (copyText != null)
            {
                menu.Items.Add(new Separator());
                menu.Items.Add(TextCopy.CopyItem(() => copyText));
            }
            return menu;
        }

        // An entry's menu also offers Add Reminder, so a day full of entries still has it wherever you right-click.
        private static ContextMenu ForEntry(DependencyObject owner, DateTime date, string copyText, Action changed,
            Func<bool> edit, Func<bool> delete)
        {
            MenuItem editItem = new MenuItem { Header = "Edit" };
            editItem.Click += (s, e) =>
            {
                if (edit())
                    changed();
            };
            MenuItem deleteItem = new MenuItem { Header = "Delete" };
            deleteItem.Click += (s, e) =>
            {
                if (delete())
                    changed();
            };

            ContextMenu menu = new ContextMenu();
            menu.Items.Add(editItem);
            menu.Items.Add(deleteItem);
            menu.Items.Add(new Separator());
            menu.Items.Add(AddReminderItem(owner, date, changed));
            menu.Items.Add(new Separator());
            menu.Items.Add(TextCopy.CopyItem(() => copyText));
            return menu;
        }

        private static MenuItem AddReminderItem(DependencyObject owner, DateTime date, Action changed)
        {
            MenuItem item = new MenuItem
            {
                Header = "Add Reminder",
                IsEnabled = date.Date >= DateTime.Today // a reminder can't fire in the past
            };
            item.Click += (s, e) =>
            {
                if (ReminderActions.Add(owner, date))
                    changed();
            };
            return item;
        }

        // Removes the day's note (title and text); its color stays.
        private static bool ConfirmDeleteNote(DateTime date)
        {
            MessageBoxResult result = MessageBox.Show(
                "Delete the note for " + date.ToString("dddd, MMMM d", CultureInfo.InvariantCulture) + "? The day's color stays.",
                "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes)
                return false;

            if (CalendarService.DeleteNote(date))
                return true;

            MessageBox.Show("Couldn't delete the note: calendar.json can't be read or written.", "Couldn't Delete",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }
}
