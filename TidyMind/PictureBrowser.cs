using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;

namespace TidyMind
{
    // An item's pictures to look through: the selected one large, with a previous / next arrow on it, and a strip of
    // thumbnails under it (the first marked Cover, the selected one framed). Used by the item form, the detail card and
    // the vehicle window.
    //
    // It only shows what its owner says (Show): the owner keeps the list and the selected index, the one source of
    // truth, and draws again after any change. A thumbnail click, an arrow or the Left / Right key asks for another
    // picture (Picked); the arrows and keys only browse, they never change the order. With canReorder, dragging a
    // thumbnail to another place asks for a move (Moved); the owner moves it and keeps it selected.
    public sealed class PictureBrowser : Grid
    {
        public const double ThumbSize = 64;

        public event Action<int> Picked;
        public event Action<int, int> Moved; // from index, to index (in the list after taking it out)

        private static readonly ControlTemplate FlatTemplate = (ControlTemplate)XamlReader.Parse(
            "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Button'>" +
            "<ContentPresenter/></ControlTemplate>");

        // A round, light button over the picture; dimmed when there's nothing more that way.
        private static readonly ControlTemplate ArrowTemplate = (ControlTemplate)XamlReader.Parse(
            "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'" +
            " xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Button'>" +
            "<Border x:Name='b' Width='32' Height='32' CornerRadius='16' Background='#F2FFFFFF' BorderBrush='#E0E0DE' BorderThickness='1'>" +
            "<ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/></Border>" +
            "<ControlTemplate.Triggers>" +
            "<Trigger Property='IsMouseOver' Value='True'><Setter TargetName='b' Property='Background' Value='#FFF0F0EE'/></Trigger>" +
            "<Trigger Property='IsEnabled' Value='False'><Setter TargetName='b' Property='Opacity' Value='0.35'/></Trigger>" +
            "</ControlTemplate.Triggers></ControlTemplate>");

        private readonly Image preview;
        private readonly Button previous, next;
        private readonly WrapPanel strip;
        private readonly ScrollViewer stripScroll;
        private readonly DragReorder<Button> drag;
        private readonly bool stripForOne;
        private int count;

        public int Selected { get; private set; } = -1;

        // previewHeight: the large picture's height (NaN: whatever room there is). stripMaxHeight: the strip scrolls
        // beyond it. stripForOne: show the strip for a single picture too (the form, where it shows the cover badge).
        public PictureBrowser(double previewHeight, double stripMaxHeight, bool canReorder, bool stripForOne)
        {
            this.stripForOne = stripForOne;
            RowDefinitions.Add(new RowDefinition { Height = double.IsNaN(previewHeight) ? new GridLength(1, GridUnitType.Star) : new GridLength(previewHeight) });
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            Grid big = new Grid();
            preview = new Image { Stretch = Stretch.Uniform, Margin = new Thickness(40, 0, 40, 0) };
            previous = Arrow("", HorizontalAlignment.Left, -1);
            next = Arrow("", HorizontalAlignment.Right, +1);
            big.Children.Add(preview);
            big.Children.Add(previous);
            big.Children.Add(next);
            Children.Add(big);

            strip = new WrapPanel();
            stripScroll = new ScrollViewer
            {
                Content = strip,
                MaxHeight = stripMaxHeight,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new Thickness(0, 10, 0, 0)
            };
            SetRow(stripScroll, 1);
            Children.Add(stripScroll);

            // Thumbnails sit 8px apart (their right margin); the drop line goes in that gap.
            if (canReorder)
                drag = new DragReorder<Button>(strip, Orientation.Horizontal, 8, null, (dragged, target, after) =>
                {
                    int from = strip.Children.IndexOf(dragged), to = strip.Children.IndexOf(target) + (after ? 1 : 0);
                    if (from < to) to--;
                    if (from >= 0 && to >= 0 && from != to)
                        Moved?.Invoke(from, to);
                });

            PreviewKeyDown += (s, e) =>
            {
                if ((e.Key == Key.Left || e.Key == Key.Right) && Keyboard.Modifiers == ModifierKeys.None)
                {
                    Browse(e.Key == Key.Left ? -1 : +1);
                    e.Handled = true;
                }
            };
        }

