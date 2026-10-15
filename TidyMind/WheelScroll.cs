using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace TidyMind
{
    // A short list that scrolls inside a page: it scrolls itself while it has rows to show in the wheel's direction,
    // and at either end the wheel moves on, so the list never swallows the page's scrolling.
    public static class WheelScroll
    {
        public static bool CanScroll(ScrollViewer list, int delta) =>
            delta < 0 ? list.VerticalOffset < list.ScrollableHeight - 0.5 : list.VerticalOffset > 0.5;

        // Ends the wheel here and raises it again on `target`, from where it bubbles up.
        public static void Forward(MouseWheelEventArgs e, UIElement target)
        {
            e.Handled = true;
            target.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = UIElement.MouseWheelEvent });
        }
    }
}
