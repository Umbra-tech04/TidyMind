using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace TidyMind
{
    // Drag-and-drop reordering for the items of one panel (sidebar memories, tab pills, project cards).
    // A press only turns into a drag after the system drag distance, so plain clicks keep working;
    // the release that ends a drag is swallowed so it can't also count as a click.
    internal class DragReorder<T> where T : class
    {
        private readonly Panel host;
        private readonly Orientation flow;
        private readonly double gap;
        private readonly Func<T, T, bool> canDrop;
        private readonly Action<T, T, bool> onDrop;

        // Unique per instance: an item dragged from one list is never a valid drop on another.
        private readonly string format = "TidyMind.Reorder." + Guid.NewGuid().ToString("N");
        private readonly Dictionary<FrameworkElement, T> items = new Dictionary<FrameworkElement, T>();

        // Elements whose drag just ended: their next release (if it arrives at all) is not a click.
        private readonly HashSet<FrameworkElement> dragEnded = new HashSet<FrameworkElement>();

        private FrameworkElement pressed;
        private Point pressPoint;
        private FrameworkElement target;
        private bool dropAfter;
        private InsertionAdorner indicator;

        // flow: how items are laid out (Horizontal = left-to-right). gap: space between neighbouring items.
        public DragReorder(Panel host, Orientation flow, double gap, Func<T, T, bool> canDrop, Action<T, T, bool> onDrop)
        {
            this.host = host;
            this.flow = flow;
            this.gap = gap;
            this.canDrop = canDrop ?? ((dragged, over) => true);
            this.onDrop = onDrop;

            host.AllowDrop = true;
            if (host.Background == null)
                host.Background = Brushes.Transparent; // so drags over the gaps between items still reach us

            host.DragOver += Host_DragOver;
            host.DragLeave += Host_DragLeave;
            host.Drop += Host_Drop;
        }

        // Call before re-rendering the panel's items.
        public void Clear()
        {
            items.Clear();
            HideIndicator();
        }

        public void Attach(FrameworkElement element, T item)
        {
            items[element] = item;

            element.PreviewMouseLeftButtonDown += (s, e) =>
            {
                pressed = element;
                pressPoint = e.GetPosition(host);
                dragEnded.Remove(element); // a new press: nothing left to swallow
            };

            element.PreviewMouseMove += (s, e) =>
            {
                if (pressed != element || e.LeftButton != MouseButtonState.Pressed) return;

                Vector moved = e.GetPosition(host) - pressPoint;
                if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance &&
                    Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
                    return;

                pressed = null;
                StartDrag(element, item);
            };

            element.PreviewMouseLeftButtonUp += (s, e) =>
            {
                pressed = null;
                if (dragEnded.Remove(element))
                    e.Handled = true;
            };
        }

        private static readonly Duration SlideDuration = TimeSpan.FromMilliseconds(320);
        private static readonly Duration FadeDuration = TimeSpan.FromMilliseconds(140);

        private void StartDrag(FrameworkElement element, T item)
        {
            element.BeginAnimation(UIElement.OpacityProperty, Ease(0.4, FadeDuration));
            dragEnded.Add(element);

            try
            {
                DragDrop.DoDragDrop(element, new DataObject(format, item), DragDropEffects.Move);
            }
            finally
            {
                // Cancelled drags fade back in place; after a drop this element has already been replaced.
                element.BeginAnimation(UIElement.OpacityProperty, Ease(1, FadeDuration));
                HideIndicator();
            }
        }

        // Reorders, then slides every item that moved from its old slot into its new one (and fades the dropped one in).
        private void Commit(T dragged, T over, bool after)
        {
            Dictionary<T, Vector> before = new Dictionary<T, Vector>();
            foreach (KeyValuePair<FrameworkElement, T> entry in items)
                before[entry.Value] = VisualTreeHelper.GetOffset(entry.Key);

            onDrop(dragged, over, after); // re-renders the panel, re-attaching fresh elements
            host.UpdateLayout();

            foreach (KeyValuePair<FrameworkElement, T> entry in items)
            {
                FrameworkElement element = entry.Key;

                if (EqualityComparer<T>.Default.Equals(entry.Value, dragged))
                    element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.4, 1, SlideDuration));

                if (!before.TryGetValue(entry.Value, out Vector old)) continue;
                Vector shift = old - VisualTreeHelper.GetOffset(element);
                if (shift.Length < 0.5) continue;

                // Cards already carry a TranslateTransform for their hover lift; reuse it so the lift keeps working.
                if (!(element.RenderTransform is TranslateTransform slide))
                {
                    slide = new TranslateTransform();
                    element.RenderTransform = slide;
                }
                slide.BeginAnimation(TranslateTransform.XProperty, Slide(shift.X));
                slide.BeginAnimation(TranslateTransform.YProperty, Slide(shift.Y));
            }
        }

        private static DoubleAnimation Slide(double from)
        {
            return new DoubleAnimation(from, 0, SlideDuration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
        }

        private static DoubleAnimation Ease(double to, Duration duration)
        {
            return new DoubleAnimation(to, duration) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
        }

        private void Host_DragOver(object sender, DragEventArgs e)
        {
            e.Handled = true;
            if (!(e.Data.GetData(format) is T dragged))
            {
                e.Effects = DragDropEffects.None;
                return;
            }

            FrameworkElement over = ItemUnder(e.OriginalSource as DependencyObject);
            if (over != null)
            {
                if (items[over] == dragged || !canDrop(dragged, items[over]))
                {
                    HideIndicator();
                }
                else
                {
                    Point p = e.GetPosition(over);
                    bool after = flow == Orientation.Horizontal ? p.X > over.ActualWidth / 2 : p.Y > over.ActualHeight / 2;
                    ShowIndicator(over, after);
                }
            }
            // Over a gap between items: keep the current indicator so it doesn't flicker.

            e.Effects = target != null ? DragDropEffects.Move : DragDropEffects.None;
        }

        private void Host_DragLeave(object sender, DragEventArgs e)
        {
            // DragLeave also fires when moving between our own children; only react to really leaving.
            Point p = e.GetPosition(host);
            if (p.X < 0 || p.Y < 0 || p.X >= host.ActualWidth || p.Y >= host.ActualHeight)
                HideIndicator();
        }

        private void Host_Drop(object sender, DragEventArgs e)
        {
            e.Handled = true;
            FrameworkElement dropTarget = target;
            bool after = dropAfter;
            HideIndicator();

            if (dropTarget != null && e.Data.GetData(format) is T dragged && items.TryGetValue(dropTarget, out T over) && over != dragged)
                Commit(dragged, over, after);
        }

        private FrameworkElement ItemUnder(DependencyObject d)
        {
            for (; d != null && d != host; d = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
                if (d is FrameworkElement fe && items.ContainsKey(fe))
                    return fe;
            return null;
        }

        private void ShowIndicator(FrameworkElement over, bool after)
        {
            if (target == over && dropAfter == after) return;

            HideIndicator();
            target = over;
            dropAfter = after;

            AdornerLayer layer = AdornerLayer.GetAdornerLayer(over);
            if (layer == null) return;
            indicator = new InsertionAdorner(over, flow, after, gap / 2);
            layer.Add(indicator);
            indicator.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, FadeDuration));
        }

        private void HideIndicator()
        {
            if (indicator != null)
                AdornerLayer.GetAdornerLayer(indicator.AdornedElement)?.Remove(indicator);
            indicator = null;
            target = null;
        }

        // The accent line drawn in the gap where the dragged item would land.
        private class InsertionAdorner : Adorner
        {
            private readonly Orientation flow;
            private readonly bool after;
            private readonly double offset;

            public InsertionAdorner(UIElement adorned, Orientation flow, bool after, double offset) : base(adorned)
            {
                this.flow = flow;
                this.after = after;
                this.offset = offset;
                IsHitTestVisible = false;
            }

            protected override void OnRender(DrawingContext dc)
            {
                Size size = AdornedElement.RenderSize;
                Pen pen = new Pen(CardEffects.Accent, 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };

                if (flow == Orientation.Horizontal)
                {
                    double x = after ? size.Width + offset : -offset;
                    dc.DrawLine(pen, new Point(x, 4), new Point(x, size.Height - 4));
                }
                else
                {
                    double y = after ? size.Height + offset : -offset;
                    dc.DrawLine(pen, new Point(6, y), new Point(size.Width - 6, y));
                }
            }
        }

        // Moves item next to target in the list; the caller then renumbers Order from list positions.
        public static void Move(List<T> list, T item, T target, bool after)
        {
            list.Remove(item);
            int at = list.IndexOf(target);
            list.Insert(after ? at + 1 : at, item);
        }
    }
}
