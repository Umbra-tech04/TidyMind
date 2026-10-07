using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace TidyMind
{
    // A memory's page background (Edit Background, projects and collections), drawn behind everything on the page.
    // It stays faint by design, so every dark-on-light element on top (header, search box, tabs, cards) keeps
    // working unchanged: a palette color is laid over the off-white page at low opacity; a picture is washed out
    // under white.
    public static class PageBackground
    {
        public const double ColorOpacity = 0.18; // a tint, not a fill
        public const double ImageWash = 0.82;    // white over the picture: a watermark, not a photo

        // Fills `layer` (a panel spanning the page, behind its content, over the off-white base) for a background.
        // `image` overrides the stored picture (a dialog preview). Nothing is drawn for None or a missing picture.
        // A stored picture not decoded yet this session is decoded off the UI thread: the page shows its plain
        // base meanwhile and the picture appears when ready.
        public static void Fill(Panel layer, BackgroundFill type, string color, ImageSource image = null, string storedImage = null)
        {
            layer.Children.Clear();
            object fill = new object();
            layer.Tag = fill; // which Fill is current: a slower, older decode mustn't paint over a newer choice

            switch (type)
            {
                case BackgroundFill.Color:
                    Color? tint = ProjectCard.ParseColor(color);
                    if (tint.HasValue)
                        layer.Children.Add(new Rectangle { Fill = new SolidColorBrush(tint.Value), Opacity = ColorOpacity });
                    break;

                case BackgroundFill.Image:
                    ImageSource picture = image ?? ImageStore.Backgrounds.TryGetLoaded(storedImage);
                    if (picture != null)
                        AddPicture(layer, picture);
                    else if (ImageStore.Backgrounds.Exists(storedImage))
                        AddWhenDecoded(layer, fill, ImageStore.Backgrounds.LoadAsync(storedImage));
                    break;
            }
        }

        private static void AddPicture(Panel layer, ImageSource picture)
        {
            layer.Children.Add(new Rectangle { Fill = new ImageBrush(picture) { Stretch = Stretch.UniformToFill } });
            layer.Children.Add(new Rectangle { Fill = Brushes.White, Opacity = ImageWash });
        }

        // Added on the layer's own UI thread once decoded; skipped if the layer has been refilled since (another
        // background chosen).
        private static async void AddWhenDecoded(Panel layer, object fill, Task<BitmapImage> decoding)
        {
            BitmapImage picture = await decoding.ConfigureAwait(false);
            if (picture == null)
                return;
            layer.Dispatcher.BeginInvoke(new System.Action(() =>
            {
                if (layer.Tag == fill)
                    AddPicture(layer, picture);
            }));
        }

        // A palette color as it ends up on the page: blended into the off-white base at ColorOpacity. Used for the
        // Edit Background swatches, so the picker shows what the page will really look like.
        public static Color Washed(Color color)
        {
            Color page = ((SolidColorBrush)Application.Current.FindResource("BgMain")).Color;
            byte Mix(byte over, byte under) => (byte)System.Math.Round(over * ColorOpacity + under * (1 - ColorOpacity));
            return Color.FromRgb(Mix(color.R, page.R), Mix(color.G, page.G), Mix(color.B, page.B));
        }
    }
}
