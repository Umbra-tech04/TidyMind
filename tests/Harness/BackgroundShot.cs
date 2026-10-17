using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TidyMind;

// Harness bgshot <data> <shotsFolder>: a "Garage" collection with a few items on a busy picture (bright sky, dark
// ground, strong colors), as a screenshot at the default background visibility and at the lowest and highest
// slider values, to judge how vivid the picture is and whether the header, tabs and cards stay readable.
static partial class Program
{
    static async Task BackgroundShots(MainWindow main)
    {
        await Wait(1200);
        GuidedTour.Current?.Stop(TourEnd.Skipped);
        main.Width = 1240;
        main.Height = 820;

        string picture = Path.Combine(Path.GetTempPath(), "tidymind-harness-garage.png");
        SaveTestPicture(picture);
        Profile garage = AddMemory(main, "Garage", ProfileType.Collection);
        garage.BackgroundType = BackgroundFill.Image;
        garage.BackgroundImagePath = ImageStore.Backgrounds.Import(picture);
        ProfileManager.TrySaveProfiles(Profiles(main));
        File.WriteAllText(MemoryFiles.Items(garage), System.Text.Json.JsonSerializer.Serialize(new List<Entity>
        {
            new Entity { Id = Guid.NewGuid(), EntityType = EntityTypes.Vehicle, Name = "Family car", Order = 1, CreatedDate = DateTime.Now },
            new Entity { Id = Guid.NewGuid(), EntityType = EntityTypes.Vehicle, Name = "Bicycle", Order = 2, CreatedDate = DateTime.Now },
            new Entity { Id = Guid.NewGuid(), EntityType = EntityTypes.Clothing, Name = "Winter jacket", Order = 3, CreatedDate = DateTime.Now }
        }));
        Check(garage.BackgroundImagePath != null, "the test picture was taken in as a background");

        Invoke(main, "NavigateTo", garage);
        await Wait(1500); // the picture is decoded off the UI thread
        Check(PageBackground.ImageVisibility == PageBackground.DefaultImageVisibility,
            "the default visibility: " + PageBackground.ImageVisibility + "%");
        FrameworkElement plate = (FrameworkElement)((FrameworkElement)Content(main)).FindName("HeaderPlate");
        Check(plate.IsVisible, "the header and tabs sit on their plate");
        RenderWindow(main, "bg-1-default");

        foreach (int percent in new[] { PageBackground.MinImageVisibility, 100 })
        {
            PageBackground.ImageVisibility = percent;
            await Wait(300);
            RenderWindow(main, "bg-2-" + percent);
        }
        PageBackground.ImageVisibility = PageBackground.DefaultImageVisibility;

        // Empty tabs: the small "nothing here yet" card, on the picture and on a plain page.
        Profile shed = AddMemory(main, "Shed", ProfileType.Collection);
        shed.BackgroundType = BackgroundFill.Image;
        shed.BackgroundImagePath = garage.BackgroundImagePath;
        ProfileManager.TrySaveProfiles(Profiles(main));
        Invoke(main, "NavigateTo", shed);
        await Wait(1200);
        CheckEmptyCard(main, "Collection.Empty", "bg-3-empty-collection-picture");
        Profile plans = AddMemory(main, "Plans", ProfileType.Project);
        Invoke(main, "NavigateTo", plans);
        await Wait(800);
        CheckEmptyCard(main, "Project.Empty", "bg-4-empty-project-plain");
    }

    static void CheckEmptyCard(MainWindow main, string key, string shot)
    {
        FrameworkElement view = (FrameworkElement)Content(main);
        System.Windows.Controls.TextBlock text = (System.Windows.Controls.TextBlock)view.FindName("EmptyState");
        FrameworkElement card = (FrameworkElement)System.Windows.Media.VisualTreeHelper.GetParent(System.Windows.Media.VisualTreeHelper.GetParent(text));
        Check(text.IsVisible && text.Text == Loc.T(key) && card.ActualHeight < 160 && card.ActualWidth <= 440,
            key + ": a small card (" + card.ActualWidth.ToString("0") + " × " + card.ActualHeight.ToString("0") + " px)");
        RenderWindow(main, shot);
    }

    // Sky, sun, a dark ground and bright shapes: the hard case for text on top.
    static void SaveTestPicture(string path)
    {
        const int width = 1600, height = 1000;
        DrawingVisual visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x6F, 0xB7, 0xF2), Color.FromRgb(0xFF, 0xF1, 0xC9), 90), null,
                new Rect(0, 0, width, height * 0.55));
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x3B, 0x2E, 0x24), Color.FromRgb(0x12, 0x10, 0x0E), 90), null,
                new Rect(0, height * 0.55, width, height * 0.45));
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xFF, 0xD2, 0x3F)), null, new Point(1250, 180), 110, 110);
            Random random = new Random(7);
            Color[] colors = { Color.FromRgb(0xE7, 0x4C, 0x3C), Color.FromRgb(0x2E, 0xCC, 0x71), Color.FromRgb(0x34, 0x98, 0xDB),
                Color.FromRgb(0xF3, 0x9C, 0x12), Color.FromRgb(0x9B, 0x59, 0xB6) };
            for (int i = 0; i < 40; i++)
                dc.DrawRoundedRectangle(new SolidColorBrush(colors[i % colors.Length]), null,
                    new Rect(random.Next(0, width), random.Next(300, height), random.Next(40, 220), random.Next(30, 140)), 12, 12);
        }
        RenderTargetBitmap bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        PngBitmapEncoder png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        using (FileStream file = File.Create(path))
            png.Save(file);
    }
}
