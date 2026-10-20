using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace TidyMind
{
    // An item's pictures at the top of its window: the selected one whole and as large as the area allows, over a
    // blurred, darkened copy of itself that fills what its shape leaves free. With several, a slim strip of thumbnails
    // and a "2 / 5" count sit on the bottom edge, and previous / next arrows show while the mouse is over it (Left and
    // Right go through them too, ItemWindow). Like PictureBrowser it only draws what it is given; Picked asks for
    // another picture.
    public sealed class PictureHero : Grid
    {
        public event Action<int>? Picked;

        private readonly Image backdrop = new Image { Stretch = Stretch.UniformToFill, Effect = new BlurEffect { Radius = 48 } };
        private readonly Image picture = new Image { Stretch = Stretch.Uniform };
        private readonly StackPanel strip = new StackPanel { Orientation = Orientation.Horizontal };
        private readonly Border stripPlate;
        private readonly TextBlock count = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White };
        private readonly Border countPlate;
        private readonly Button previous, next;
        private int total;

        public int Selected { get; private set; } = -1;

        public PictureHero()
        {
            ClipToBounds = true;
            Background = (Brush)Application.Current.FindResource("Sidebar");
            Children.Add(backdrop);
            Children.Add(new Rectangle { Fill = new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)) });
            Children.Add(picture);
            Children.Add(new Rectangle
            {
                Height = 88,
                VerticalAlignment = VerticalAlignment.Bottom,
                IsHitTestVisible = false,
                // From transparent black, not Colors.Transparent (transparent white), which would draw a pale band.
                Fill = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Color.FromArgb(0x66, 0, 0, 0), 90)
            });

            stripPlate = Plate(strip, HorizontalAlignment.Left, new Thickness(4));
            countPlate = Plate(count, HorizontalAlignment.Right, new Thickness(10, 3, 10, 4));
            Children.Add(stripPlate);
            Children.Add(countPlate);

            previous = Arrow("", HorizontalAlignment.Left, -1);
            next = Arrow("", HorizontalAlignment.Right, +1);
            Children.Add(previous);
            Children.Add(next);
            MouseEnter += (s, e) => ShowArrows();
            MouseLeave += (s, e) => ShowArrows();
        }

        private static Border Plate(UIElement content, HorizontalAlignment side, Thickness padding) => new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x59, 0, 0, 0)),
            CornerRadius = new CornerRadius(10),
            Padding = padding,
            Margin = new Thickness(24, 0, 24, 16),
            HorizontalAlignment = side,
            VerticalAlignment = VerticalAlignment.Bottom,
            Child = content
        };

        private Button Arrow(string glyph, HorizontalAlignment side, int step)
        {
            Button arrow = new Button
            {
                Template = PictureBrowser.ArrowTemplate,
                Content = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 14, Foreground = (Brush)Application.Current.FindResource("TextMain") },
                Width = 40,
                Height = 40,
                Margin = new Thickness(16, 0, 16, 0),
                HorizontalAlignment = side,
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand,
                Visibility = Visibility.Collapsed
            };
            arrow.Click += (s, e) => Browse(step);
            return arrow;
        }

        // How far in from the sides the strip, the count and the arrows sit: lined up with the content under the pictures.
        public double Inset
        {
            set
            {
                stripPlate.Margin = countPlate.Margin = new Thickness(value, 0, value, 16);
                previous.Margin = next.Margin = new Thickness(value, 0, value, 0);
            }
        }

        // The previous / next picture (step -1 / +1), if there is one; false at either end.
        public bool Browse(int step)
        {
            int to = Selected + step;
            if (total < 2 || to < 0 || to >= total)
                return false;
            Picked?.Invoke(to);
            return true;
        }

        // The item's stored pictures (EntityImages names) with `selected` large. What is decoded already is drawn at once,
        // the rest off the UI thread and drawn then, if stillShowing.
        public void Show(IReadOnlyList<string> names, int selected, Func<bool> stillShowing)
        {
            Draw(names.Select(n => (ImageSource?)EntityImages.Loaded(n)).ToList(), selected);
            List<string> missing = names.Where(n => EntityImages.Loaded(n) == null).Distinct().ToList();
            if (missing.Count > 0)
                _ = DecodeAndDraw(names, missing, stillShowing);
        }

        private async System.Threading.Tasks.Task DecodeAndDraw(IReadOnlyList<string> names, List<string> missing, Func<bool> stillShowing)
        {
            await System.Threading.Tasks.Task.WhenAll(missing.Select(EntityImages.LoadAsync));
            if (stillShowing())
                Draw(names.Select(n => (ImageSource?)EntityImages.Loaded(n)).ToList(), Selected);
        }

        private void Draw(List<ImageSource?> pictures, int selected)
        {
            total = pictures.Count;
            Selected = total == 0 ? -1 : Math.Clamp(selected, 0, total - 1);
            picture.Source = backdrop.Source = Selected < 0 ? null : pictures[Selected];

            strip.Children.Clear();
            for (int i = 0; i < total; i++)
                strip.Children.Add(Thumbnail(pictures[i], i));
            stripPlate.Visibility = countPlate.Visibility = total > 1 ? Visibility.Visible : Visibility.Collapsed;
            count.Text = Loc.F("Entity.Picture.Count", Selected + 1, total);
            previous.IsEnabled = Selected > 0;
            next.IsEnabled = Selected < total - 1;
            foreach ((Button arrow, string key) in new[] { (previous, "Entity.Picture.Previous"), (next, "Entity.Picture.Next") })
            {
                arrow.ToolTip = Loc.T(key);
                AutomationProperties.SetName(arrow, Loc.T(key));
            }
            ShowArrows();
        }

        private void ShowArrows() =>
            previous.Visibility = next.Visibility = total > 1 && IsMouseOver ? Visibility.Visible : Visibility.Collapsed;

        // A small rounded thumbnail; the shown one framed in white, the others dimmed.
        private Button Thumbnail(ImageSource? source, int index)
        {
            bool shown = index == Selected;
            Button thumb = new Button
            {
                Template = PictureBrowser.FlatTemplate,
                Cursor = Cursors.Hand,
                Margin = new Thickness(2),
                Content = new Border
                {
                    Width = 44,
                    Height = 32,
                    CornerRadius = new CornerRadius(5),
                    BorderBrush = Brushes.White,
                    BorderThickness = new Thickness(shown ? 2 : 0),
                    Opacity = shown ? 1 : 0.7,
                    Background = source == null ? (Brush)Application.Current.FindResource("BgSubtle") : new ImageBrush(source) { Stretch = Stretch.UniformToFill }
                }
            };
            AutomationProperties.SetName(thumb, Loc.F(index == 0 ? "Entity.Picture.OfCover" : "Entity.Picture.Of", index + 1, total));
            thumb.Click += (s, e) => { if (index != Selected) Picked?.Invoke(index); };
            return thumb;
        }
    }
}
