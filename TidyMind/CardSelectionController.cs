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
    // ItemSelection on a card grid, shared by the project and the collection view, plus a marquee: drag on empty
    // space and the cards it touches are selected, live (Ctrl+drag adds to the selection). Selected cards get an
    // outline and a check badge. The view shows its bulk-action bar from SelectionChanged and provides the Delete.
    public class CardSelectionController<T> : ItemSelection<T> where T : class
    {
        private const double DragThreshold = 4;

        private readonly ScrollViewer scroll;   // where a marquee can start (the visible grid area)
        private readonly Panel area;            // the scrolled content the cards sit in: marquee coordinates
        private readonly Canvas overlay;        // above the cards, in `area`, for the marquee rectangle
        private readonly double cardRadius;

        private readonly Dictionary<FrameworkElement, FrameworkElement> marks = new Dictionary<FrameworkElement, FrameworkElement>();

        private bool tracking;              // left button down on empty space
        private Point start;
        private Rectangle? marquee;         // shown once the drag passes the threshold
        private HashSet<T> baseline = new HashSet<T>(); // what stays selected under a Ctrl+drag marquee

        // The keys (Esc, Delete, Ctrl+A) work while the grid has the keyboard; a selecting click gives it to the grid.
        public CardSelectionController(ScrollViewer scroll, Panel area, Canvas overlay, double cardRadius,
            Func<IReadOnlyList<T>, bool> deleteSelected)
            : base(scroll, deleteSelected, null)
        {
            this.scroll = scroll;
            this.area = area;
            this.overlay = overlay;
            this.cardRadius = cardRadius;

            scroll.PreviewMouseLeftButtonDown += Scroll_MouseDown;
            scroll.PreviewMouseMove += Scroll_MouseMove;
            scroll.PreviewMouseLeftButtonUp += Scroll_MouseUp;
            scroll.LostMouseCapture += (s, e) => EndMarquee(); // e.g. the window lost focus mid-drag
        }

        // Call before the view re-adds its cards.
        public override void BeginRender()
        {
            base.BeginRender();
            marks.Clear();
        }

        // ---- Marquee ------------------------------------------------------

        private void Scroll_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!OnEmptySpace(e.OriginalSource as DependencyObject))
                return;

            scroll.Focus();
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
            baseline = additive ? new HashSet<T>(Selected) : new HashSet<T>();
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
            SetSelection(baseline.Concat(Shown
                .Where(card => card.Key.IsVisible && Bounds(card.Key).IntersectsWith(box))
                .Select(card => card.Value)));
        }

        // Removes the rectangle and keeps the selection; returns whether a rectangle was drawn at all (if not, the
        // gesture was a click).
        public bool EndMarquee()
        {
            if (!tracking) return false;
            tracking = false;
            bool drawn = marquee != null;
            if (marquee != null)
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
        private bool OnEmptySpace(DependencyObject? source)
        {
            for (DependencyObject? d = source; d != null && d != scroll; d = Parent(d))
            {
                if (IsItemElement(d))
                    return false;
                if (d is ButtonBase || d is ScrollBar || d is TextBoxBase)
                    return false;
            }
            return true;
        }

        // ---- Mark ---------------------------------------------------------

        // An accent outline just outside the card and a check badge in its top-left corner, laid over the card (so
        // it moves with the hover lift and looks the same on plain, color and picture cards).
        protected override void Paint(FrameworkElement card, bool on)
        {
            marks.TryGetValue(card, out FrameworkElement? mark);
            if (on == (mark != null)) return;

            Panel panel = (Panel)card;
            if (!on)
            {
                panel.Children.Remove(mark);
                marks.Remove(card);
                return;
            }

            const double gap = 4, stroke = 2, badgeSize = 22;
            Grid layer = new Grid { IsHitTestVisible = false };
            layer.Children.Add(new Border
            {
                Margin = new Thickness(-(gap + stroke)),
                BorderBrush = CardEffects.Accent,
                BorderThickness = new Thickness(stroke),
                CornerRadius = new CornerRadius(cardRadius + gap + stroke)
            });
            FrameworkElement badge = SelectionLook.CheckBadge(badgeSize);
            badge.HorizontalAlignment = HorizontalAlignment.Left;
            badge.VerticalAlignment = VerticalAlignment.Top;
            badge.Margin = new Thickness(-badgeSize / 2 + 3, -badgeSize / 2 + 3, 0, 0); // straddling the rounded corner
            layer.Children.Add(badge);
            panel.Children.Add(layer);
            marks[card] = layer;
        }
    }
}
