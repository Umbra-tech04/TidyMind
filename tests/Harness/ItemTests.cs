using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TidyMind;

// Harness items <data> <shotsFolder>: the item window every collection item opens, for every type. A vehicle with
// much in it and one with nothing, a garment with and without a picture, at a wide, a very wide and a narrow size, in
// English, Hungarian and Serbian, with a record open and a record's dialog: screenshots to look at, and checks that
// nothing is clipped.
static partial class Program
{
    // ---- Items to show ------------------------------------------------------------------------

    static Entity Vehicle(string name, int order = 0) => new Entity
    {
        Id = Guid.NewGuid(), EntityType = EntityTypes.Vehicle, Name = name, Order = order, CreatedDate = DateTime.Now
    };

    static Entity Garment(string name, int order = 0) => new Entity
    {
        Id = Guid.NewGuid(), EntityType = EntityTypes.Clothing, Name = name, Order = order, CreatedDate = DateTime.Now, Sizes = new List<SizeQuantity>()
    };

    static EntityRecord Document(string type, DateTime? validUntil, int? remindDays, string name = null)
    {
        EntityRecord document = new EntityRecord { Id = Guid.NewGuid() };
        document.Set(VehicleDocuments.TypeKey, type);
        document.Set(VehicleDocuments.NameKey, name);
        document.Set(VehicleDocuments.ValidUntilKey, Values.FromDate(validUntil));
        document.Set(VehicleDocuments.RemindKey, remindDays?.ToString());
        return document;
    }

    static EntityRecord Entry(string type, DateTime date, string cost = null, string work = null)
    {
        EntityRecord entry = new EntityRecord { Id = Guid.NewGuid() };
        entry.Set(VehicleService.TypeKey, type);
        entry.Set(VehicleService.DateKey, Values.FromDate(date));
        entry.Set("Cost", cost);
        entry.Set("Work", work);
        return entry;
    }

    static EntityRecord Node(string name, params EntityRecord[] under)
    {
        EntityRecord node = new EntityRecord { Id = Guid.NewGuid() };
        node.Set("Name", name);
        node.Children = under.Length > 0 ? under.ToList() : null;
        return node;
    }

    static readonly Color[] PicturePalette =
    {
        Color.FromRgb(0xC0, 0x39, 0x2B), Color.FromRgb(0x27, 0xAE, 0x60), Color.FromRgb(0x2E, 0x7B, 0xC4),
        Color.FromRgb(0xE6, 0x7E, 0x22), Color.FromRgb(0x8E, 0x44, 0xAD), Color.FromRgb(0x16, 0xA0, 0x85)
    };

