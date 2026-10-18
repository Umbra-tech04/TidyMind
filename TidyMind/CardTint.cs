using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TidyMind
{
    // The color laid over a frosted surface: a tint at an opacity.
    public readonly record struct TintLook(Color Color, double Opacity);

    // Settings > Appearance > Adaptive card tint: on a memory page with a background picture, the surfaces that sit on
    // the picture (the plate behind the header and tabs, the empty-tab card) become frosted glass: the blurred picture
    // (BackgroundSample), the same white film as the page (PageBackground), and a dark tint whose strength follows
    // the picture under that surface: deeper and more opaque where the picture is light, a lighter, fainter slate
    // where it's dark. The text on them turns light, at 4.5:1 (WCAG AA) or more against the brightest spot under it.
    // Off, or with no picture, or when the picture can't be measured, the surfaces look exactly as without it.
    public static class CardTint
    {
        public const double MinOpacity = 0.4;  // a dark background still gets a visible card
        public const double MaxOpacity = 0.9;  // the picture always shows through a little
        public const double MinContrast = 4.5;
        private const double RoundingMargin = 0.15; // the drawn glass is blended and scaled in whole bytes

        private static readonly Color Ink = Color.FromRgb(0x11, 0x15, 0x1C);   // over light pictures
        private static readonly Color Slate = Color.FromRgb(0x34, 0x3B, 0x47); // over dark ones

        // The text on glass (the plain look's dark text colors are App.xaml's Plate* resources).
        public static readonly Color Text = Color.FromRgb(0xF5, 0xF5, 0xF3);
        public static readonly Color TextSub = Color.FromRgb(0xC3, 0xC8, 0xD0);
        private static readonly Color Icon = Color.FromRgb(0x6F, 0xC3, 0xFF);
        private static readonly Color TabHover = Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF);

        // Each surface's resources on glass; the two sets don't share keys, since both live inside the same page.
        public static readonly IReadOnlyDictionary<string, Color> HeaderColors = new Dictionary<string, Color>
        {
            ["PlateText"] = Text, ["PlateTextSub"] = TextSub, ["PlateTabText"] = TextSub, ["PlateTabHover"] = TabHover
        };
        public static readonly IReadOnlyDictionary<string, Color> EmptyCardColors = new Dictionary<string, Color>
        {
            ["PlateBody"] = Text, ["PlateIcon"] = Icon
        };

        private static bool? enabled;
        private static readonly List<WeakReference<PageTint>> Pages = new List<WeakReference<PageTint>>();
        private static readonly ConditionalWeakTable<Panel, PageTint> ByLayer = new ConditionalWeakTable<Panel, PageTint>();

        // Applies at once to every open page.
        public static bool Enabled
        {
            get => enabled ??= AppSettings.Load().AdaptiveCardTint != false;
            set
            {
                enabled = value;
                RefreshAll();
            }
        }

        // The tint for a surface over `brightest` (channel by channel, the brightest of the picture under it) and
        // `mean` (its average), under the page's white film at `film` opacity: the least opacity, at least MinOpacity,
        // at which both text colors read at MinContrast. The deep ink if the slate can't get there by MaxOpacity.
        public static TintLook Pick(Color brightest, Color mean, double film)
        {
            Color worst = Over(Colors.White, film, brightest);
            double light = Math.Clamp((Luminance(Over(Colors.White, film, mean)) - 0.1) / 0.4, 0, 1);
            Color tint = Mix(Slate, Ink, light);
            double? opacity = LeastOpacity(tint, worst);
            if (opacity == null)
            {
                tint = Ink;
                opacity = LeastOpacity(Ink, worst) ?? MaxOpacity;
            }
            return new TintLook(tint, opacity.Value);
        }

        private static double? LeastOpacity(Color tint, Color under)
        {
            for (int step = 0; step <= 50; step++)
            {
                double opacity = MinOpacity + (MaxOpacity - MinOpacity) * step / 50;
                Color surface = Over(tint, opacity, under);
                if (Contrast(Text, surface) >= MinContrast + RoundingMargin && Contrast(TextSub, surface) >= MinContrast + RoundingMargin)
                    return opacity;
            }
            return null;
        }

        // `top` at `opacity` over `under`, channel by channel in sRGB, as WPF blends.
        public static Color Over(Color top, double opacity, Color under)
        {
            byte Blend(byte t, byte u) => (byte)Math.Clamp(Math.Round(t * opacity + u * (1 - opacity)), 0, 255);
            return Color.FromRgb(Blend(top.R, under.R), Blend(top.G, under.G), Blend(top.B, under.B));
        }

        private static Color Mix(Color from, Color to, double amount) => Over(to, amount, from);

        // WCAG relative luminance and contrast ratio.
        public static double Luminance(Color c)
        {
            static double Linear(byte v)
            {
                double s = v / 255.0;
                return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);
        }

        public static double Contrast(Color a, Color b)
        {
            double la = Luminance(a), lb = Luminance(b);
            return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
        }

        // The page whose background is drawn into `layer`; made once per layer.
        internal static PageTint Track(Panel layer)
        {
            if (ByLayer.TryGetValue(layer, out PageTint page))
                return page;
            page = new PageTint(layer);
            ByLayer.Add(layer, page);
            lock (Pages)
                Pages.Add(new WeakReference<PageTint>(page));
            return page;
        }

        // From PageBackground: the picture now on `layer`, null when it has none (any other background, or not decoded
        // yet). Layers no page tracks (a dialog's preview) are left alone.
        internal static void PictureShown(Panel layer, ImageSource picture)
        {
            if (ByLayer.TryGetValue(layer, out PageTint page))
                page.Show(picture as BitmapSource);
        }

        // The slider or the switch moved: every open page again.
        internal static void RefreshAll()
        {
            List<PageTint> open = new List<PageTint>();
            lock (Pages)
            {
                Pages.RemoveAll(w => !w.TryGetTarget(out _));
                foreach (WeakReference<PageTint> w in Pages)
                    if (w.TryGetTarget(out PageTint page))
                        open.Add(page);
            }
            foreach (PageTint page in open)
                page.Refresh();
        }

        // What a surface shows now: null for the plain look (the harness reads this).
        internal static TintLook? Shown(Border surface) => PageTint.ShownOn(surface);
    }

    // One memory page's frosted surfaces over its background layer.
    internal sealed class PageTint
    {
        private static readonly ConditionalWeakTable<Border, StrongBox<TintLook>> Looks = new ConditionalWeakTable<Border, StrongBox<TintLook>>();

        private readonly Panel layer;
        private readonly List<Surface> surfaces = new List<Surface>();
        private BitmapSource picture;
        private Task<BackgroundSample> measuring; // null until the picture is asked for
        private BackgroundSample sample;           // null until measured, or when it couldn't be

        private sealed class Surface
        {
            public Border Border;
            public FrameworkElement Scope; // whose resources carry the text colors
            public IReadOnlyDictionary<string, Color> Colors;
            public object PlainBackground; // the local value, or UnsetValue for a style's
            public bool Glass;
        }

        public PageTint(Panel layer)
        {
            this.layer = layer;
            layer.SizeChanged += (s, e) => Refresh();
        }

        // `scope` gets the surface's text colors on glass (its own Resources); `colors` are CardTint's set for it.
        public void Add(Border border, FrameworkElement scope, IReadOnlyDictionary<string, Color> colors)
        {
            Surface surface = new Surface { Border = border, Scope = scope, Colors = colors, PlainBackground = border.ReadLocalValue(Border.BackgroundProperty) };
            surfaces.Add(surface);
            border.SizeChanged += (s, e) => Apply(surface);
            border.IsVisibleChanged += (s, e) => Apply(surface);
            // A move without a size change of its own (the rows above it grew) is a size change of something it's in.
            border.Loaded += (s, e) =>
            {
                for (DependencyObject d = VisualTreeHelper.GetParent(border); d is FrameworkElement parent; d = VisualTreeHelper.GetParent(d))
                {
                    if (parent == layer.Parent)
                        break;
                    parent.SizeChanged -= ParentResized;
                    parent.SizeChanged += ParentResized;
                }
                Apply(surface);
            };
            void ParentResized(object sender, SizeChangedEventArgs e) => Apply(surface);
        }

        public void Show(BitmapSource shown)
        {
            if (shown == picture)
                return;
            picture = shown;
            sample = null;
            measuring = null;
            Refresh();
        }

        // Sampled only while the tint is on (turned on later, it's sampled then).
        private void Measure()
        {
            BitmapSource shown = picture;
            measuring = BackgroundSample.For(shown);
            if (measuring.IsCompleted)
                sample = measuring.Result; // measured before: no flash of the plain look
            else
                measuring.ContinueWith(t => layer.Dispatcher.BeginInvoke(new Action(() => Measured(shown, t.Result))), TaskScheduler.Default);
        }

        private void Measured(BitmapSource measured, BackgroundSample result)
        {
            if (measured != picture)
                return; // another background since
            sample = result;
            Refresh();
        }

        public void Refresh()
        {
            if (CardTint.Enabled && picture != null && measuring == null)
                Measure();
            foreach (Surface surface in surfaces)
                Apply(surface);
        }

        private void Apply(Surface surface)
        {
            Border border = surface.Border;
            if (!CardTint.Enabled || sample == null || picture == null)
            {
                Plain(surface);
                return;
            }
            if (!border.IsVisible || border.ActualWidth < 1 || border.ActualHeight < 1 || layer.ActualWidth < 1 || !border.IsDescendantOf(layer.Parent))
                return; // drawn when it's shown (SizeChanged)

            Size layerSize = new Size(layer.ActualWidth, layer.ActualHeight);
            Point origin = border.TranslatePoint(new Point(0, 0), layer);
            Rect bounds = new Rect(0, 0, border.ActualWidth, border.ActualHeight);
            var (brightest, mean) = sample.Under(new Rect(origin, bounds.Size), layerSize);
            TintLook look = CardTint.Pick(brightest, mean, PageBackground.FilmOpacity);

            // Frosted copy where the picture lies on the page, the page's own film (it follows the slider by itself),
            // then the tint; the border's rounded corners clip it.
            Rect shownAt = sample.Shown(layerSize);
            shownAt.Offset(-origin.X, -origin.Y);
            DrawingGroup glass = new DrawingGroup { ClipGeometry = new RectangleGeometry(bounds) };
            glass.Children.Add(new ImageDrawing(sample.Frosted, shownAt));
            glass.Children.Add(new GeometryDrawing(PageBackground.Film, null, new RectangleGeometry(bounds)));
            glass.Children.Add(new GeometryDrawing(new SolidColorBrush(look.Color) { Opacity = look.Opacity }, null, new RectangleGeometry(bounds)));
            border.Background = new DrawingBrush(glass)
            {
                Stretch = Stretch.Fill,
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = bounds,
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = bounds
            };

            if (!surface.Glass)
            {
                foreach (KeyValuePair<string, Color> color in surface.Colors)
                {
                    SolidColorBrush brush = new SolidColorBrush(color.Value);
                    brush.Freeze();
                    surface.Scope.Resources[color.Key] = brush;
                }
                surface.Glass = true;
            }
            Looks.AddOrUpdate(border, new StrongBox<TintLook>(look));
        }

        // Exactly the look without the tint: the border's own background back, the text colors from App.xaml again.
        private static void Plain(Surface surface)
        {
            Looks.Remove(surface.Border);
            if (!surface.Glass)
                return;
            if (surface.PlainBackground == DependencyProperty.UnsetValue)
                surface.Border.ClearValue(Border.BackgroundProperty);
            else
                surface.Border.SetValue(Border.BackgroundProperty, surface.PlainBackground);
            foreach (string key in surface.Colors.Keys)
                surface.Scope.Resources.Remove(key);
            surface.Glass = false;
        }

        public static TintLook? ShownOn(Border border) => Looks.TryGetValue(border, out StrongBox<TintLook> look) ? look.Value : null;
    }
}
