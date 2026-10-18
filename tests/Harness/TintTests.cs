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
// measured, and a page without a picture. Screenshots: tint-1-light (a car photo), tint-2-dark (a night photo).
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
                List<string> plateFailed = new List<string>(), quietFailed = new List<string>(), cardFailed = new List<string>();
                foreach (var (areaName, area) in areas)
                {
                    PictureArea under = sample.Under(area, layer);

                    // The header plate: dark text at 4.5:1 on its darkest spot, light glass.
                    TintLook plate = CardTint.PickPlate(under, film);
                    Color plateDark = Blend(plate.Color, plate.Opacity, Blend(Colors.White, film, under.Darkest));
                    double text = Ratio(CardTint.PlateText, plateDark), sub = Ratio(CardTint.PlateTextSub, plateDark);
                    if (text < 4.5 || sub < 4.5 || plate.Opacity < CardTint.PlateMinOpacity || plate.Opacity > CardTint.PlateMaxOpacity)
                        plateFailed.Add(areaName + " (" + sub.ToString("0.0") + ":1 at " + plate.Opacity.ToString("0.00") + ")");

                    // Never darker or more contrasty than a white card on the same spot: it only lightens the page, by
                    // less than a card does, and is never a dark block.
                    foreach (Color spot in new[] { under.Darkest, under.Brightest })
                    {
                        Color onPage = Blend(Colors.White, film, spot);
                        Color glass = Blend(plate.Color, plate.Opacity, onPage);
                        if (CardTint.Luminance(glass) + 0.001 < CardTint.Luminance(onPage) || Ratio(glass, onPage) > Ratio(Colors.White, onPage) + 0.001
                            || CardTint.Luminance(glass) < 0.45)
                            quietFailed.Add(areaName);
                    }

                    // The empty-tab card (dark glass, as before): light text at 4.5:1 on its brightest spot.
                    TintLook card = CardTint.Pick(under, film);
                    Color cardBright = Blend(card.Color, card.Opacity, Blend(Colors.White, film, under.Brightest));
                    if (Ratio(CardTint.GlassText, cardBright) < 4.5 || Ratio(CardTint.GlassTextSub, cardBright) < 4.5
                        || card.Opacity < CardTint.MinOpacity || card.Opacity > CardTint.MaxOpacity)
                        cardFailed.Add(areaName);
                }
                string at = name + " at " + visibility + "%: ";
                Check(plateFailed.Count == 0, at + "the header plate's dark text and secondary text at 4.5:1 or more, opacity within "
                    + CardTint.PlateMinOpacity + "–" + CardTint.PlateMaxOpacity + (plateFailed.Count > 0 ? ": " + string.Join(", ", plateFailed) : ""));
                Check(quietFailed.Count == 0, at + "the header plate is light and never darker or more contrasty than a white card"
                    + (quietFailed.Count > 0 ? ": " + string.Join(", ", quietFailed.Distinct()) : ""));
                Check(cardFailed.Count == 0, at + "the empty card's light text at 4.5:1 or more" + (cardFailed.Count > 0 ? ": " + string.Join(", ", cardFailed) : ""));
            }
        }

        // The plate is barely there over light pictures and stronger over dark ones, always well under the cards.
        double defaultFilm = 1 - PageBackground.DefaultImageVisibility / 100.0;
        PictureArea white1 = new PictureArea(Colors.White, Colors.White, Colors.White), black1 = new PictureArea(Colors.Black, Colors.Black, Colors.Black);
        TintLook plateOnWhite = CardTint.PickPlate(white1, defaultFilm), plateOnBlack = CardTint.PickPlate(black1, defaultFilm);
        Check(plateOnWhite.Opacity == CardTint.PlateMinOpacity && plateOnBlack.Opacity > plateOnWhite.Opacity && plateOnBlack.Opacity <= CardTint.PlateMaxOpacity,
            "the header plate: " + plateOnWhite.Opacity.ToString("0.00") + " over white, " + plateOnBlack.Opacity.ToString("0.00") + " over black");
        TintLook plateWorst = CardTint.PickPlate(black1, 0);
        Color worstGlass = Blend(plateWorst.Color, plateWorst.Opacity, Colors.Black);
        Check(Ratio(CardTint.PlateTextSub, worstGlass) >= 4.5 && plateWorst.Opacity < CardTint.PlateMaxOpacity,
            "pure black at 100%: the plate still reaches 4.5:1 under its cap (" + plateWorst.Opacity.ToString("0.00") + ")");
        TintLook onWhite = CardTint.Pick(white1, defaultFilm), onBlack = CardTint.Pick(black1, defaultFilm);
        Check(onWhite.Opacity > onBlack.Opacity && CardTint.Luminance(onWhite.Color) < CardTint.Luminance(onBlack.Color),
            "the empty card: darker and stronger over white (" + onWhite.Opacity.ToString("0.00") + ") than over black (" + onBlack.Opacity.ToString("0.00") + ")");
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
        string carFile = Path.Combine(Path.GetTempPath(), "tidymind-harness-car.png");
        string darkFile = Path.Combine(Path.GetTempPath(), "tidymind-harness-dark.png");
        SaveCarPicture(carFile);
        SaveTintPicture(darkFile, light: false);

        // Off first: the window as it was before this feature, to compare with once it's turned off again.
        CardTint.Enabled = false;
        Profile garage = AddGarage(main, "Garázs", carFile);
        Invoke(main, "NavigateTo", garage);
        await Wait(1500);
        FrameworkElement view = (FrameworkElement)Content(main);
        Border plate = (Border)view.FindName("HeaderPlate");
        Grid page = (Grid)view.FindName("Page");
        object plainPlate = plate.ReadLocalValue(Border.BackgroundProperty), plainShadow = plate.ReadLocalValue(UIElement.EffectProperty);
        Check(plate.IsVisible && measured == 0, "off: the picture page with its plate, nothing sampled");
        Check(plainPlate == Application.Current.FindResource("BgPlate") && !page.Resources.Contains("PlateTextSub") && CardTint.Shown(plate) == null,
            "off: the plate keeps its own background and text colors");
        byte[] before = WindowPixels(main);

        // On: live, once the picture is sampled (off the UI thread).
        CardTint.Enabled = true;
        await Until(() => CardTint.Shown(plate) != null, 3000);
        Check(measured == 1, "on: the picture sampled once");
        Check(plate.Background is DrawingBrush && !page.Resources.Contains("PlateText")
            && ((SolidColorBrush)page.Resources["PlateTextSub"]).Color == CardTint.PlateTextSub,
            "on: the header plate is light frosted glass; the title stays dark, the secondary text a little darker");
        Check(plate.Effect is System.Windows.Media.Effects.DropShadowEffect soft
            && soft.Opacity < ((System.Windows.Media.Effects.DropShadowEffect)plainShadow).Opacity, "on: the plate's shadow is softer than before");
        TintLook lightLook = CardTint.Shown(plate).Value;
        CheckPlate(plate, "the header plate on the car photo");
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
        Check(plate.ReadLocalValue(Border.BackgroundProperty) == plainPlate && plate.ReadLocalValue(UIElement.EffectProperty) == plainShadow
            && !page.Resources.Contains("PlateTextSub") && !page.Resources.Contains("PlateTabText"),
            "off again: the plain background, shadow and text colors are back");
        byte[] after = WindowPixels(main);
        Check(before.Length == after.Length && before.SequenceEqual(after), "off again: the window is drawn exactly as before the tint was turned on");

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

        // An empty tab on the same picture: the empty card is dark glass, as before.
        Profile shed = AddMemory(main, "Kamra", ProfileType.Collection);
        shed.BackgroundType = BackgroundFill.Image;
        shed.BackgroundImagePath = garage.BackgroundImagePath;
        ProfileManager.TrySaveProfiles(Profiles(main));
        Invoke(main, "NavigateTo", shed);
        view = (FrameworkElement)Content(main);
        Border empty = (Border)view.FindName("EmptyCard");
        await Until(() => CardTint.Shown(empty) != null, 3000);
        Check(empty.Background is DrawingBrush && ((SolidColorBrush)empty.Resources["PlateBody"]).Color == CardTint.GlassText,
            "the empty card: dark frosted glass with light text");
        CheckGlass(empty, CardTint.GlassText, CardTint.GlassTextSub, "the empty card on the car photo");

        // The dark photo with the same items: a stronger plate, still light and under its cap.
        Profile night = AddGarage(main, "Garázs (este)", darkFile);
        Invoke(main, "NavigateTo", night);
        view = (FrameworkElement)Content(main);
        plate = (Border)view.FindName("HeaderPlate");
        await Until(() => CardTint.Shown(plate) != null, 3000);
        TintLook darkLook = CardTint.Shown(plate) ?? default;
        Check(darkLook.Opacity > lightLook.Opacity && darkLook.Opacity <= CardTint.PlateMaxOpacity,
            "the dark photo: the plate a little stronger (" + darkLook.Opacity.ToString("0.00") + ") than on the car photo ("
            + lightLook.Opacity.ToString("0.00") + "), at most " + CardTint.PlateMaxOpacity);
        CheckPlate(plate, "the header plate on the dark photo");
        RenderWindow(main, "tint-2-dark");

        // The slider: at either end the plate stays readable and light.
        foreach (int percent in new[] { 100, PageBackground.MinImageVisibility })
        {
            PageBackground.ImageVisibility = percent;
            await Wait(300);
            CheckPlate(plate, "the header plate on the dark photo at " + percent + "%");
        }
        PageBackground.ImageVisibility = PageBackground.DefaultImageVisibility;
        await Wait(300);

        // A picture that can't be sampled: the plain look, nothing said.
        Func<BitmapSource, Color, BackgroundSample> working = BackgroundSample.Measurer;
        BackgroundSample.Measurer = (picture, color) => throw new InvalidOperationException("harness: sampling fails");
        string otherFile = Path.Combine(Path.GetTempPath(), "tidymind-harness-light2.png");
        SaveTintPicture(otherFile, light: true, seed: 3);
        Profile broken = AddGarage(main, "Unsampled", otherFile);
        Invoke(main, "NavigateTo", broken);
        await Wait(1500);
        view = (FrameworkElement)Content(main);
        plate = (Border)view.FindName("HeaderPlate");
        Check(plate.IsVisible && CardTint.Shown(plate) == null && plate.Background == Application.Current.FindResource("BgPlate")
            && !((Grid)view.FindName("Page")).Resources.Contains("PlateTextSub"), "sampling fails: the plate keeps the plain look");
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

    static Profile AddGarage(MainWindow main, string name, string pictureFile)
    {
        Profile garage = AddMemory(main, name, ProfileType.Collection);
        garage.BackgroundType = BackgroundFill.Image;
        garage.BackgroundImagePath = ImageStore.Backgrounds.Import(pictureFile);
        ProfileManager.TrySaveProfiles(Profiles(main));
        File.WriteAllText(MemoryFiles.Items(garage), System.Text.Json.JsonSerializer.Serialize(new List<Entity>
        {
            new Entity { Id = Guid.NewGuid(), EntityType = EntityTypes.Vehicle, Name = "Családi autó", Order = 1, CreatedDate = DateTime.Now },
            new Entity { Id = Guid.NewGuid(), EntityType = EntityTypes.Vehicle, Name = "Bicikli", Order = 2, CreatedDate = DateTime.Now },
            new Entity { Id = Guid.NewGuid(), EntityType = EntityTypes.Clothing, Name = "Téli kabát", Order = 3, CreatedDate = DateTime.Now }
        }));
        return garage;
    }

    // The header plate as drawn: dark text at 4.5:1 on every pixel, and never a dark block (every pixel light).
    static void CheckPlate(Border plate, string what) => CheckGlass(plate, CardTint.PlateText, CardTint.PlateTextSub, what, 0.45);

    // The glass as drawn (its own brush on a border its size), inside the rounded corners: both text colors at 4.5:1
    // or more on every pixel, and every pixel at least `lightest` luminance.
    static void CheckGlass(Border surface, Color textColor, Color subColor, string what, double lightest = 0)
    {
        int w = (int)surface.ActualWidth, h = (int)surface.ActualHeight;
        Border copy = new Border { Width = w, Height = h, Background = surface.Background };
        copy.Measure(new Size(w, h));
        copy.Arrange(new Rect(0, 0, w, h));
        RenderTargetBitmap bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(copy);
        byte[] pixels = new byte[w * h * 4];
        bitmap.CopyPixels(pixels, w * 4, 0);
        double text = double.MaxValue, sub = double.MaxValue, darkest = 1;
        int inset = (int)Math.Ceiling(surface.CornerRadius.TopLeft) + 1;
        for (int y = inset; y < h - inset; y++)
            for (int x = inset; x < w - inset; x++)
            {
                int i = (y * w + x) * 4;
                Color c = Color.FromRgb(pixels[i + 2], pixels[i + 1], pixels[i]);
                text = Math.Min(text, Ratio(textColor, c));
                sub = Math.Min(sub, Ratio(subColor, c));
                darkest = Math.Min(darkest, CardTint.Luminance(c));
            }
        Check(text >= 4.5 && sub >= 4.5 && darkest >= lightest, what + ": text " + text.ToString("0.0") + ":1, secondary text "
            + sub.ToString("0.0") + ":1 at worst" + (lightest > 0 ? ", darkest pixel " + darkest.ToString("0.00") : ""));
    }

    // A light-ish street photo: pale sky, light buildings, a grey road and a red car with dark windows and wheels.
    static void SaveCarPicture(string path)
    {
        const int width = 1600, height = 1000;
        DrawingVisual visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0xCF, 0xE2, 0xF2), Color.FromRgb(0xF4, 0xF1, 0xEA), 90), null, new Rect(0, 0, width, height * 0.7));
            Random random = new Random(5);
            for (int x = 0; x < width; x += 140)
            {
                int h = random.Next(140, 320);
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xC6, 0xCC, 0xD4)), null, new Rect(x, height * 0.7 - h, 120, h));
            }
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x9C, 0x9E, 0xA2)), null, new Rect(0, height * 0.7, width, height * 0.3));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE4)), null, new Rect(0, height * 0.84, width, 10));
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B)), null, new Rect(520, 640, 640, 150), 50, 50);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0xA9, 0x31, 0x25)), null, new Rect(650, 545, 360, 130), 60, 60);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x2B, 0x34, 0x40)), null, new Rect(690, 570, 130, 80), 24, 24);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x2B, 0x34, 0x40)), null, new Rect(840, 570, 130, 80), 24, 24);
            foreach (double x in new[] { 650.0, 1030.0 })
            {
                dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x18)), null, new Point(x, 790), 62, 62);
                dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xB8, 0xBC, 0xC2)), null, new Point(x, 790), 26, 26);
            }
        }
        RenderTargetBitmap bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        PngBitmapEncoder png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        using (FileStream file = File.Create(path))
            png.Save(file);
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