    // A picture of `width` x `height` in one of the palette's colors with a white circle (a stand-in for a photo).
    static void ColorPicture(string path, int number, int width = 240, int height = 160)
    {
        DrawingVisual visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(PicturePalette[number % PicturePalette.Length]), null, new Rect(0, 0, width, height));
            double r = Math.Min(width, height) / 4.0 + number % 5 * 4;
            dc.DrawEllipse(Brushes.White, null, new Point(width / 2.0, height / 2.0), r, r);
        }
        RenderTargetBitmap bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        PngBitmapEncoder encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (FileStream output = File.Create(path))
            encoder.Save(output);
    }

    // A vehicle with something on every page (not stored).
    static Entity SampleVehicle()
    {
        Entity car = Vehicle("Škoda Octavia – „Đurđa”");
        car.VehicleKind = "Car";
        car.Plate = "NS-123-ĆŽ";
        car.Year = 2017;
        car.OdometerKm = 152300;
        car.Notes = "Téli gumik a pincében. A hátsó ablaktörlő néha akad.";
        car.Details = new Dictionary<string, string>
        {
            ["Make"] = "Škoda", ["Model"] = "Octavia", ["Edition"] = "III RS", ["Fuel"] = "Diesel", ["Power"] = "135", ["Gearbox"] = "Automatic",
            ["Drive"] = "Front-wheel", ["Displacement"] = "1968", ["EngineCode"] = "CUPA", ["Vin"] = "TMBJJ7NE0F0123456", ["BodyType"] = "Estate"
        };
        VehicleSchema.Documents.Write(car, new List<EntityRecord>
        {
            Document("Inspection", DateTime.Today.AddDays(12), 7),
            Document("Vignette", DateTime.Today.AddDays(-2), null, "Ausztria"),
            Document("Insurance", DateTime.Today.AddDays(200), 30, "Allianz")
        });
        EntityRecord summer = VehicleSchema.WheelSets.New(car);
        summer.Set("Name", "Nyári");
        summer.Set("Season", "Summer");
        summer.Set("TyreSize", "225/40 R18 92Y");
        summer.Set("RimSize", "8J x 18 ET49");
        summer.Set("SetDistance", "18400");
        VehicleSchema.WheelSets.Write(car, new List<EntityRecord> { summer });
        VehicleSchema.WheelSets.SetCurrent(car, summer.Id);
        EntityRecord disc = Node("Féktárcsa");
        disc.Set("Brand", "ATE");
        disc.Set("PartNumber", "24.0125-0186.1");
        disc.Specs = new List<SpecLine> { new SpecLine { Name = "Méret", Value = "312x25 mm" }, new SpecLine { Name = "Nyomaték", Value = "45 Nm" } };
        VehicleSchema.Parts.Write(car, new List<EntityRecord> { Node("Fékek", Node("Első fékek", disc)), Node("Motor"), Node("Futómű") });
        EntityRecord service = Entry("Service", DateTime.Today.AddDays(-40), "85000", "Olajcsere, szűrők");
        service.Set("Odometer", "150000");
        service.Set("Where", "Bosch Car Service");
        service.Set("NextDueOdometer", "152900");
        VehicleSchema.ServiceLog.Write(car, new List<EntityRecord>
        {
            service,
            Entry("Repair", DateTime.Today.AddDays(-420), "20000", "Izzócsere"),
            Entry("Fee", DateTime.Today.AddDays(-10), "15000", "Pályamatrica")
        });
        car.Logs["Fuel"] = new List<Dictionary<string, string>>
        {
            new Dictionary<string, string> { ["Date"] = Values.FromDate(DateTime.Today.AddDays(-30)), ["Liters"] = "45.2", ["Price"] = "27000", ["Km"] = "151000" },
            new Dictionary<string, string> { ["Date"] = Values.FromDate(DateTime.Today.AddDays(-5)), ["Liters"] = "41.8", ["Price"] = "25100", ["Km"] = "151700" }
        };
        return car;
    }

    static Entity SampleGarment()
    {
        Entity coat = Garment("Gyapjúkabát (őszi)");
        coat.Brand = "Massimo Dutti";
        coat.Color = "teveszín";
        coat.Material = "Wool";
        coat.Season = "Spring/Autumn";
        coat.PurchasePrice = "89 000 Ft";
        coat.Notes = "Vegytisztítás évente egyszer.";
        coat.Sizes = new List<SizeQuantity>
        {
            new SizeQuantity { Size = "M", Condition = "Good", Quantity = 1 },
            new SizeQuantity { Size = "L", Condition = "New", Quantity = 2 }
        };
        return coat;
    }

    // ---- Nothing runs off the side ---------------------------------------------------------------------

    // Elements WPF cuts at the edge of the room they were given because they need more than that (the layout clip is
    // narrower than the element): a box row that doesn't fit its column, a button pushed out of its card. Text that
    // wraps or trims itself, and scroll viewers' content, are never cut this way.
    static IEnumerable<string> CutOff(DependencyObject root)
    {
        foreach (FrameworkElement e in Descendants(root).OfType<FrameworkElement>())
        {
            if (!e.IsVisible || e.ActualWidth <= 0 || e is ScrollContentPresenter || e is Window)
                continue;
            Geometry clip = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutClip(e);
            if (clip != null && clip.Bounds.Width + 1 < e.RenderSize.Width)
                yield return e.GetType().Name + (string.IsNullOrEmpty(AutomationProperties.GetName(e)) ? "" : " \"" + AutomationProperties.GetName(e) + "\"")
                    + " needs " + e.RenderSize.Width.ToString("0") + " px, has " + clip.Bounds.Width.ToString("0");
        }
    }

    // ---- Switching pages: what stays still ----------------------------------------------------------

    // Where the pieces are on the screen (window coordinates) and the page's scroll state, for one moment.
    sealed record TabState(string Page, double Hero, double Title, double Nav, double Content, double Offset, double Extent, bool Bar, double Width);

    static TabState StateOf(ItemWindow w, string page)
    {
        ScrollViewer scroll = Field<ScrollViewer>(w, "PageScroll");
        double At(FrameworkElement e) => e.TransformToAncestor(w).Transform(new Point(0, 0)).Y;
        return new TabState(page, At(Field<ContentControl>(w, "Hero")), At(Field<TextBox>(w, "NameText")), At(Field<ListBox>(w, "SectionList")),
            At(Field<StackPanel>(w, "Page")), scroll.VerticalOffset, scroll.ExtentHeight,
            scroll.ComputedVerticalScrollBarVisibility == Visibility.Visible, Field<Grid>(w, "Column").ActualWidth);
    }

    // The window scrolled to `offset`, then every page chosen in turn, each one's state taken.
    static async Task<List<TabState>> SwitchPages(ItemWindow w, double offset, string[] pages, string shots = null)
    {
        ScrollViewer scroll = Field<ScrollViewer>(w, "PageScroll");
        w.SelectPage("Overview");
        await Wait(300);
        scroll.ScrollToVerticalOffset(offset);
        await Wait(300);
        List<TabState> states = new List<TabState> { StateOf(w, "start") };
        if (shots != null)
            RenderWindow(w, shots + "-0-Overview");
        foreach (string page in pages)
        {
            w.SelectPage(page);
            await Wait(300);
            states.Add(StateOf(w, page));
            if (shots != null)
                RenderWindow(w, shots + "-" + (states.Count - 1) + "-" + page);
        }
        return states;
    }

    // ---- The window, opened on its own (not as a dialog) ------------------------------------------

    static ItemWindow ShowItem(MainWindow main, Entity item, double width, double height)
    {
        ItemWindow window = new ItemWindow(item, new ItemWindow.Host()) { Owner = main, Width = width, Height = height, WindowStartupLocation = WindowStartupLocation.Manual, Left = 10, Top = 10 };
        window.Show();
        return window;
    }

    static async Task Shot(ItemWindow window, string name, string page = null)
    {
        if (page != null)
            window.SelectPage(page);
        await Wait(400);
        // A page: scrolled down to it (the pictures and title block are in the first shot of each).
        if (page != null && page != "Overview")
        {
            ScrollViewer scroll = Field<ScrollViewer>(window, "PageScroll");
            scroll.ScrollToVerticalOffset(Field<Grid>(window, "Body").TranslatePoint(new Point(0, 0), (UIElement)scroll.Content).Y - 16);
        }
        await Wait(300);
        RenderWindow(window, name);
        List<string> clipped = Clipped(window).ToList();
        Check(clipped.Count == 0, name + ": nothing clipped" + (clipped.Count > 0 ? ": " + string.Join("; ", clipped.Take(5)) : ""));
    }

    // The dialog about to open is looked at (nothing cut off, a shot if named) and closed.
    static void InspectDialog(MainWindow main, string what, string shot = null)
    {
        _ = main.Dispatcher.BeginInvoke(new Action(async () =>
        {
            EditDialog dialog = null;
            for (int i = 0; i < 50 && dialog == null; i++)
            {
                await Wait(100);
                dialog = Application.Current.Windows.OfType<EditDialog>().FirstOrDefault();
            }
            if (dialog == null)
            {
                Check(false, what + ": the dialog opened");
                return;
            }
            await Wait(300);
            if (shot != null)
                RenderWindow(dialog, shot);
            List<string> cut = CutOff(dialog).Concat(Clipped(dialog)).ToList();
            Check(cut.Count == 0, what + ": nothing cut off" + (cut.Count > 0 ? ": " + string.Join("; ", cut.Take(4)) : ""));
            dialog.Close();
        }), DispatcherPriority.Background);
    }

    // Every form of the item window, at the narrowest the window goes and wider, in each language.
    static async Task ItemForms(MainWindow main, Entity car)
    {
        foreach (string language in new[] { Loc.English, Loc.Hungarian, Loc.SerbianLatin })
        {
            Loc.Switch(language);
            foreach (double width in new[] { 560.0, 760.0, 1100.0 })
            {
                ItemWindow w = ShowItem(main, car, width, 900);
                foreach (TabDefinition tab in EntityTypes.Tabs(EntityTypes.Vehicle))
                {
                    w.SelectPage(tab.Key);
                    await Wait(250);
                    foreach (FieldsSection section in Sections(w))
                    {
                        section.BeginEdit();
                        await Wait(150);
                        if (section.Definition.Key == "Engine" && (language != Loc.English ? width == 1100 : width != 760))
                        {
                            Field<ScrollViewer>(w, "PageScroll").ScrollToVerticalOffset(section.TranslatePoint(new Point(0, 0), (UIElement)Field<ScrollViewer>(w, "PageScroll").Content).Y - 24);
                            await Wait(200);
                            RenderWindow(w, "forms-" + (language == Loc.SerbianLatin ? "sr" : language) + "-01-specs-engine-edit-" + width);
                        }
                        List<string> cut = CutOff(w).Concat(Clipped(w)).ToList();
                        Check(cut.Count == 0, $"{language} {width}: {tab.Key}/{section.Definition.Key} edited: nothing cut off" + (cut.Count > 0 ? ": " + string.Join("; ", cut.Take(4)) : ""));
                        section.CancelEdit();
                    }
                }
                w.Close();
            }

            ItemWindow window = ShowItem(main, car, 1100, 900);
            foreach (RecordDefinition records in EntityTypes.Records(EntityTypes.Vehicle))
            {
                InspectDialog(main, $"{language} {records.Key}: new", language == Loc.English ? "forms-en-02-" + records.Key + "-new" : null);
                Invoke(window, "OpenRecord", records, null, null);
                EntityRecord first = records.Read(car).FirstOrDefault();
                if (first == null)
                    continue;
                InspectDialog(main, $"{language} {records.Key}: edit", language == Loc.English ? "forms-en-03-" + records.Key + "-edit" : null);
                Invoke(window, "OpenRecord", records, first, null);
            }
            foreach (LogDefinition log in EntityTypes.Tabs(EntityTypes.Vehicle).SelectMany(t => t.Logs))
            {
                InspectDialog(main, $"{language} log {log.Key}");
                Invoke(window, "OpenLog", log);
            }
            window.Close();

            EntityForm form = new EntityForm(car) { Width = 480, Left = 10, Top = 10, WindowStartupLocation = WindowStartupLocation.Manual };
            form.Show();
            await Wait(400);
            List<string> formCut = CutOff(form).Concat(Clipped(form)).ToList();
            Check(formCut.Count == 0, $"{language}: the item form: nothing cut off" + (formCut.Count > 0 ? ": " + string.Join("; ", formCut.Take(4)) : ""));
            form.Close();
        }
        Loc.Switch(Loc.English);
    }

    // Every page chosen in turn from several scroll positions, wide and narrow: the pictures, the title block and the page
    // list stay exactly where they are on the screen, the content starts where it did (right under the list's top), the
    // scroll offset is the same, and the scroll bar and the column's width never change. Scrolled further down than the
    // page list, the page goes up to where the list starts.
    static async Task ItemTabSwitching(MainWindow main, Entity car)
    {
        Console.WriteLine("Switching pages");
        string[] pages = { "Specs", "Wheels", "Parts", "Documents", "Service", "Overview" };
        foreach (double width in new[] { 1200.0, 640.0 })
        {
            foreach (double offset in new[] { 0.0, 180.0, 400.0 })
            {
                ItemWindow w = ShowItem(main, car, width, 820);
                List<TabState> states = await SwitchPages(w, offset, pages, width == 1200 && offset == 180 ? "switch" : null);
                TabState first = states[0];
                bool still = states.All(s => Math.Abs(s.Hero - first.Hero) < 0.5 && Math.Abs(s.Title - first.Title) < 0.5 && Math.Abs(s.Nav - first.Nav) < 0.5
                    && Math.Abs(s.Content - first.Content) < 0.5 && Math.Abs(s.Offset - first.Offset) < 0.5 && s.Bar == first.Bar && Math.Abs(s.Width - first.Width) < 0.5);
                Check(still && first.Bar, $"width {width}, scrolled to {first.Offset:0}: all {pages.Length} pages leave the pictures, title, page list, scroll offset, bar and column width alone"
                    + (still ? "" : ": " + string.Join(" | ", states.Select(s => $"{s.Page} hero {s.Hero:0} nav {s.Nav:0} content {s.Content:0} offset {s.Offset:0} bar {s.Bar}"))));
                w.Close();
            }
        }

        // Further down than the page list: the new page starts under it (its top at the top of the window), whichever page.
        ItemWindow far = ShowItem(main, car, 1200, 820);
        far.SelectPage("Overview");
        await Wait(300);
        ScrollViewer scroll = Field<ScrollViewer>(far, "PageScroll");
        scroll.ScrollToVerticalOffset(scroll.ScrollableHeight);
        await Wait(300);
        List<TabState> down = new List<TabState>();
        foreach (string page in new[] { "Service", "Wheels", "Specs" })
        {
            far.SelectPage(page);
            await Wait(300);
            down.Add(StateOf(far, page));
        }
        Check(down.All(s => Math.Abs(s.Content) < 0.5), "scrolled to the very end: a page picked starts right under the page list, at the top of the window (" + string.Join(", ", down.Select(s => s.Content.ToString("0"))) + ")");
        far.Close();
    }

    // ---- Clothing sizes: a table, no rows to open --------------------------------------------------

    static async Task ScrollEnd(ItemWindow w)
    {
        Field<ScrollViewer>(w, "PageScroll").ScrollToBottom();
        await Wait(250);
    }

    static Button IconIn(DependencyObject root, string name) =>
        Descendants(root).OfType<Button>().First(b => AutomationProperties.GetName(b) == name);

    // The garment's sizes: every one always on view with its condition and pieces, - and + save at once and keep the
    // keyboard, Add and Edit open a line in the table, Delete asks first. Nothing opens and closes.
    static async Task ItemSizes(MainWindow main, Entity coat)
    {
        Console.WriteLine("Clothing sizes");
        List<Entity> saved = new List<Entity>();
        ItemWindow w = new ItemWindow(coat, new ItemWindow.Host { Save = e => { saved.Add(e); return true; } })
            { Owner = main, Width = 1000, Height = 900, WindowStartupLocation = WindowStartupLocation.Manual, Left = 10, Top = 10 };
        w.Show();
        await Wait(500);
        CompactRecords table() => Descendants(Field<StackPanel>(w, "Page")).OfType<CompactRecords>().Single();
        List<string> Lines() => Descendants(table()).OfType<Border>().Select(b => AutomationProperties.GetName(b)).Where(n => n.Length > 0).ToList();
        int Pieces(string size) => saved.Last().Sizes.First(s => s.Size == size).Quantity;

        Check(!Descendants(w).OfType<RecordRow>().Any() && table() != null, "the sizes are a table, not rows to open");
        Check(Lines().SequenceEqual(new[] { "M", "L" }), "every size is on view: " + string.Join(", ", Lines()));
        Check(Descendants(table()).OfType<TextBlock>().Any(t => t.Text == Loc.T("Entity.Records.Sizes.Count")) && Descendants(table()).OfType<TextBlock>().Any(t => t.Text == Loc.T("Entity.Field.Condition")),
            "its columns: size, condition, pieces");
        Check(CutOff(w).Count() == 0 && Clipped(w).Count() == 0, "nothing cut off");
        await ScrollEnd(w);
        RenderWindow(w, "sizes-en-01-table");

        // - and +: saved at once, the keyboard stays on the button, a size can't go below none.
        Button more = IconIn(Descendants(table()).OfType<Border>().First(b => AutomationProperties.GetName(b) == "M"), Loc.T("Entity.Records.Sizes.More"));
        more.Focus();
        ClickNow(more);
        await Wait(300);
        Check(saved.Count == 1 && Pieces("M") == 2 && saved[0].Sizes.Count == 2, "+ on M: saved at once, M has 2");
        Check(Keyboard.FocusedElement is Button b && AutomationProperties.GetName(b) == Loc.T("Entity.Records.Sizes.More")
              && ((Panel)b.Parent).Children.OfType<TextBlock>().Single().Text == "2", "  the keyboard is on M's + again, beside the new count");
        Button fewer(string size) => IconIn(Descendants(table()).OfType<Border>().First(x => AutomationProperties.GetName(x) == size), Loc.T("Entity.Records.Sizes.Fewer"));
        ClickNow(fewer("M"));
        await Wait(250);
        ClickNow(fewer("M"));
        await Wait(250);
        Check(Pieces("M") == 0 && !fewer("M").IsEnabled, "- twice: M has none, and - stops there");

        // Add: a line in the table.
        Button add = Descendants(w).OfType<Button>().First(x => x.Content as string == "+ " + Loc.T("Entity.Records.Sizes.Add"));
        ClickNow(add);
        await Wait(300);
        List<FieldEditor> editors = Field<List<FieldEditor>>(table(), "editors");
        Check(table().IsEditing && editors.Count == 3 && Lines().Count == 2, "+ Add size: a line opens in the table (" + editors.Count + " inputs), no dialog");
        Field<TextBox>(editors[0], "box").Text = "XL";
        Field<TextBox>(editors[2], "box").Text = "3";
        await ScrollEnd(w);
        RenderWindow(w, "sizes-en-02-adding");
        Check(CutOff(w).Count() == 0, "  nothing cut off while adding");
        ClickNow(Descendants(table()).OfType<Button>().First(x => x.Content as string == Loc.T("Common.Save")));
        await Wait(300);
        Check(saved.Last().Sizes.Count == 3 && saved.Last().Sizes[2].Size == "XL" && saved.Last().Sizes[2].Quantity == 3 && saved.Last().Sizes[2].Condition == "New"
              && Lines().SequenceEqual(new[] { "M", "L", "XL" }), "Save: XL, 3 pieces, in New condition (the default), added at the end");

        // Edit: the same line, in place; Enter saves, Esc leaves.
        ClickNow(IconIn(Descendants(table()).OfType<Border>().First(x => AutomationProperties.GetName(x) == "L"), Loc.T("Common.Edit")));
        await Wait(300);
        editors = Field<List<FieldEditor>>(table(), "editors");
        Check(table().IsEditing && Field<TextBox>(editors[0], "box").Text == "L", "Edit on L: its line opens in place");
        Field<TextBox>(editors[2], "box").Text = "5";
        PressKey(Field<TextBox>(editors[2], "box"), Key.Enter);
        await Wait(300);
        Check(Pieces("L") == 5 && saved.Last().Sizes.Count == 3 && !table().IsEditing, "Enter saves it: L has 5");
        ClickNow(IconIn(Descendants(table()).OfType<Border>().First(x => AutomationProperties.GetName(x) == "L"), Loc.T("Common.Edit")));
        await Wait(300);
        int before = saved.Count;
        w.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(w), 0, Key.Escape) { RoutedEvent = Keyboard.KeyDownEvent });
        await Wait(300);
        Check(!table().IsEditing && saved.Count == before, "Esc leaves the line, nothing saved");

        // Delete: asked first (the harness answers Yes), then gone.
        ClickNow(IconIn(Descendants(table()).OfType<Border>().First(x => AutomationProperties.GetName(x) == "XL"), Loc.T("Common.Delete")));
        await Wait(500);
        Check(saved.Last().Sizes.Select(s => s.Size).SequenceEqual(new[] { "M", "L" }) && Lines().SequenceEqual(new[] { "M", "L" }), "Delete XL: asked, then gone");

        // The same inside a narrow window and in the other languages, nothing cut.
        foreach (string language in new[] { Loc.English, Loc.Hungarian, Loc.SerbianLatin })
        {
            Loc.Switch(language);
            w.Width = 560;
            await Wait(500);
            ClickNow(IconIn(Descendants(table()).OfType<Border>().First(x => AutomationProperties.GetName(x) == "M"), Loc.T("Common.Edit")));
            await Wait(300);
            List<string> cut = CutOff(w).Concat(Clipped(w)).ToList();
            Check(cut.Count == 0, language + " at 560 px, editing a size: nothing cut off" + (cut.Count > 0 ? ": " + string.Join("; ", cut.Take(4)) : ""));
            await ScrollEnd(w);
            RenderWindow(w, "sizes-" + (language == Loc.SerbianLatin ? "sr" : language) + "-03-editing-narrow");
            w.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(w), 0, Key.Escape) { RoutedEvent = Keyboard.KeyDownEvent });
            await Wait(200);
        }
        Loc.Switch(Loc.English);
        w.Close();
    }

    // ---- A record's row: Edit and Delete on the row itself ----------------------------------------------

    static async Task ItemRowActions(MainWindow main, Entity car)
    {
        Console.WriteLine("Edit and Delete on a record's row");
        ItemWindow w = ShowItem(main, car, 1100, 900);
        w.SelectPage("Documents");
        await Wait(300);
        RecordRow row = Descendants(w).OfType<RecordRow>().First();
        Button edit = IconIn(row, Loc.T("Common.Edit")), delete = IconIn(row, Loc.T("Common.Delete"));
        UIElement icons = (UIElement)edit.Parent;
        Check(icons == delete.Parent && icons.Visibility == Visibility.Hidden && !row.IsOpen, "closed, pointer away: the icons are not drawn, and they have their room (the row doesn't change size)");
        double height = row.ActualHeight;
        Descendants(row).OfType<Button>().First().Focus(); // the row's own button, as a Tab would
        await Wait(150);
        Check(icons.Visibility == Visibility.Visible && Math.Abs(row.ActualHeight - height) < 0.5, "the keyboard on the row shows Edit and Delete, the row's height the same");
        Check(edit.Focusable && delete.Focusable && edit.ToolTip as string == Loc.T("Common.Edit"), "  both can be reached with the keyboard and carry their names");
        InspectDialog(main, "Edit on the row", null);
        ClickNow(edit);
        await Wait(500);
        Check(!row.IsOpen, "its click opens the record's form and doesn't open the row");
        Check(!Descendants(row).OfType<Button>().Any(b => b.Content as string == Loc.T("Common.Edit")), "opened, the row doesn't list Edit and Delete again");
        w.Close();
    }

    static async Task ItemShots(MainWindow main)
    {
        await Wait(1200);
        GuidedTour.Current?.Stop(TourEnd.Skipped);
        StartSelectWatcher(); // answers (Yes, OK) and logs message boxes

        string work = Path.Combine(Path.GetDirectoryName(AppPaths.DataFolder), "items-work");
        Directory.CreateDirectory(work);
        string[] pictures = Enumerable.Range(0, 4).Select(i =>
        {
            string own = Path.Combine(work, "p" + i + ".png");
            ColorPicture(own, i, i == 1 ? 600 : 1200, i == 1 ? 800 : 800);
            return ImageStore.Items.Import(own);
        }).ToArray();

        Entity car = SampleVehicle();
        EntityImages.SetNames(car, pictures.Take(3).ToList());
        await ItemForms(main, car);
        await ItemTabSwitching(main, car);
        await ItemRowActions(main, car);
        Entity bare = Vehicle("Új autó");
        Entity coat = SampleGarment();
        EntityImages.SetNames(coat, new List<string> { pictures[3] });
        Entity plain = Garment("Fehér póló");
        plain.Color = "fehér";
        await ItemSizes(main, coat);

        foreach (string language in new[] { Loc.English, Loc.Hungarian, Loc.SerbianLatin })
        {
            Loc.Switch(language);
            ItemWindow w = ShowItem(main, car, 1200, 900);
            await Shot(w, "items-" + language + "-01-vehicle-overview", "Overview");
            if (language == Loc.English)
            {
                await Shot(w, "items-en-02-vehicle-specs", "Specs");
                await Shot(w, "items-en-03-vehicle-service", "Service");
                RecordRow first = Descendants(w).OfType<RecordRow>().First();
                Invoke(w, "Toggle", first);
                await Shot(w, "items-en-03b-vehicle-service-open");
                _ = main.Dispatcher.BeginInvoke(new Action(async () =>
                {
                    await Wait(600);
                    Window dialog = Application.Current.Windows.OfType<EditDialog>().First();
                    RenderWindow(dialog, "items-en-12-record-dialog");
                    dialog.Close();
                }));
                Invoke(w, "OpenRecord", VehicleSchema.ServiceLog, first.Record, null);
                await Shot(w, "items-en-04-vehicle-parts", "Parts");
                await Shot(w, "items-en-05-vehicle-documents", "Documents");
                w.Width = 1900;
                w.Height = 1000;
                await Shot(w, "items-en-06-vehicle-very-wide", "Overview");
            }
            w.Width = 620;
            w.Height = 820;
            await Shot(w, "items-" + language + "-07-vehicle-narrow", language == Loc.English ? "Service" : "Documents");
            w.Close();

            w = ShowItem(main, coat, 1000, 860);
            await Shot(w, "items-" + language + "-08-garment");
            w.Close();
        }
        Loc.Switch(Loc.English);

        ItemWindow empty = ShowItem(main, bare, 1100, 800);
        await Shot(empty, "items-en-09-vehicle-empty", "Overview");
        await Shot(empty, "items-en-10-vehicle-empty-specs", "Specs");
        empty.Close();
        ItemWindow simple = ShowItem(main, plain, 900, 700);
        await Shot(simple, "items-en-11-garment-no-picture");
        simple.Close();
    }
}
