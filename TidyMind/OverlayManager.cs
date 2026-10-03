using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace TidyMind
{
    public enum Overlay { None, QuickNotes, Reminders, Search, Calendar }

    // The four floating overlays share one slot: opening one closes whichever other is open,
    // and pressing the open one's shortcut again closes it.
    public static class OverlayManager
    {
        private static readonly Overlay[] All = { Overlay.QuickNotes, Overlay.Reminders, Overlay.Search, Overlay.Calendar };

        private static MainWindow Main => (MainWindow)Application.Current.MainWindow;

        // Read from the overlays themselves, so one closed any other way (Esc, its X, a click outside) is always reflected.
        public static Overlay Current
        {
            get
            {
                if (QuickNotesWindow.IsOpen) return Overlay.QuickNotes;
                if (RemindersWindow.IsOpen) return Overlay.Reminders;
                if (Main.IsSearchOpen) return Overlay.Search;
                if (Main.IsCalendarOpen) return Overlay.Calendar;
                return Overlay.None;
            }
        }

        public static void Toggle(Overlay overlay)
        {
            if (Current == overlay)
                Close(overlay);
            else
                Open(overlay);
        }

        public static void Open(Overlay overlay)
        {
            foreach (Overlay other in All.Where(o => o != overlay))
                Close(other);

            // Quick Notes and Reminders are owned by the main window, so they would open hidden while it's minimized.
            if (Main.WindowState == WindowState.Minimized)
                Main.WindowState = WindowState.Normal;

            switch (overlay)
            {
                case Overlay.QuickNotes: QuickNotesWindow.ShowOrFocus(); break;
                case Overlay.Reminders: RemindersWindow.ShowOrFocus(); break;
                case Overlay.Search: Main.OpenSearch(); break;
                case Overlay.Calendar: Main.OpenCalendar(); break;
            }
        }

        // From the dashboard: open the overlay already pointed at one item.
        public static void OpenReminder(Guid id)
        {
            Open(Overlay.Reminders);
            RemindersWindow.Highlight(id);
        }

        public static void OpenCalendarAt(DateTime date)
        {
            Open(Overlay.Calendar);
            Main.ShowCalendarDate(date);
        }

        public static void Close(Overlay overlay)
        {
            switch (overlay)
            {
                case Overlay.QuickNotes: QuickNotesWindow.CloseIfOpen(); break;
                case Overlay.Reminders: RemindersWindow.CloseIfOpen(); break;
                case Overlay.Search: Main.CloseSearch(); break;
                case Overlay.Calendar: Main.CloseCalendar(); break;
            }
        }

        // Called for every key press in every window of the app (one class handler in App).
        public static bool TryHandleShortcut(KeyEventArgs e)
        {
            if (Keyboard.Modifiers != (ModifierKeys.Control | ModifierKeys.Shift))
                return false;

            Overlay overlay = OverlayFor(e.Key);
            if (overlay == Overlay.None)
                return false;

            // A held shortcut auto-repeats into the overlay it just opened; acting on each repeat would flicker it shut.
            return e.IsRepeat || Route(overlay);
        }

        // Shortcuts and the main window's overlay buttons both come through here: what happens depends on
        // which dialog (if any) is in the way. Returns whether it acted.
        public static bool Route(Overlay overlay)
        {
            List<Window> dialogs = OpenDialogs();

            if (dialogs.Count == 0)
            {
                // A message box or file dialog is up (it disables its owner but isn't a WPF dialog):
                // switching overlays now could close the window it belongs to.
                if (Application.Current.Windows.OfType<Window>().Any(w => w.IsVisible && IsDisabled(w)))
                    return false;

                Toggle(overlay);
                return true;
            }

            // An open project behaves like a view, not a form: every shortcut works from it.
            if (dialogs.Count == 1 && dialogs[0] is ProjectWindow project && !IsDisabled(project))
            {
                // Quick Notes and Reminders are their own windows and can float over the project.
                if (overlay == Overlay.QuickNotes || overlay == Overlay.Reminders)
                {
                    Toggle(overlay);
                    return true;
                }

                // Search and Calendar live inside the main window, which a modal project blocks:
                // close the project first (closing saves it), then open once its ShowDialog has returned.
                project.Close();
                Main.Dispatcher.BeginInvoke(new Action(() => Open(overlay)), DispatcherPriority.Background);
                return true;
            }

            // Any other dialog holds unsaved form input (and may belong to an overlay, e.g. Add Reminder over
            // Reminders), so switching overlays could throw that input away: finish or cancel it first.
            return false;
        }

        private static Overlay OverlayFor(Key key)
        {
            switch (key)
            {
                case Key.N: return Overlay.QuickNotes;
                case Key.R: return Overlay.Reminders;
                case Key.F: return Overlay.Search;
                case Key.C: return Overlay.Calendar;
                default: return Overlay.None;
            }
        }

        [DllImport("user32.dll")]
        private static extern bool IsWindowEnabled(IntPtr hWnd);

        // Win32 state: a message box disables its owner without WPF's IsEnabled knowing.
        private static bool IsDisabled(Window window) => !IsWindowEnabled(new WindowInteropHelper(window).Handle);

        // Everything except the main window and the two overlay windows is opened with ShowDialog.
        private static List<Window> OpenDialogs()
        {
            return Application.Current.Windows.OfType<Window>()
                .Where(w => w.IsVisible && !(w is MainWindow || w is QuickNotesWindow || w is RemindersWindow))
                .ToList();
        }
    }
}
