using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TidyMind;

// Harness tint <data> <shotsFolder>: Adaptive card tint (CardTint). The sampling on made-up pictures (all white, all
// black, half and half) and the tint's contrast for them at three slider settings; then the real pages: a light and a
// dark photo (the header plate and the empty-tab card as frosted glass, measured on the drawn glass), the slider, a
// resize (no new sampling), the Settings switch, off drawing the window exactly as before, a picture that can't be
// measured, and a page without a picture. Screenshots: tint-1-light, tint-2-dark, tint-3-off.
static partial class Program
{
    static int measured;

    static async Task TintScenarios(MainWindow main)
    {
        await Wait(1200);
        GuidedTour.Current?.Stop(TourEnd.Skipped);
        main.Width = 1240;
        main.Height = 820;
        Func<BitmapSource, Color, BackgroundSample> measure = BackgroundSample.Measurer;
        BackgroundSample.Measurer = (picture, page) => { measured++; return measure(picture, page); };

        TintSampling();
        await TintPages(main);

        BackgroundSample.Measurer = measure;
    }

    // ---- The sampling and the pick, on made-up pictures -------------------------------------------------------

    static void TintSampling()
    {
        Color page = ((SolidColorBrush)Application.Current.FindResource("BgMain")).Color;
        BitmapSource white = Picture(320, 180, x => Colors.White), black = Picture(320, 180, x => Colors.Black);
        BitmapSource half = Picture(320, 180, x => x < 160 ? Colors.White : Colors.Black);

        BackgroundSample whiteSample = BackgroundSample.Measure(white, page), blackSample = BackgroundSample.Measure(black, page);
        BackgroundSample halfSample = BackgroundSample.Measure(half, page);
        Check(whiteSample.Frosted.IsFrozen && whiteSample.Frosted.PixelWidth == 64 && whiteSample.Frosted.PixelHeight == 36,
            "the blurred copy: 64 × 36 px for a 16:9 picture, frozen");
        Check(Cells(whiteSample).All(c => CardTint.Luminance(c) > 0.99), "all white: every one of the 16 × 9 cells reads white");
        Check(Cells(blackSample).All(c => CardTint.Luminance(c) < 0.01), "all black: every cell reads black");
        List<Color> halves = Cells(halfSample);
        bool leftWhite = true, rightBlack = true, edgeBetween = true;
        for (int i = 0; i < halves.Count; i++)
        {
            int column = i % BackgroundSample.Columns;
            double l = CardTint.Luminance(halves[i]);
            if (column <= 6) leftWhite &= l > 0.99;
            else if (column >= 9) rightBlack &= l < 0.01;
            else edgeBetween &= l > 0.01 && l < 0.99; // the blur softens the edge across the two middle cells
        }
        Check(leftWhite && rightBlack && edgeBetween, "half and half: columns 0–6 white, 9–15 black, the blurred edge between");

        // Transparent parts count as the page under them.
        BitmapSource clear = Picture(320, 180, x => Color.FromArgb(0, 0, 0, 0));
        Check(Cells(BackgroundSample.Measure(clear, page)).All(c => Math.Abs(CardTint.Luminance(c) - CardTint.Luminance(page)) < 0.01),
            "a transparent picture reads as the page color");

        Size layer = new Size(1600, 900);
        var areas = new (string Name, Rect Area)[]
        {
            ("the whole page", new Rect(layer)), ("the left half", new Rect(0, 0, 800, 900)), ("the right half", new Rect(800, 0, 800, 900)),
            ("a header strip", new Rect(14, 12, 1572, 160))
        };
        foreach (var (name, sample) in new[] { ("all white", whiteSample), ("all black", blackSample), ("half and half", halfSample) })
        {
            foreach (int visibility in new[] { 100, PageBackground.DefaultImageVisibility, PageBackground.MinImageVisibility })
            {
                double film = 1 - visibility / 100.0;
                List<string> failed = new List<string>();
                foreach (var (areaName, area) in areas)
                {
                    var (brightest, mean) = sample.Under(area, layer);
                    TintLook look = CardTint.Pick(brightest, mean, film);
                    Color surface = Blend(look.Color, look.Opacity, Blend(Colors.White, film, brightest));
                    double text = Ratio(CardTint.Text, surface), sub = Ratio(CardTint.TextSub, surface);
                    if (text < 4.5 || sub < 4.5 || look.Opacity < CardTint.MinOpacity || look.Opacity > CardTint.MaxOpacity)
                        failed.Add(areaName + " (" + sub.ToString("0.0") + ":1 at " + look.Opacity.ToString("0.00") + ")");
                }
                Check(failed.Count == 0, name + " at " + visibility + "%: text and secondary text at 4.5:1 or more on the glass, opacity within "
                    + CardTint.MinOpacity + "–" + CardTint.MaxOpacity + (failed.Count > 0 ? ": " + string.Join(", ", failed) : ""));
            }
        }

        // Darker and more opaque over light, a lighter, fainter tint over dark.
        double defaultFilm = 1 - PageBackground.DefaultImageVisibility / 100.0;
        TintLook onWhite = CardTint.Pick(Colors.White, Colors.White, defaultFilm), onBlack = CardTint.Pick(Colors.Black, Colors.Black, defaultFilm);
        Check(onWhite.Opacity > onBlack.Opacity && CardTint.Luminance(onWhite.Color) < CardTint.Luminance(onBlack.Color),
            "over white the tint is darker and stronger (" + onWhite.Opacity.ToString("0.00") + ") than over black (" + onBlack.Opacity.ToString("0.00") + ")");
        Check(onBlack.Opacity == CardTint.MinOpacity, "over black the tint stays at its lightest (" + CardTint.MinOpacity + ")");
    }

