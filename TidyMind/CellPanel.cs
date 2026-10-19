using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace TidyMind
{
    // Lays its children out row by row in equal columns, as many as fit at MinColumn wide or more (at least one); a
    // child marked Wide takes a whole row. Each row is as tall as its tallest child. The vehicle window uses it for the
    // fields of a card and for the cards of a tab, so both reflow with the window's width.
    public sealed class CellPanel : Panel
    {
        public static readonly DependencyProperty WideProperty = DependencyProperty.RegisterAttached(
            "Wide", typeof(bool), typeof(CellPanel), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

        public static void SetWide(UIElement element, bool wide) => element.SetValue(WideProperty, wide);
        public static bool GetWide(UIElement element) => (bool)element.GetValue(WideProperty);

        public double MinColumn { get; set; } = 200;
        public double ColumnGap { get; set; } = 16;
        public double RowGap { get; set; } = 10;

        protected override Size MeasureOverride(Size available)
        {
            double width = double.IsInfinity(available.Width) ? MinColumn * 2 + ColumnGap : available.Width;
            double height = 0;
            foreach (List<(UIElement Child, Rect Slot)> row in Rows(width, child =>
                     {
                         child.Measure(new Size(SlotWidth(child, width), double.PositiveInfinity));
                         return child.DesiredSize.Height;
                     }))
                height = row[^1].Slot.Bottom;
            return new Size(width, height);
        }

        protected override Size ArrangeOverride(Size final)
        {
            foreach (List<(UIElement Child, Rect Slot)> row in Rows(final.Width, child => child.DesiredSize.Height))
                foreach ((UIElement child, Rect slot) in row)
                    child.Arrange(slot);
            return final;
        }

        private int Columns(double width) => Math.Max(1, (int)((width + ColumnGap) / (MinColumn + ColumnGap)));

        private double ColumnWidth(double width)
        {
            int columns = Columns(width);
            return Math.Max(0, (width - ColumnGap * (columns - 1)) / columns);
        }

        private double SlotWidth(UIElement child, double width) => GetWide(child) ? width : ColumnWidth(width);

        // The visible children placed in rows; heightOf measures (or reads) a child's height.
        private List<List<(UIElement Child, Rect Slot)>> Rows(double width, Func<UIElement, double> heightOf)
        {
            List<List<(UIElement, Rect)>> rows = new List<List<(UIElement, Rect)>>();
            int columns = Columns(width);
            double column = ColumnWidth(width), top = 0;
            List<(UIElement Child, double Height)> pending = new List<(UIElement, double)>();

            void Close()
            {
                if (pending.Count == 0)
                    return;
                double rowHeight = 0;
                foreach ((_, double h) in pending)
                    rowHeight = Math.Max(rowHeight, h);
                List<(UIElement, Rect)> row = new List<(UIElement, Rect)>();
                double left = 0;
                foreach ((UIElement child, _) in pending)
                {
                    double w = GetWide(child) ? width : column;
                    row.Add((child, new Rect(left, top, w, rowHeight)));
                    left += w + ColumnGap;
                }
                rows.Add(row);
                top += rowHeight + RowGap;
                pending.Clear();
            }

            foreach (UIElement child in InternalChildren)
            {
                if (child == null || child.Visibility == Visibility.Collapsed)
                    continue;
                if (GetWide(child))
                    Close();
                pending.Add((child, heightOf(child)));
                if (GetWide(child) || pending.Count == columns)
                    Close();
            }
            Close();
            return rows;
        }
    }
}
