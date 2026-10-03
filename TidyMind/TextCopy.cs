using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace TidyMind
{
    // Getting text out of the app: selectable read-only text where nothing is clickable, and a right-click
    // "Copy" on clickable cards and rows (where selecting with the mouse would fight the click).
    public static class TextCopy
    {
        // Looks like a TextBlock, but can be selected with the mouse and copied with Ctrl+C.
        public static TextBox Selectable(string text, double fontSize, Brush foreground, FontWeight? weight = null)
        {
            return new TextBox
            {
                Text = text,
                FontSize = fontSize,
                Foreground = foreground,
                FontWeight = weight ?? FontWeights.Normal,
                Style = (Style)Application.Current.FindResource("SelectableTextStyle")
            };
        }

        // For a clickable area holding selectable text: a plain click (press + release) runs onClick, but a press
        // during which text got selected — a drag-select — doesn't. Judged per press, so a selection left over
        // from before never blocks a later click. Preview events, because the TextBoxes inside handle the
        // ordinary mouse events themselves.
        public static void AttachClick(FrameworkElement area, Action onClick)
        {
            bool pressed = false;
            bool selected = false;

            area.PreviewMouseLeftButtonDown += (s, e) =>
            {
                pressed = true;
                selected = false;
            };
            area.AddHandler(TextBoxBase.SelectionChangedEvent, new RoutedEventHandler((s, e) =>
            {
                if (pressed && e.OriginalSource is TextBox box && box.SelectionLength > 0)
                    selected = true;
            }));
            area.MouseLeave += (s, e) => pressed = false;
            area.PreviewMouseLeftButtonUp += (s, e) =>
            {
                if (!pressed) return;
                pressed = false;
                if (!selected)
                    onClick();
            };
        }

        public static MenuItem CopyItem(Func<string> text, string header = "Copy")
        {
            MenuItem item = new MenuItem { Header = header };
            item.Click += (s, e) => Copy(text());
            return item;
        }

        // For a card or row that has no menu of its own yet.
        public static void AttachMenu(FrameworkElement element, Func<string> text)
        {
            ContextMenu menu = new ContextMenu();
            menu.Items.Add(CopyItem(text));
            element.ContextMenu = menu;
        }

        public static void Copy(string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            try
            {
                Clipboard.SetText(text);
            }
            catch (ExternalException)
            {
                // Another program is holding the clipboard open; the copy simply didn't happen.
            }
        }
    }
}
