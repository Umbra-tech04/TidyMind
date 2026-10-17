using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace TidyMind
{
    // How a selected item looks (ItemSelection), always in the accent color: cards get an outline and a check badge
    // (CardSelectionController), Day Plan timeline blocks an accent edge and the badge; rows on the light pages a soft
    // accent fill with an accent edge; sidebar rows (dark) a deeper accent fill.
    public static class SelectionLook
    {
        public static Brush RowFill => (Brush)Application.Current.FindResource("AccentSoft");
        public static Brush RowEdge => (Brush)Application.Current.FindResource("Accent");

        // On the dark sidebar: the accent at half strength reads as blue there and keeps the light text readable.
        public static readonly Brush SidebarFill =
            Frozen(Color.FromArgb(0x80, CardEffects.Accent.Color.R, CardEffects.Accent.Color.G, CardEffects.Accent.Color.B));

        // A round accent badge with a white check, for the corner of a selected card.
        public static FrameworkElement CheckBadge(double size)
        {
            return new Border
            {
                Width = size,
                Height = size,
                CornerRadius = new CornerRadius(size / 2),
                Background = CardEffects.Accent,
                BorderBrush = Brushes.White,
                BorderThickness = new Thickness(2),
                IsHitTestVisible = false,
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
            };
        }

        // A row on a light page: filled and edged while selected, else back to `rest` (its hover look, if hovered).
        public static void PaintRow(Border row, bool on, Brush rest, Brush restEdge)
        {
            row.Background = on ? RowFill : rest;
            row.BorderBrush = on ? RowEdge : restEdge;
        }

        private static Brush Frozen(Color color)
        {
            SolidColorBrush brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }
}
