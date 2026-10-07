using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace TidyMind
{
    // Edit Background: a memory's page background (project or collection). The same palette as Customize Card,
    // but every swatch is shown washed out the way the page will wear it (a vivid swatch would promise a much
    // stronger page than you get), and the preview is a small page with a few cards on it.
    public static class MemoryBackgroundDialog
    {
        private const double PreviewWidth = 190;
        private const double PreviewHeight = 132;

        // The choice, or null if the dialog was cancelled.
        public static BackgroundChoice Choose(DependencyObject owner, Profile profile)
        {
            BackgroundDialog dialog = new BackgroundDialog("Edit background", profile.Name + "  ·  every tab of this memory",
                profile.GetBackground(), ImageStore.Backgrounds, PageBackground.Washed,
                (type, color, pickedImage) => PagePreview(profile, type, color, pickedImage));
            return dialog.Ask(owner);
        }

        // The page in miniature: off-white base, the background, the memory's name and three plain cards.
        private static FrameworkElement PagePreview(Profile profile, BackgroundFill type, string color, ImageSource pickedImage)
        {
            Grid layer = new Grid();
            PageBackground.Fill(layer, type, color, pickedImage, profile.BackgroundImagePath);

            StackPanel content = new StackPanel { Margin = new Thickness(12, 10, 12, 10) };
            content.Children.Add(new TextBlock
            {
                Text = profile.Name,
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = (Brush)Application.Current.FindResource("TextMain"),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            WrapPanel cards = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            for (int i = 0; i < 3; i++)
            {
                cards.Children.Add(new Rectangle
                {
                    Width = 44,
                    Height = 44,
                    RadiusX = 5,
                    RadiusY = 5,
                    Fill = Brushes.White,
                    Stroke = new SolidColorBrush(Color.FromRgb(0xEC, 0xEC, 0xE9)),
                    StrokeThickness = 1.5,
                    Margin = new Thickness(0, 0, 8, 0)
                });
            }
            content.Children.Add(cards);

            Grid page = new Grid
            {
                Children = { layer, content },
                // Rounded like the frame (ClipToBounds alone would leave square corners on a picture).
                Clip = new RectangleGeometry(new Rect(0, 0, PreviewWidth - 2, PreviewHeight - 2), 9, 9)
            };
            return new Border
            {
                Width = PreviewWidth,
                Height = PreviewHeight,
                CornerRadius = new CornerRadius(10),
                BorderBrush = (Brush)Application.Current.FindResource("Border"),
                BorderThickness = new Thickness(1),
                Background = (Brush)Application.Current.FindResource("BgMain"),
                ClipToBounds = true,
                Child = page
            };
        }
    }
}