    // The mean of each cell, read through Under with a layer the picture's own shape.
    static List<Color> Cells(BackgroundSample sample)
    {
        Size layer = new Size(sample.Aspect * 900, 900);
        List<Color> cells = new List<Color>();
        for (int row = 0; row < BackgroundSample.Rows; row++)
            for (int column = 0; column < BackgroundSample.Columns; column++)
                cells.Add(sample.Under(new Rect((column + 0.5) * layer.Width / BackgroundSample.Columns, (row + 0.5) * layer.Height / BackgroundSample.Rows, 0.1, 0.1), layer).Mean);
        return cells;
    }

    static BitmapSource Picture(int width, int height, Func<int, Color> colorAt)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                Color c = colorAt(x);
                int i = (y * width + x) * 4;
                pixels[i] = c.B; pixels[i + 1] = c.G; pixels[i + 2] = c.R; pixels[i + 3] = c.A;
            }
        BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    // The harness's own blend and WCAG contrast, so the check doesn't lean on the code it checks.
    static Color Blend(Color top, double opacity, Color under)
    {
        byte Mix(byte t, byte u) => (byte)Math.Round(t * opacity + u * (1 - opacity));
        return Color.FromRgb(Mix(top.R, under.R), Mix(top.G, under.G), Mix(top.B, under.B));
    }

    static double Ratio(Color a, Color b)
    {
        static double Lin(byte v) { double s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        static double Lum(Color c) => 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
        double la = Lum(a), lb = Lum(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    // ---- The real pages ------------------------------------------------------------------------------------

    static async Task TintPages(MainWindow main)
    {
        string lightFile = Path.Combine(Path.GetTempPath(), "tidymind-harness-light.png");
        string darkFile = Path.Combine(Path.GetTempPath(), "tidymind-harness-dark.png");
        SaveTintPicture(lightFile, light: true);
        SaveTintPicture(darkFile, light: false);

        // Off first: the window as it was before this feature, to compare with once it's turned off again.
        CardTint.Enabled = false;
        Profile bright = AddMemory(main, "Bright day", ProfileType.Collection);
        bright.BackgroundType = BackgroundFill.Image;
        bright.BackgroundImagePath = ImageStore.Backgrounds.Import(lightFile);
        ProfileManager.TrySaveProfiles(Profiles(main));
        Invoke(main, "NavigateTo", bright);
        await Wait(1500);
        FrameworkElement view = (FrameworkElement)Content(main);
        Border plate = (Border)view.FindName("HeaderPlate"), empty = (Border)view.FindName("EmptyCard");
        Grid page = (Grid)view.FindName("Page");
        object plainPlate = plate.ReadLocalValue(Border.BackgroundProperty);
        Check(plate.IsVisible && empty.IsVisible && measured == 0, "off: the picture page with its plate and empty card, nothing sampled");
        Check(plainPlate == Application.Current.FindResource("BgPlate") && empty.ReadLocalValue(Border.BackgroundProperty) == DependencyProperty.UnsetValue
            && !page.Resources.Contains("PlateText") && CardTint.Shown(plate) == null, "off: the plate and the card keep their own backgrounds and text colors");
        byte[] before = WindowPixels(main);

        // On: live, once the picture is sampled (off the UI thread).
        CardTint.Enabled = true;
        await Until(() => CardTint.Shown(plate) != null && CardTint.Shown(empty) != null, 3000);
        Check(measured == 1, "on: the picture sampled once");
        Check(plate.Background is DrawingBrush && empty.Background is DrawingBrush, "on: the header plate and the empty card are frosted glass");
        Check(((SolidColorBrush)page.Resources["PlateText"]).Color == CardTint.Text && ((SolidColorBrush)empty.Resources["PlateBody"]).Color == CardTint.Text,
            "on: the header's and the card's text turn light");
        TintLook lightLook = CardTint.Shown(plate).Value;
        CheckGlass(plate, "the header plate on the light photo");
        CheckGlass(empty, "the empty card on the light photo");
        RenderWindow(main, "tint-1-light");

        // A resize maps the surfaces again without sampling the picture again.
        double plateWidth = plate.ActualWidth;
        main.Width = 1040;
        await Wait(600);
        Check(plate.ActualWidth < plateWidth && ((DrawingBrush)plate.Background).Viewbox.Width == plate.ActualWidth && measured == 1,
            "a resize redraws the glass at the new size (" + plate.ActualWidth.ToString("0") + " px) without sampling again");
        main.Width = 1240;
        await Wait(600);

        // Off again: exactly the old look, pixel for pixel.
        CardTint.Enabled = false;
        await Wait(400);
        Check(plate.ReadLocalValue(Border.BackgroundProperty) == plainPlate && empty.ReadLocalValue(Border.BackgroundProperty) == DependencyProperty.UnsetValue
            && !page.Resources.Contains("PlateText") && !empty.Resources.Contains("PlateBody"), "off again: the plain backgrounds and text colors are back");
        byte[] after = WindowPixels(main);
        Check(before.Length == after.Length && before.SequenceEqual(after), "off again: the window is drawn exactly as before the tint was turned on");
        RenderWindow(main, "tint-3-off");

        // The Settings switch: on by default, writes settings.json, applies at once.
        CardTint.Enabled = true;
        SettingsWindow settings = new SettingsWindow(null, owner => false) { Owner = main };
        settings.Show();
        await Wait(500);
        CheckBox tintSwitch = (CheckBox)settings.FindName("CardTintSwitch");
        Check(tintSwitch.IsChecked == true, "Settings > Appearance: Adaptive card tint is on");
        ClickCheck(tintSwitch);
        await Wait(300);
        Check(!CardTint.Enabled && AppSettings.Load().AdaptiveCardTint == false && CardTint.Shown(plate) == null,
            "the switch off: saved as false, the open page plain at once");
        ClickCheck(tintSwitch);
        await Wait(300);
        Check(CardTint.Enabled && AppSettings.Load().AdaptiveCardTint == null && CardTint.Shown(plate) != null,
            "the switch on: the default again (not written), the open page frosted at once");
        settings.Close();

        // A dark photo with items: a lighter, fainter tint.
        Profile night = AddMemory(main, "Night drive", ProfileType.Collection);
        night.BackgroundType = BackgroundFill.Image;
        night.BackgroundImagePath = ImageStore.Backgrounds.Import(darkFile);
        ProfileManager.TrySaveProfiles(Profiles(main));
        File.WriteAllText(MemoryFiles.Items(night), System.Text.Json.JsonSerializer.Serialize(new List<Entity>
        {
            new Entity { Id = Guid.NewGuid(), EntityType = EntityTypes.Vehicle, Name = "Family car", Order = 1, CreatedDate = DateTime.Now },
            new Entity { Id = Guid.NewGuid(), EntityType = EntityTypes.Vehicle, Name = "Motorbike", Order = 2, CreatedDate = DateTime.Now },
            new Entity { Id = Guid.NewGuid(), EntityType = EntityTypes.Clothing, Name = "Rain jacket", Order = 3, CreatedDate = DateTime.Now }
        }));
        Invoke(main, "NavigateTo", night);
        view = (FrameworkElement)Content(main);
        plate = (Border)view.FindName("HeaderPlate");
        await Until(() => CardTint.Shown(plate) != null, 3000);
        TintLook darkLook = CardTint.Shown(plate) ?? default;
        Check(darkLook.Opacity < lightLook.Opacity && CardTint.Luminance(darkLook.Color) > CardTint.Luminance(lightLook.Color),
            "the dark photo gets a lighter, fainter tint (" + darkLook.Opacity.ToString("0.00") + ") than the light one (" + lightLook.Opacity.ToString("0.00") + ")");
        CheckGlass(plate, "the header plate on the dark photo");
        RenderWindow(main, "tint-2-dark");

        // The slider: a lighter page (more film) gets a stronger tint, still readable.
        PageBackground.ImageVisibility = PageBackground.MinImageVisibility;
        await Wait(300);
        Check(CardTint.Shown(plate)?.Opacity > darkLook.Opacity, "at " + PageBackground.MinImageVisibility + "% the tint is stronger ("
            + CardTint.Shown(plate)?.Opacity.ToString("0.00") + ")");
        CheckGlass(plate, "the header plate on the dark photo at " + PageBackground.MinImageVisibility + "%");
        PageBackground.ImageVisibility = PageBackground.DefaultImageVisibility;
        await Wait(300);

        // A picture that can't be sampled: the plain look, nothing said.
        Func<BitmapSource, Color, BackgroundSample> working = BackgroundSample.Measurer;
        BackgroundSample.Measurer = (picture, color) => throw new InvalidOperationException("harness: sampling fails");
        string otherFile = Path.Combine(Path.GetTempPath(), "tidymind-harness-light2.png");
        SaveTintPicture(otherFile, light: true, seed: 3);
        Profile broken = AddMemory(main, "Unsampled", ProfileType.Collection);
        broken.BackgroundType = BackgroundFill.Image;
        broken.BackgroundImagePath = ImageStore.Backgrounds.Import(otherFile);
        ProfileManager.TrySaveProfiles(Profiles(main));
        Invoke(main, "NavigateTo", broken);
        await Wait(1500);
        view = (FrameworkElement)Content(main);
        plate = (Border)view.FindName("HeaderPlate");
        Check(plate.IsVisible && CardTint.Shown(plate) == null && plate.Background == Application.Current.FindResource("BgPlate")
            && !((Grid)view.FindName("Page")).Resources.Contains("PlateText"), "sampling fails: the plate keeps the plain look");
        Check(Application.Current.Windows.OfType<Window>().All(w => w == main), "sampling fails: no message");
        BackgroundSample.Measurer = working;

        // No picture: as without the feature.
        Profile plain = AddMemory(main, "Plain", ProfileType.Project);
        plain.BackgroundType = BackgroundFill.Color;
        plain.BackgroundColor = "#2E7BC4";
        ProfileManager.TrySaveProfiles(Profiles(main));
        Invoke(main, "NavigateTo", plain);
        await Wait(800);
        view = (FrameworkElement)Content(main);
        plate = (Border)view.FindName("HeaderPlate");
        empty = (Border)view.FindName("EmptyCard");
        Check(!plate.IsVisible && CardTint.Shown(empty) == null && empty.ReadLocalValue(Border.BackgroundProperty) == DependencyProperty.UnsetValue,
            "a color background: no plate, the empty card as before");
    }

    // The glass as drawn (its own brush on a border its size): the brightest pixel inside the rounded corners keeps
    // both text colors at 4.5:1 or more.
    static void CheckGlass(Border surface, string what)
    {
        int w = (int)surface.ActualWidth, h = (int)surface.ActualHeight;
        Border copy = new Border { Width = w, Height = h, Background = surface.Background };
        copy.Measure(new Size(w, h));
        copy.Arrange(new Rect(0, 0, w, h));
        RenderTargetBitmap bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(copy);
        byte[] pixels = new byte[w * h * 4];
        bitmap.CopyPixels(pixels, w * 4, 0);
        Color brightest = Colors.Black;
        double top = -1;
        int inset = (int)Math.Ceiling(surface.CornerRadius.TopLeft) + 1;
        for (int y = inset; y < h - inset; y++)
            for (int x = inset; x < w - inset; x++)
            {
                int i = (y * w + x) * 4;
                Color c = Color.FromRgb(pixels[i + 2], pixels[i + 1], pixels[i]);
                double l = CardTint.Luminance(c);
                if (l > top) { top = l; brightest = c; }
            }
        double text = Ratio(CardTint.Text, brightest), sub = Ratio(CardTint.TextSub, brightest);
        Check(text >= 4.5 && sub >= 4.5, what + ": text " + text.ToString("0.0") + ":1, secondary text " + sub.ToString("0.0") + ":1 on its brightest spot");
    }

    static byte[] WindowPixels(Window window)
    {
        window.UpdateLayout();
        FrameworkElement root = (FrameworkElement)VisualTreeHelper.GetChild(window, 0);
        RenderTargetBitmap bitmap = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        byte[] pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels;
    }

    static async Task Until(Func<bool> done, int ms)
    {
        for (int waited = 0; waited < ms && !done(); waited += 100)
            await Wait(100);
    }

    // A light photo (pale sky, haze, bright clouds) or a dark one (a night sky over a city with a few lights).
    static void SaveTintPicture(string path, bool light, int seed = 11)
    {
        const int width = 1600, height = 1000;
        DrawingVisual visual = new DrawingVisual();
        Random random = new Random(seed);
        using (DrawingContext dc = visual.RenderOpen())
        {
            if (light)
            {
                dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0xB9, 0xDC, 0xF7), Color.FromRgb(0xFF, 0xFB, 0xF0), 90), null, new Rect(0, 0, width, height));
                for (int i = 0; i < 18; i++)
                    dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF)), null,
                        new Point(random.Next(0, width), random.Next(0, height / 2)), random.Next(80, 260), random.Next(30, 90));
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x9C, 0xC6, 0x8E)), null, new Rect(0, height * 0.8, width, height * 0.2));
            }
            else
            {
                dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x0B, 0x10, 0x20), Color.FromRgb(0x23, 0x1C, 0x30), 90), null, new Rect(0, 0, width, height));
                for (int i = 0; i < 30; i++)
                {
                    int w = random.Next(60, 180), h = random.Next(150, 450);
                    dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x14, 0x17, 0x22)), null, new Rect(random.Next(0, width), height - h, w, h));
                }
                for (int i = 0; i < 60; i++)
                    dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xFF, 0xC8, 0x6B)), null, new Point(random.Next(0, width), random.Next(height / 2, height)), 3, 3);
            }
        }
        RenderTargetBitmap bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        PngBitmapEncoder png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        using (FileStream file = File.Create(path))
            png.Save(file);
    }
}
