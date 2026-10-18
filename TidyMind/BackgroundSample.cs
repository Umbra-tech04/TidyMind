using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TidyMind
{
    // A memory page's background picture, measured once: a small blurred copy (the frosted glass under a tinted
    // surface, CardTint) and, on a 16 × 9 grid over it, the colors in each part. Made off the UI thread the first time
    // a picture is shown and kept with that decoded picture (a new picture is a new decode, so a new sample), so a
    // resize only maps the surfaces onto the grid again.
    public sealed class BackgroundSample
    {
        public const int Columns = 16;
        public const int Rows = 9;
        private const int FrostedSide = 64; // the blurred copy's long side, in pixels; drawn stretched, it reads as a blur

        private static readonly ConditionalWeakTable<BitmapSource, Task<BackgroundSample>> Cache =
            new ConditionalWeakTable<BitmapSource, Task<BackgroundSample>>();

        // The measuring itself (picture, the page color under transparent parts); the harness swaps in one that fails.
        internal static Func<BitmapSource, Color, BackgroundSample> Measurer = Measure;

        private readonly Color[] brightest; // per cell, channel by channel, a one-pixel ring around the cell included
        private readonly Color[] mean;

        private BackgroundSample(BitmapSource frosted, double aspect, Color[] brightest, Color[] mean)
        {
            Frosted = frosted;
            Aspect = aspect;
            this.brightest = brightest;
            this.mean = mean;
        }

        public BitmapSource Frosted { get; }
        public double Aspect { get; } // the picture's width / height (the blurred copy's own size is rounded)

        // The picture's sample, measured once per decoded picture. Null (in the task) when it can't be measured; the
        // surfaces then keep their plain look. Call on the UI thread: the page color is read here.
        public static Task<BackgroundSample> For(BitmapSource picture)
        {
            if (picture == null || !picture.IsFrozen)
                return Task.FromResult<BackgroundSample>(null);
            Color page = ((SolidColorBrush)Application.Current.FindResource("BgMain")).Color;
            lock (Cache)
            {
                if (!Cache.TryGetValue(picture, out Task<BackgroundSample> task))
                {
                    Func<BitmapSource, Color, BackgroundSample> measure = Measurer;
                    task = Task.Run(() =>
                    {
                        try { return measure(picture, page); }
                        catch (Exception) { return null; } // any failure: the plain look, nothing to tell the user
                    });
                    Cache.Add(picture, task);
                }
                return task;
            }
        }

        // Area-averages the picture (over the page color where it's transparent) into the small copy, blurs that with
        // two 3-pixel box passes, then reads the grid off the blurred copy: that is what a tinted surface shows.
        public static BackgroundSample Measure(BitmapSource picture, Color page)
        {
            BitmapSource bgra = picture.Format == PixelFormats.Bgra32 ? picture : new FormatConvertedBitmap(picture, PixelFormats.Bgra32, null, 0);
            int width = bgra.PixelWidth, height = bgra.PixelHeight;
            if (width < 1 || height < 1)
                throw new ArgumentException("an empty picture");
            byte[] pixels = new byte[width * height * 4];
            bgra.CopyPixels(pixels, width * 4, 0);

            int fw = width >= height ? FrostedSide : Math.Max(1, (int)Math.Round(FrostedSide * (double)width / height));
            int fh = width >= height ? Math.Max(1, (int)Math.Round(FrostedSide * (double)height / width)) : FrostedSide;
            double[] r = new double[fw * fh], g = new double[fw * fh], b = new double[fw * fh];
            int[] count = new int[fw * fh];
            for (int y = 0; y < height; y++)
            {
                int row = y * fh / height * fw;
                for (int x = 0; x < width; x++)
                {
                    int i = (y * width + x) * 4, cell = row + x * fw / width;
                    double alpha = pixels[i + 3] / 255.0;
                    b[cell] += pixels[i] * alpha + page.B * (1 - alpha);
                    g[cell] += pixels[i + 1] * alpha + page.G * (1 - alpha);
                    r[cell] += pixels[i + 2] * alpha + page.R * (1 - alpha);
                    count[cell]++;
                }
            }
            for (int i = 0; i < count.Length; i++)
            {
                int n = Math.Max(1, count[i]);
                r[i] /= n; g[i] /= n; b[i] /= n;
            }
            for (int pass = 0; pass < 2; pass++)
                foreach (double[] channel in new[] { r, g, b })
                    BoxBlur(channel, fw, fh);

            byte[] frosted = new byte[fw * fh * 4];
            Color[] samples = new Color[fw * fh];
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] = Color.FromRgb(ToByte(r[i]), ToByte(g[i]), ToByte(b[i]));
                frosted[i * 4] = samples[i].B;
                frosted[i * 4 + 1] = samples[i].G;
                frosted[i * 4 + 2] = samples[i].R;
                frosted[i * 4 + 3] = 255;
            }
            BitmapSource copy = BitmapSource.Create(fw, fh, 96, 96, PixelFormats.Bgra32, null, frosted, fw * 4);
            copy.Freeze();

            Color[] brightest = new Color[Columns * Rows], mean = new Color[Columns * Rows];
            for (int cy = 0; cy < Rows; cy++)
            {
                int y0 = cy * fh / Rows, y1 = Math.Max(y0, (int)Math.Ceiling((cy + 1) * fh / (double)Rows) - 1);
                for (int cx = 0; cx < Columns; cx++)
                {
                    int x0 = cx * fw / Columns, x1 = Math.Max(x0, (int)Math.Ceiling((cx + 1) * fw / (double)Columns) - 1);
                    byte maxR = 0, maxG = 0, maxB = 0;
                    double sumR = 0, sumG = 0, sumB = 0;
                    int n = 0;
                    // The ring: a stretched copy blends each pixel with its neighbours, so they count as "under" too.
                    for (int y = Math.Max(0, y0 - 1); y <= Math.Min(fh - 1, y1 + 1); y++)
                    {
                        for (int x = Math.Max(0, x0 - 1); x <= Math.Min(fw - 1, x1 + 1); x++)
                        {
                            Color c = samples[y * fw + x];
                            maxR = Math.Max(maxR, c.R); maxG = Math.Max(maxG, c.G); maxB = Math.Max(maxB, c.B);
                            if (y < y0 || y > y1 || x < x0 || x > x1)
                                continue;
                            sumR += c.R; sumG += c.G; sumB += c.B;
                            n++;
                        }
                    }
                    brightest[cy * Columns + cx] = Color.FromRgb(maxR, maxG, maxB);
                    mean[cy * Columns + cx] = Color.FromRgb(ToByte(sumR / n), ToByte(sumG / n), ToByte(sumB / n));
                }
            }
            return new BackgroundSample(copy, (double)width / height, brightest, mean);
        }

        // Where the picture lies in a layer of this size: UniformToFill, centred, as PageBackground draws it.
        public Rect Shown(Size layer)
        {
            double scale = Math.Max(layer.Width / Aspect, layer.Height);
            double w = Aspect * scale, h = scale;
            return new Rect((layer.Width - w) / 2, (layer.Height - h) / 2, w, h);
        }

        // The picture under `area` (in the layer's coordinates): its brightest color, channel by channel, and the
        // average of the covered cells.
        public (Color Brightest, Color Mean) Under(Rect area, Size layer)
        {
            Rect shown = Shown(layer);
            int c0 = Cell((area.Left - shown.X) / shown.Width, Columns, false), c1 = Cell((area.Right - shown.X) / shown.Width, Columns, true);
            int r0 = Cell((area.Top - shown.Y) / shown.Height, Rows, false), r1 = Cell((area.Bottom - shown.Y) / shown.Height, Rows, true);
            byte maxR = 0, maxG = 0, maxB = 0;
            double sumR = 0, sumG = 0, sumB = 0;
            int n = 0;
            for (int cy = r0; cy <= Math.Max(r0, r1); cy++)
            {
                for (int cx = c0; cx <= Math.Max(c0, c1); cx++)
                {
                    Color top = brightest[cy * Columns + cx], avg = mean[cy * Columns + cx];
                    maxR = Math.Max(maxR, top.R); maxG = Math.Max(maxG, top.G); maxB = Math.Max(maxB, top.B);
                    sumR += avg.R; sumG += avg.G; sumB += avg.B;
                    n++;
                }
            }
            return (Color.FromRgb(maxR, maxG, maxB), Color.FromRgb(ToByte(sumR / n), ToByte(sumG / n), ToByte(sumB / n)));
        }

        // A cell index from a position across the picture (0..1); an end edge belongs to the cell before it.
        private static int Cell(double at, int cells, bool end)
        {
            double scaled = Math.Clamp(at, 0, 1) * cells;
            int cell = end ? (int)Math.Ceiling(scaled) - 1 : (int)Math.Floor(scaled);
            return Math.Clamp(cell, 0, cells - 1);
        }

        private static void BoxBlur(double[] channel, int w, int h)
        {
            double[] line = new double[Math.Max(w, h)];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                    line[x] = (channel[y * w + Math.Max(0, x - 1)] + channel[y * w + x] + channel[y * w + Math.Min(w - 1, x + 1)]) / 3;
                Array.Copy(line, 0, channel, y * w, w);
            }
            for (int x = 0; x < w; x++)
            {
                for (int y = 0; y < h; y++)
                    line[y] = (channel[Math.Max(0, y - 1) * w + x] + channel[y * w + x] + channel[Math.Min(h - 1, y + 1) * w + x]) / 3;
                for (int y = 0; y < h; y++)
                    channel[y * w + x] = line[y];
            }
        }

        private static byte ToByte(double value) => (byte)Math.Clamp(Math.Round(value), 0, 255);
    }
}
