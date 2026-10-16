using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace TidyMind
{
    // The row of color dots a calendar day or a day plan block picks its color from: "no color", then the palette.
    // The chosen one wears a dark ring.
    public sealed class SwatchRow
    {
        private static readonly string[] Palette =
        {
            "#2E7BC4", "#27AE60", "#1ABC9C", "#F2C94C", "#F2994A", "#EB5757", "#E86FB0", "#9B51E0", "#8A8A87"
        };

        private readonly List<KeyValuePair<Border, string?>> swatches = new List<KeyValuePair<Border, string?>>();

        public string? Selected { get; private set; }

        // Fills `panel` with the dots, `selected` already chosen.
        public SwatchRow(Panel panel, string? selected)
        {
            Selected = selected;
            panel.Children.Add(Swatch(null));
            foreach (string hex in Palette)
                panel.Children.Add(Swatch(hex));
            Paint();
        }

        private Border Swatch(string? hex)
        {
            Border fill = new Border
            {
                CornerRadius = new CornerRadius(11),
                Background = hex == null ? Brushes.White : B(hex)
            };

            if (hex == null)
            {
                // "No colour": a white dot with a thin diagonal slash.
                fill.BorderBrush = B("#D5D5D2");
                fill.BorderThickness = new Thickness(1);
                fill.Child = new Line
                {
                    X1 = 16, Y1 = 5, X2 = 5, Y2 = 16,
                    Stroke = B("#BDBDB9"),
                    StrokeThickness = 1.5
                };
            }

            Border ring = new Border
            {
                Width = 30,
                Height = 30,
                CornerRadius = new CornerRadius(15),
                BorderThickness = new Thickness(2),
                Padding = new Thickness(2),
                Margin = new Thickness(0, 0, 4, 0),
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent,
                ToolTip = hex == null ? Loc.T("Swatch.NoColor") : null,
                Child = fill
            };

            CardEffects.AttachClick(ring, () =>
            {
                Selected = hex;
                Paint();
            });

            swatches.Add(new KeyValuePair<Border, string?>(ring, hex));
            return ring;
        }

        private void Paint()
        {
            foreach (KeyValuePair<Border, string?> swatch in swatches)
            {
                bool selected = string.Equals(swatch.Value, Selected, StringComparison.OrdinalIgnoreCase);
                swatch.Key.BorderBrush = selected ? (Brush)Application.Current.FindResource("TextMain") : Brushes.Transparent;
            }
        }

        private static SolidColorBrush B(string hex) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
    }
}
