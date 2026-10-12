using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;

namespace TidyMind
{
    // WPF text boxes switch to overwrite ("overtype") mode when Insert is pressed, and show no sign of it: the next
    // character typed then replaces the one to the right of the caret. Easy to hit by accident (on many laptops
    // Insert sits next to Delete), so it's turned off for every TextBox and RichTextBox in the app, in one place.
    // Shift+Insert (paste) and Ctrl+Insert (copy) still work.
    public static class TextEditing
    {
        public static void DisableOvertype()
        {
            EventManager.RegisterClassHandler(typeof(TextBoxBase), UIElement.PreviewKeyDownEvent,
                new KeyEventHandler(BlockInsertKey));
            EventManager.RegisterClassHandler(typeof(TextBoxBase), CommandManager.PreviewCanExecuteEvent,
                new CanExecuteRoutedEventHandler(BlockToggleInsertCanExecute));
            EventManager.RegisterClassHandler(typeof(TextBoxBase), CommandManager.PreviewExecutedEvent,
                new ExecutedRoutedEventHandler(BlockToggleInsertExecuted));
        }

        // Insert on its own only; with Shift or Ctrl it's paste or copy.
        private static void BlockInsertKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Insert && Keyboard.Modifiers == ModifierKeys.None)
                e.Handled = true;
        }

        // The command itself too, however it's invoked.
        private static void BlockToggleInsertCanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            if (e.Command == EditingCommands.ToggleInsert)
            {
                e.CanExecute = false;
                e.Handled = true;
            }
        }

        private static void BlockToggleInsertExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            if (e.Command == EditingCommands.ToggleInsert)
                e.Handled = true;
        }

        // A list item's text edited in place, the way the project notes are: it looks like plain text, and a click
        // puts the caret where you clicked, so a wrong letter is fixed without retyping the rest. Enter or clicking
        // away saves, Esc puts the stored text back. An empty text is never saved.
        // stored: the item's current text. save: stores the trimmed new text; false if it couldn't (the caller has
        // said so), and the stored text then comes back. leave: where focus goes after Enter or Esc.
        public static TextBox InlineEditor(Func<string> stored, Func<string, bool> save, Action leave, bool editable = true)
        {
            TextBox box = new TextBox
            {
                Text = stored(),
                Style = (Style)Application.Current.FindResource("SelectableTextStyle"),
                IsReadOnly = !editable,
                IsTabStop = editable
            };
            box.ContextMenu = new ContextMenu
            {
                Items =
                {
                    new MenuItem { Command = ApplicationCommands.Cut, CommandTarget = box },
                    new MenuItem { Command = ApplicationCommands.Copy, CommandTarget = box },
                    new MenuItem { Command = ApplicationCommands.Paste, CommandTarget = box }
                }
            };

            box.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                    leave(); // leaving the field saves (below)
                }
                else if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    box.Text = stored();
                    leave();
                }
            };
            box.LostKeyboardFocus += (s, e) =>
            {
                string edited = box.Text.Trim();
                if (edited.Length > 0 && edited != stored())
                    save(edited);
                box.Text = stored(); // the saved text, or the old one if it was empty or couldn't be saved
            };
            return box;
        }
    }
}