        private Button Arrow(string glyph, HorizontalAlignment side, int step)
        {
            Button arrow = new Button
            {
                Template = ArrowTemplate,
                // A TextBlock of its own: the app's TextBlock style would put Segoe UI on a plain string.
                Content = new TextBlock
                {
                    Text = glyph,
                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize = 12,
                    Foreground = (Brush)Application.Current.FindResource("TextMain")
                },
                Cursor = Cursors.Hand,
                HorizontalAlignment = side,
                VerticalAlignment = VerticalAlignment.Center
            };
            arrow.Click += (s, e) => Browse(step);
            return arrow;
        }

        // The previous / next picture (step -1 / +1), if there is one; false at either end.
        public bool Browse(int step)
        {
            int to = Selected + step;
            if (count < 2 || to < 0 || to >= count)
                return false;
            Picked?.Invoke(to);
            return true;
        }

        // Draws the pictures with `selected` large and framed (-1 or an empty list: nothing).
        public void Show(IReadOnlyList<ImageSource> pictures, int selected)
        {
            count = pictures.Count;
            Selected = count == 0 ? -1 : Math.Clamp(selected, 0, count - 1);

            // The strip is drawn anew; the keyboard stays on the selected thumbnail (or moves there from an arrow
            // that has nothing more to show).
            bool keyboardInStrip = strip.IsKeyboardFocusWithin;
            Button focusedArrow = previous.IsKeyboardFocused ? previous : next.IsKeyboardFocused ? next : null;
            preview.Source = Selected < 0 ? null : pictures[Selected];

            drag?.Clear();
            strip.Children.Clear();
            for (int i = 0; i < count; i++)
            {
                int index = i;
                Button thumb = new Button
                {
                    Template = FlatTemplate,
                    Cursor = Cursors.Hand,
                    Margin = new Thickness(0, 0, 8, 8),
                    Content = Thumbnail(pictures[i], i == 0, i == Selected)
                };
                AutomationProperties.SetName(thumb, Loc.F(i == 0 ? "Entity.Picture.OfCover" : "Entity.Picture.Of", i + 1, count));
                thumb.Click += (s, e) => { if (index != Selected) Picked?.Invoke(index); };
                drag?.Attach(thumb, thumb);
                strip.Children.Add(thumb);
            }
            stripScroll.Visibility = count > 1 || (count == 1 && stripForOne) ? Visibility.Visible : Visibility.Collapsed;

            previous.Visibility = next.Visibility = count > 1 ? Visibility.Visible : Visibility.Collapsed;
            previous.IsEnabled = Selected > 0;
            next.IsEnabled = Selected >= 0 && Selected < count - 1;
            foreach ((Button arrow, string key) in new[] { (previous, "Entity.Picture.Previous"), (next, "Entity.Picture.Next") })
            {
                arrow.ToolTip = Loc.T(key);
                AutomationProperties.SetName(arrow, Loc.T(key));
            }

            if (Selected >= 0 && (keyboardInStrip || (focusedArrow != null && !focusedArrow.IsEnabled)))
                strip.Children[Selected].Focus();
        }

        // An item's stored pictures (EntityImages names) with `selected` large. What is decoded already is drawn at once;
        // the rest is decoded off the UI thread and drawn then, if stillShowing (once: a missing file stays empty).
        public void ShowStored(IReadOnlyList<string> names, int selected, Func<bool> stillShowing)
        {
            List<ImageSource> shown = names.Select(n => (ImageSource)EntityImages.Loaded(n)).ToList();
            Show(shown, selected);
            List<string> missing = names.Where((n, i) => shown[i] == null).Distinct().ToList();
            if (missing.Count > 0)
                _ = DecodeAndShow(names, missing, stillShowing);
        }

        private async System.Threading.Tasks.Task DecodeAndShow(IReadOnlyList<string> names, List<string> missing, Func<bool> stillShowing)
        {
            await System.Threading.Tasks.Task.WhenAll(missing.Select(EntityImages.LoadAsync));
            if (stillShowing())
                Show(names.Select(n => (ImageSource)EntityImages.Loaded(n)).ToList(), Selected);
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
