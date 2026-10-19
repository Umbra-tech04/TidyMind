using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;

namespace TidyMind
{
    // A row of small pictures to click through, for an item's pictures (the item form's list and the detail card).
    // The first is the cover and says so; the selected one has a blue frame. A thumbnail is a button, so it can be
    // reached and chosen with the keyboard too.
    public static class PictureStrip
    {
        public const double ThumbSize = 64;

        private static readonly ControlTemplate Flat = (ControlTemplate)XamlReader.Parse(
            "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Button'>" +
            "<ContentPresenter/></ControlTemplate>");

        // Replaces the panel's thumbnails. A picture that can't be shown (its file is missing) is an empty frame, so
        // it can still be selected and removed.
        public static void Fill(Panel panel, IReadOnlyList<ImageSource> pictures, int selected, Action<int> select)
        {
            panel.Children.Clear();
            for (int i = 0; i < pictures.Count; i++)
            {
                int index = i;
                Button thumb = new Button
                {
                    Template = Flat,
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Margin = new Thickness(0, 0, 8, 8),
                    Content = Thumbnail(pictures[i], i == 0, i == selected)
                };
                AutomationProperties.SetName(thumb, Loc.F("Entity.Picture.Of", i + 1, pictures.Count)
                    + (i == 0 ? ", " + Loc.T("Entity.Picture.Cover") : ""));
                thumb.Click += (s, e) => select(index);
                panel.Children.Add(thumb);
            }
        }

        private static FrameworkElement Thumbnail(ImageSource picture, bool cover, bool selected)
        {
            Grid grid = new Grid { Width = ThumbSize, Height = ThumbSize };
            grid.Children.Add(new Border
            {
                Background = (Brush)Application.Current.FindResource("BgSubtle"),
                BorderBrush = (Brush)Application.Current.FindResource(selected ? "Accent" : "Border"),
                BorderThickness = new Thickness(selected ? 2 : 1),
                CornerRadius = new CornerRadius(6),
                Child = new Image { Source = picture, Stretch = Stretch.Uniform, Margin = new Thickness(3) }
            });
            if (cover)
            {
                grid.Children.Add(new Border
                {
                    Background = (Brush)Application.Current.FindResource("BtnHover"), // white on it is 6:1
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(4, 1, 4, 1),
                    Margin = new Thickness(4),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Child = new TextBlock
                    {
                        Text = Loc.T("Entity.Picture.Cover"),
                        FontSize = 10,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = Brushes.White
                    }
                });
            }
            return grid;
        }
    }
}
