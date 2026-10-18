using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace TidyMind
{
    // A memory's page background (Edit Background, projects and collections), drawn behind everything on the page.
    // A palette color stays a faint tint over the off-white page. A picture shows as clearly as Settings > Background
    // pictures says (ImageVisibility), under a thin white film; the page puts its header and tabs on a light plate
    // over it (MemoryPageBackground), so dark text stays readable on bright, dark and busy photos. With Adaptive card
    // tint on, that plate and the empty-tab card are frosted glass instead (CardTint).
    public static class PageBackground
    {
        public const double ColorOpacity = 0.18; // a tint, not a fill

        public const int DefaultImageVisibility = 70;
        public const int MinImageVisibility = 20;

        // One film for every picture on screen: the slider changes it live, everywhere at once.
        private static readonly SolidColorBrush ImageWash = new SolidColorBrush(Colors.White);

        static PageBackground() => ImageVisibility = AppSettings.Load().BackgroundImageVisibility ?? DefaultImageVisibility;

        // How much of a background picture shows, in percent: 100 the photo as it is, MinImageVisibility a faint
        // watermark. The white film over it is the rest.
        public static int ImageVisibility
        {
            get => (int)System.Math.Round((1 - ImageWash.Opacity) * 100);
            set
            {
                ImageWash.Opacity = 1 - System.Math.Clamp(value, MinImageVisibility, 100) / 100.0;
                CardTint.RefreshAll(); // the tint follows how light the page gets
            }
        }

        // The white film over every picture, and its opacity: CardTint lays the same film on its frosted copy.
        internal static Brush Film => ImageWash;
        internal static double FilmOpacity => ImageWash.Opacity;

        // Fills `layer` (a panel spanning the page, behind its content, over the off-white base) for a background.
        // `image` overrides the stored picture (a dialog preview). Nothing is drawn for None or a missing picture.
        // A stored picture not decoded yet this session is decoded off the UI thread: the page shows its plain
        // base meanwhile and the picture appears when ready.
        public static void Fill(Panel layer, BackgroundFill type, string color, ImageSource image = null, string storedImage = null)
        {
            layer.Children.Clear();
            object fill = new object();
            layer.Tag = fill; // which Fill is current: a slower, older decode mustn't paint over a newer choice
            CardTint.PictureShown(layer, null);

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
            layer.Children.Add(new Rectangle { Fill = ImageWash });
            CardTint.PictureShown(layer, picture);
        }

        // Added on the layer's own UI thread once decoded; skipped if the layer has been refilled since (another
        // background chosen).
        private static async void AddWhenDecoded(Panel layer, object fill, Task<BitmapImage> decoding)
        {
            BitmapImage picture = await decoding.ConfigureAwait(false);
            if (picture == null)
                return;
            _ = layer.Dispatcher.BeginInvoke(new System.Action(() =>
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
