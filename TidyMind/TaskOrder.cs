using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TidyMind
{
    // The order every checkbox task list shows (a project's tasks, a memory's to-dos, Home's to-dos) and every export
    // lists: the open ones above the done ones, each group in its own order. Only the display is sorted. A task keeps its
    // place in what is stored (Project.Tasks, todos.json, quicktodos.json), so ticking one never rewrites an order the
    // user made, unticking it puts it back among the open ones exactly where it was, and another version of the app reads
    // the same files as before.
    internal static class TaskOrder
    {
        // Stable: the open tasks in their stored order, then the done ones in theirs.
        public static List<T> OpenFirst<T>(IEnumerable<T> tasks, Func<T, bool> isDone) => tasks.OrderBy(isDone).ToList();

        // Runs `move` (a task changing places and the keyboard going back to its box) with the view left where the user
        // had it: the list is laid out inside the guard, nothing scrolls to the moved task, and if the scroll position
        // was moved anyway (a list item bringing itself into view as it is laid out) it is put back.
        // `scope` is the list (or a parent of it), inside the scrolling area.
        public static void KeepView(FrameworkElement scope, Action move)
        {
            ScrollViewer? scroller = null;
            for (DependencyObject? up = scope; up != null && scroller == null; up = VisualTreeHelper.GetParent(up))
                scroller = up as ScrollViewer;
            double? offset = scroller?.VerticalOffset;
            void Restore()
            {
                if (scroller != null && offset is double wanted && Math.Abs(scroller.VerticalOffset - wanted) > 0.5)
                    scroller.ScrollToVerticalOffset(wanted);
            }

            RequestBringIntoViewEventHandler keep = (s, e) => e.Handled = true;
            scope.RequestBringIntoView += keep;
            try
            {
                move();
                scope.UpdateLayout();
            }
            finally
            {
                scope.RequestBringIntoView -= keep;
            }
            Restore();
            scope.Dispatcher.BeginInvoke(new Action(Restore), System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }
}
