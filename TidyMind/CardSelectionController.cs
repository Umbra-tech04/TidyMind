using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace TidyMind
{
    // Windows-style selection on a card grid, shared by the project and the collection view: drag on empty space
    // for a marquee (cards it touches are selected, live), Ctrl+click a card to toggle it, a plain click on empty
    // space or Escape to clear. Selected cards get an outline and a check badge. The view shows its bulk-action bar
    // from SelectionChanged and provides what a bulk Delete does.
    public class CardSelectionController<T> where T : class
    {
        private const double DragThreshold = 4;

        private readonly ScrollViewer scroll;   // where a marquee can start (the visible grid area)
        private readonly Panel area;            // the scrolled content the cards sit in: marquee coordinates
        private readonly Canvas overlay;        // above the cards, in `area`, for the marquee rectangle
        private readonly double cardRadius;
        private readonly Func<IReadOnlyCollection<T>, bool> deleteSelected; // asks, deletes; true if it did

        private readonly Dictionary<FrameworkElement, T> cards = new Dictionary<FrameworkElement, T>();
        private readonly HashSet<T> selected = new HashSet<T>();
        private readonly Dictionary<FrameworkElement, FrameworkElement> marks = new Dictionary<FrameworkElement, FrameworkElement>();

        private bool tracking;              // left button down on empty space
        private Point start;
        private Rectangle marquee;          // shown once the drag passes the threshold
        private HashSet<T> baseline;        // what stays selected under a Ctrl+drag marquee

        public CardSelectionController(UIElement view, ScrollViewer scroll, Panel area, Canvas overlay, double cardRadius,
            Func<IReadOnlyCollection<T>, bool> deleteSelected)
        {
            this.scroll = scroll;
            this.area = area;
            this.overlay = overlay;
            this.cardRadius = cardRadius;
            this.deleteSelected = deleteSelected;

            scroll.PreviewMouseLeftButtonDown += Scroll_MouseDown;
            scroll.PreviewMouseMove += Scroll_MouseMove;
            scroll.PreviewMouseLeftButtonUp += Scroll_MouseUp;
            scroll.LostMouseCapture += (s, e) => EndMarquee(); // e.g. the window lost focus mid-drag
            view.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape && selected.Count > 0)
                {
                    ClearSelection();
                    e.Handled = true;
                }
            };
        }

        public event Action SelectionChanged;

        public IReadOnlyCollection<T> Selected => selected.ToList();
        public int Count => selected.Count;

        // ---- Cards (registered on every render) ---------------------------

        // Call before the view re-adds its cards.
        public void BeginRender()
        {
            cards.Clear();
            marks.Clear();
        }

        // Call while building each card, BEFORE the card's own drag-to-reorder and click handlers are attached, so
        // a Ctrl+click is taken here first and neither reorders nor opens the card.
        public void AttachCard(Panel card, T item)
        {
            cards[card] = item;

            card.PreviewMouseLeftButtonDown += (s, e) =>
            {
                if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
                ToggleSelect(item);
                scroll.Focus(); // so Escape reaches the view and clears it
                e.Handled = true;
            };

            // Right-click on a card that's part of a multi-selection: the bulk menu instead of the card's own.
            ContextMenu own = null;
            card.ContextMenuOpening += (s, e) =>
            {
                if (!(card.ContextMenu?.Tag is BulkMenuTag))
                    own = card.ContextMenu;
                card.ContextMenu = selected.Contains(item) && selected.Count > 1 ? BulkMenu() : own;
            };

            if (selected.Contains(item))
                ShowMark(card, true);
        }

        // Call after the render: only cards on screen can stay selected (a bulk Delete never touches hidden ones).
        public void EndRender()
        {
            if (selected.RemoveWhere(item => !cards.ContainsValue(item)) > 0)
                SelectionChanged?.Invoke();
        }

        // ---- Selection ----------------------------------------------------

        public void ToggleSelect(T item)
        {
            if (!selected.Remove(item))
                selected.Add(item);
            Repaint();
        }

        public void ClearSelection()
        {
            if (selected.Count == 0) return;
            selected.Clear();
            Repaint();
        }

        public bool DeleteSelected()
        {
            if (selected.Count == 0 || !deleteSelected(Selected))
                return false;
            selected.Clear();
            SelectionChanged?.Invoke();
            return true;
        }

        private void Repaint()
        {
            foreach (KeyValuePair<FrameworkElement, T> card in cards)
                ShowMark(card.Key, selected.Contains(card.Value));
            SelectionChanged?.Invoke();
        }

        private class BulkMenuTag { }

        private ContextMenu BulkMenu()
        {
            MenuItem delete = new MenuItem { Header = "Delete " + selected.Count + " selected" };
            delete.Click += (s, e) => DeleteSelected();
            MenuItem clear = new MenuItem { Header = "Clear selection" };
            clear.Click += (s, e) => ClearSelection();
            return new ContextMenu { Tag = new BulkMenuTag(), Items = { delete, clear } };
        }

        // ---- Marquee ------------------------------------------------------

        private void Scroll_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!OnEmptySpace(e.OriginalSource as DependencyObject))
                return;

            StartMarquee(e.GetPosition(area), additive: (Keyboard.Modifiers & ModifierKeys.Control) != 0);
            scroll.CaptureMouse();
        }

        private void Scroll_MouseMove(object sender, MouseEventArgs e)
        {
            if (tracking)
                UpdateMarquee(e.GetPosition(area));
        }

        private void Scroll_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!tracking) return;

            bool dragged = EndMarquee();
            if (!dragged && (Keyboard.Modifiers & ModifierKeys.Control) == 0)
                ClearSelection(); // a plain click on empty space
        }

        // Points are in the card area's coordinates (it scrolls with the cards). additive: Ctrl was held, so what's
        // already selected stays selected.
        public void StartMarquee(Point startPoint, bool additive = false)
        {
            tracking = true;
            start = startPoint;
            baseline = additive ? new HashSet<T>(selected) : new HashSet<T>();
        }

        // Nothing until the pointer has moved past a few pixels (a click isn't a marquee); then the rectangle, and
        // the selection becomes exactly the cards it touches.
        public void UpdateMarquee(Point now)
        {
            if (!tracking) return;

            if (marquee == null)
            {
                if (Math.Abs(now.X - start.X) < DragThreshold && Math.Abs(now.Y - start.Y) < DragThreshold)
                    return;
                marquee = new Rectangle
                {
                    Fill = new SolidColorBrush(Color.FromArgb(0x26, CardEffects.Accent.Color.R, CardEffects.Accent.Color.G, CardEffects.Accent.Color.B)),
                    Stroke = CardEffects.Accent,
                    StrokeThickness = 1,
                    SnapsToDevicePixels = true,
                    IsHitTestVisible = false
                };
                overlay.Children.Add(marquee);
            }

            Rect box = new Rect(start, now);
            Canvas.SetLeft(marquee, box.X);
            Canvas.SetTop(marquee, box.Y);
            marquee.Width = box.Width;
            marquee.Height = box.Height;

            // Live: exactly the cards the rectangle touches (plus, under Ctrl, what was selected before).
            selected.Clear();
            selected.UnionWith(baseline);
            foreach (KeyValuePair<FrameworkElement, T> card in cards)
            {
                if (card.Key.IsVisible && Bounds(card.Key).IntersectsWith(box))
                    selected.Add(card.Value);
            }
            Repaint();
        }

        // Removes the rectangle and keeps the selection; returns whether a rectangle was drawn at all (if not, the
        // gesture was a click).
        public bool EndMarquee()
        {
            if (!tracking) return false;
            tracking = false;
            bool drawn = marquee != null;
            if (drawn)
            {
                overlay.Children.Remove(marquee);
                marquee = null;
            }
            if (scroll.IsMouseCaptured)
                scroll.ReleaseMouseCapture();
            return drawn;
        }

        private Rect Bounds(FrameworkElement card)
        {
            return card.TransformToAncestor(area).TransformBounds(new Rect(0, 0, card.ActualWidth, card.ActualHeight));
        }

        // Not on a card, a button, a scrollbar or a text box: the grid's blank space.
        private bool OnEmptySpace(DependencyObject source)
        {
            for (DependencyObject d = source; d != null && d != scroll; d = Parent(d))
            {
                if (d is FrameworkElement element && cards.ContainsKey(element))
                    return false;
                if (d is ButtonBase || d is ScrollBar || d is TextBoxBase)
                    return false;
            }
            return true;
        }

        private static DependencyObject Parent(DependencyObject d)
        {
            return d is Visual || d is System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(d)
                : LogicalTreeHelper.GetParent(d);
        }

        // ---- Mark ---------------------------------------------------------

        // An accent outline just outside the card and a check badge in its top-left corner, laid over the card (so
        // it moves with the hover lift and looks the same on plain, color and picture cards).
        private void ShowMark(FrameworkElement card, bool on)
        {
            marks.TryGetValue(card, out FrameworkElement mark);
            if (on == (mark != null)) return;

            Panel panel = (Panel)card;
            if (!on)
            {
                panel.Children.Remove(mark);
                marks.Remove(card);
                return;
            }

            const double gap = 4, stroke = 2, badge = 22;
            Grid layer = new Grid { IsHitTestVisible = false };
            layer.Children.Add(new Border
            {
                Margin = new Thickness(-(gap + stroke)),
                BorderBrush = CardEffects.Accent,
                BorderThickness = new Thickness(stroke),
                CornerRadius = new CornerRadius(cardRadius + gap + stroke)
            });
            layer.Children.Add(new Border
            {
                Width = badge,
                Height = badge,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(-badge / 2 + 3, -badge / 2 + 3, 0, 0), // straddling the rounded corner
                CornerRadius = new CornerRadius(badge / 2),
                Background = CardEffects.Accent,
                BorderBrush = Brushes.White,
                BorderThickness = new Thickness(2),
                Child = new Path
                {
                    Data = Geometry.Parse("M 0,4 L 3.2,7 L 9,0.8"),
                    Stroke = Brushes.White,
                    StrokeThickness = 2,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    StrokeLineJoin = PenLineJoin.Round,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            });
            panel.Children.Add(layer);
            marks[card] = layer;
        }
    }
}
