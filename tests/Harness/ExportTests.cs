using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClosedXML.Excel;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using TidyMind;

// Export samples ("Harness export <dataFolder> <outFolder>"): every PDF, Word and Excel export in English, Hungarian
// and Serbian. PDF pages are rendered to PNG through Windows' own PDF renderer (not QuestPDF's), plus one grayscale
// page; the Word and Excel files are checked with the Open XML SDK validator (what Word and Excel would want to
// repair) and by their content. Render those through LibreOffice for a look.
static partial class Program
{
    static async Task ExportScenarios(MainWindow main)
    {
        await Wait(1200);
        GuidedTour.Current?.Stop(TourEnd.Skipped);

        Profile memory = AddProjectMemory(main, "Beiratkozás");
        Project longProject = LongProject();
        Project empty = new Project { Id = Guid.NewGuid(), Name = "Üres projekt", Tasks = new List<TaskItem>() };
        Project notesOnly = new Project
        {
            Id = Guid.NewGuid(), Name = "Csak jegyzet",
            NotePages = new List<NotePage> { new NotePage { Body = "Régi, formázatlan jegyzet.\nMásodik sor, beillesztett vezérlőkarakterrel:\u000B vége.\n\nÜres sor után." } }
        };
        Project allDone = new Project
        {
            Id = Guid.NewGuid(), Name = "Kész minden",
            Tasks = new List<TaskItem> { new TaskItem { Title = "Első", IsDone = true }, new TaskItem { Title = "Második", IsDone = true } }
        };
        ProjectStore.SaveOrWarn(memory, new List<Project> { longProject, empty, notesOnly, allDone });
        MemoryTodoService.TrySave(memory, new List<TodoEntry>
        {
            new TodoEntry { Id = Guid.NewGuid(), Text = "Igazolványkép (đak, čađ, ćup)", IsDone = false },
            new TodoEntry { Id = Guid.NewGuid(), Text = "Tandíj befizetve: 185 000 Ft", IsDone = true }
        });

        Profile closet = new Profile { Id = Guid.NewGuid(), Name = "Ruhásszekrény", Type = ProfileType.Collection, Color = "#27AE60", Order = Profiles(main).Count };
        Profiles(main).Add(closet);
        Profile garage = new Profile { Id = Guid.NewGuid(), Name = "Garázs", Type = ProfileType.Collection, Color = "#2D7DD2", Order = Profiles(main).Count };
        Profiles(main).Add(garage);
        // A second memory with the same name: its own heading and sheet, never merged with the first.
        Profile closet2 = new Profile { Id = Guid.NewGuid(), Name = "Ruhásszekrény", Type = ProfileType.Collection, Color = "#8E44AD", Order = Profiles(main).Count };
        Profiles(main).Add(closet2);
        ProfileManager.TrySaveProfiles(Profiles(main));
        string picture = Path.Combine(AppPaths.DataFolder, "sample-picture.png"); // a picture of the user's own, outside EntityImages
        SavePicture(picture);
        Entity coat = new Entity
        {
            Id = Guid.NewGuid(), EntityType = EntityTypes.Clothing, Name = "Kabát (őszi)", ImagePath = Path.GetFullPath(picture),
            Brand = "Žućko & Šarić", Color = "sötétkék", Material = "Wool", PurchasePrice = "24 990 Ft",
            Notes = "Vegytisztítás évente.\nCímke a belső zsebben.",
            Sizes = new List<SizeQuantity> { new SizeQuantity { Size = "M", Condition = "New", Quantity = 1 } }
        };
        Entity scarf = new Entity { Id = Guid.NewGuid(), EntityType = EntityTypes.Clothing, Name = "Sál", Sizes = new List<SizeQuantity>() };
        EntityStore.SaveOrWarn(closet, new List<Entity> { coat, scarf });
        EntityStore.SaveOrWarn(closet2, new List<Entity> { new Entity { Id = Guid.NewGuid(), EntityType = EntityTypes.Clothing, Name = "Póló", Color = "fehér", Sizes = new List<SizeQuantity>() } });
        Entity car = new Entity
        {
            Id = Guid.NewGuid(), EntityType = EntityTypes.Vehicle, Name = "Škoda Octavia", VehicleKind = "Car", Plate = "AB-123-ČĆ",
            Year = 2019, OdometerKm = 120500, InsuranceExpires = new DateTime(2027, 3, 31), InspectionExpires = new DateTime(2026, 11, 15),
            Logs = new Dictionary<string, List<Dictionary<string, string>>>
            {
                // 18 entries: as one text they would be taller than an Excel row can be.
                ["Service"] = Enumerable.Range(0, 18).Select(i => new Dictionary<string, string>
                {
                    ["Date"] = new DateTime(2026, 5, 2).AddMonths(-3 * i).ToString("yyyy-MM-dd"), ["Work"] = i % 2 == 0 ? "Olajcsere, szűrők" : "Gumicsere (téli), futómű-beállítás",
                    ["Km"] = (112000 - 4000 * i).ToString(), ["Cost"] = "45000"
                }).ToList()
            }
        };
        // Notes longer than an Excel row can show: they go on in the rows under it.
        Entity bike = new Entity
        {
            Id = Guid.NewGuid(), EntityType = EntityTypes.Vehicle, Name = "Kerékpár", VehicleKind = "Bicycle",
            Notes = string.Join("\n", Enumerable.Range(1, 60).Select(i => i + ". megjegyzés: lánc olajozása, fékbetét ellenőrzése, guminyomás ⚙"))
        };
        EntityStore.SaveOrWarn(garage, new List<Entity> { car, bike });
        DayPlanStore.Save(new DayPlanBlock { Id = Guid.NewGuid(), Date = DateTime.Today, StartMinute = 540, EndMinute = 600, Title = "Ügyintézés", Notes = "Hozd a papírokat" });
        DayPlanStore.Save(new DayPlanBlock { Id = Guid.NewGuid(), Date = DateTime.Today.AddDays(1), StartMinute = 780, EndMinute = 840, Title = "Edzés", Done = true });

        string projectsFile = MemoryFiles.Items(memory);
        byte[] before = SHA256.HashData(File.ReadAllBytes(projectsFile));

        foreach (string language in new[] { Loc.English, Loc.Hungarian, Loc.SerbianLatin })
        {
            Loc.Switch(language);
            Console.WriteLine("PDF export, " + language);
            await Sample(language + "-1-long-project", path => ExportService.ExportProject(longProject, path, ExportFormat.Pdf), 3, 99);
            await Sample(language + "-2-empty-project", path => ExportService.ExportProject(empty, path, ExportFormat.Pdf), 1, 1);
            await Sample(language + "-3-notes-only", path => ExportService.ExportProject(notesOnly, path, ExportFormat.Pdf), 1, 1);
            await Sample(language + "-4-all-done", path => ExportService.ExportProject(allDone, path, ExportFormat.Pdf), 1, 1);
            await Sample(language + "-5-memory", path => ExportService.ExportMemory(memory, path, ExportFormat.Pdf), 3, 99);
            await Sample(language + "-6-item", path => ExportService.ExportEntity(coat, path, ExportFormat.Pdf), 1, 1);
            await Sample(language + "-7-collection", path => ExportService.ExportMemory(closet, path, ExportFormat.Pdf), 1, 1);
            await Sample(language + "-8-all", path => ExportService.ExportAll(Profiles(main), path, ExportFormat.Pdf), 3, 99);

            Console.WriteLine("Word export, " + language);
            string words = WordSample(language + "-1-long-project", path => ExportService.ExportProject(longProject, path, ExportFormat.Docx));
            Check(words.Contains(Loc.F("Export.DoneGroup", 22)) && words.Contains("☐") && words.Contains("☑") && words.Contains("185 000 Ft")
                && words.Contains(Loc.T("ProjectWindow.Tasks").ToUpper(Loc.Culture)), "  section labels, checklist, Done group");
            Check(words.Contains("fénykép.jpg") && words.Contains(Loc.F("Export.FileDetails", AttachmentStore.FormatSize(3_400_000),
                new DateTime(2026, 9, 2).ToString(Loc.T("Format.ShortDate"), Loc.Culture))), "  files with size and date");
            Check(WordSample(language + "-2-empty-project", path => ExportService.ExportProject(empty, path, ExportFormat.Docx)).Contains(Loc.T("Export.Empty")),
                "  an empty project says so");
            WordSample(language + "-3-notes-only", path => ExportService.ExportProject(notesOnly, path, ExportFormat.Docx));
            WordSample(language + "-4-all-done", path => ExportService.ExportProject(allDone, path, ExportFormat.Docx));
            WordSample(language + "-5-memory", path => ExportService.ExportMemory(memory, path, ExportFormat.Docx));
            Check(WordSample(language + "-6-item", path => ExportService.ExportEntity(coat, path, ExportFormat.Docx)).Contains("<w:drawing>"),
                "  the item's picture is in");
            WordSample(language + "-7-collection", path => ExportService.ExportMemory(closet, path, ExportFormat.Docx));
            WordSample(language + "-8-all", path => ExportService.ExportAll(Profiles(main), path, ExportFormat.Docx));

            Console.WriteLine("Excel export, " + language);
            ExcelSample(language + "-1-long-project", path => ExportService.ExportProject(longProject, path, ExportFormat.Excel), book =>
            {
                IXLWorksheet tasks = book.Worksheet(Loc.T("ProjectWindow.Tasks"));
                Check(TaskTableLooksRight(tasks, 64), "  Tasks: a filterable table, header frozen and repeated, A4 one page wide, 64 rows");
                Check(tasks.Cell(5, 1).GetString() == "☑ " + Loc.T("Export.Status.Done") && tasks.Cell(5, 1).Style.Font.FontColor.Color.R > 0x60
                    && tasks.Cell(6, 1).GetString() == "☐ " + Loc.T("Export.Status.Open"), "  status words; done rows gray");
                Check(tasks.Cell(6, 2).DataType == XLDataType.Text && tasks.Cell(6, 2).GetString().Contains("185 000 Ft"), "  amounts stay text");
                Check(tasks.CellsUsed().All(c => !c.HasFormula), "  no formulas");
                IXLWorksheet files = book.Worksheet(Loc.T("ProjectWindow.Files"));
                Check(files.Cell(5, 3).DataType == XLDataType.DateTime && files.Cell(5, 3).GetDateTime() == new DateTime(2026, 9, 1), "  Files: added dates are date cells");
                IXLWorksheet text = book.Worksheet(Loc.T("Export.Sheet.Text"));
                Check(text.CellsUsed().Count() > 70, "  Description and notes: every paragraph in");
            });
            ExcelSample(language + "-2-empty-project", path => ExportService.ExportProject(empty, path, ExportFormat.Excel), book =>
                Check(book.Worksheets.Count == 1 && book.Worksheet(1).Cell(4, 1).GetString() == Loc.T("Export.Empty"), "  an empty project says so"));
            ExcelSample(language + "-3-notes-only", path => ExportService.ExportProject(notesOnly, path, ExportFormat.Excel), null);
            ExcelSample(language + "-5-memory", path => ExportService.ExportMemory(memory, path, ExportFormat.Excel), book =>
                Check(TaskTableLooksRight(book.Worksheet(Loc.T("ProjectWindow.Tasks")), 64 + 2 + 2)
                    && book.Worksheet(Loc.T("ProjectWindow.Tasks")).Cell(XlHeader, 1).GetString() == Loc.T("Export.Column.Project"), "  every list's tasks, with a project column"));
            ExcelSample(language + "-6-item", path => ExportService.ExportEntity(coat, path, ExportFormat.Excel), null);
            ExcelSample(language + "-7-collection", path => ExportService.ExportMemory(closet, path, ExportFormat.Excel), book =>
                Check(book.Worksheet(1).Cell(5, 1).GetString() == "Kabát (őszi)" && book.Worksheet(1).Row(XlHeader).CellsUsed().Any(c => c.GetString() == Loc.T("Entity.Field.PurchasePrice")),
                    "  Ruhásszekrény: a row per item, a column per field"));
            ExcelSample(language + "-9-vehicles", path => ExportService.ExportMemory(garage, path, ExportFormat.Excel), book =>
            {
                IXLWorksheet sheet = book.Worksheet(1);
                IXLWorksheet documents = book.Worksheet(VehicleSchema.Documents.Label);
                IXLCell validUntil = documents.Row(XlHeader).CellsUsed().First(c => c.GetString() == Loc.T("Entity.Field.ValidUntil"));
                Check(documents.Column(validUntil.Address.ColumnNumber).CellsUsed().Any(c => c.DataType == XLDataType.DateTime && c.GetDateTime() == new DateTime(2027, 3, 31)),
                    "  vehicle dates are date cells (the old insurance date, as a document)");
                IXLCell notes = sheet.Row(XlHeader).CellsUsed().First(c => c.GetString() == Loc.T("Entity.Field.Notes"));
                string allNotes = string.Join("\n", sheet.Column(notes.Address.ColumnNumber).CellsUsed().Where(c => c.Address.RowNumber > 5).Select(c => c.GetString()));
                Check(sheet.LastRowUsed().RowNumber() > 7 && allNotes.Contains("1. megjegyzés") && allNotes.Contains("60. megjegyzés"),
                    "  notes too long for a row go on in the rows under it, all 60 lines");
                // The old Service log rows show as service log entries.
                IXLWorksheet service = book.Worksheet(VehicleSchema.ServiceLog.Label);
                int date = service.Row(XlHeader).CellsUsed().First(c => c.GetString() == Loc.T("Entity.Field.Date")).Address.ColumnNumber;
                int odometer = service.Row(XlHeader).CellsUsed().First(c => c.GetString() == Loc.T("Entity.Field.Odometer")).Address.ColumnNumber;
                Check(service.LastRowUsed().RowNumber() - XlHeader == 18 && service.Cell(5, date).DataType == XLDataType.DateTime
                    && service.Cell(5, odometer).DataType == XLDataType.Number, "  the service log: its own table, a row per entry, dates and km typed");
            });
            ExcelSample(language + "-8-all", path => ExportService.ExportAll(Profiles(main), path, ExportFormat.Excel), book =>
            {
                Check(book.Worksheet(Loc.T("ProjectWindow.Tasks")).Cell(XlHeader, 1).GetString() == Loc.T("Export.Column.Memory"), "  Export all: a memory column");
                Check(book.Worksheet("Ruhásszekrény").LastRowUsed().RowNumber() == 6 && book.Worksheet("Ruhásszekrény (1)").Cell(5, 1).GetString() == "Póló",
                    "  two memories with the same name: two sheets");
                IXLWorksheet plan = book.Worksheet(Loc.T("Plan.Name"));
                Check(plan.Cell(5, 1).DataType == XLDataType.DateTime && plan.Cell(5, 3).GetString() == "Ügyintézés", "  Day plan: a row per block, dates as dates");
            });
        }
        Loc.Switch(Loc.English);

        Check(SHA256.HashData(File.ReadAllBytes(projectsFile)).SequenceEqual(before), "exporting didn't touch projects.json");
        SaveGray(Path.Combine(shots, "hu-1-long-project-p1.png"), Path.Combine(shots, "hu-1-long-project-p1-gray.png"));
        Console.WriteLine("samples: " + Path.GetFullPath(shots));
    }

    const int XlHeader = 4; // the header row of every table sheet

    static bool TaskTableLooksRight(IXLWorksheet sheet, int rows) =>
        sheet.AutoFilter.IsEnabled && sheet.SheetView.SplitRow == XlHeader
        && sheet.PageSetup.PaperSize == XLPaperSize.A4Paper && sheet.PageSetup.PagesWide == 1 && sheet.PageSetup.PagesTall == 0
        && sheet.PageSetup.FirstRowToRepeatAtTop == XlHeader && sheet.Row(XlHeader).Cell(1).Style.Font.Bold
        && sheet.LastRowUsed().RowNumber() - XlHeader == rows;

    // Writes one export, checks its page count and fonts, and renders its pages.
    static async Task Sample(string name, Action<string> export, int minPages, int maxPages)
    {
        string path = Path.Combine(shots, name + ".pdf");
        try
        {
            export(path);
        }
        catch (Exception e)
        {
            Check(false, name + ": export threw " + e);
            return;
        }
        int pages = await RenderPages(path, Path.Combine(shots, name));
        Check(pages >= minPages && pages <= maxPages, name + ": " + pages + " page(s)");
        // Font names are stored as plain text; QuestPDF subsets them as "ABCDEF+Name".
        List<string> fonts = Regex.Matches(Encoding.Latin1.GetString(File.ReadAllBytes(path)), "/BaseFont\\s*/(?:[A-Z]{6}\\+)?([^\\s/\\]>]+)")
            .Select(m => m.Groups[1].Value).Distinct().ToList();
        Check(fonts.Count > 0 && fonts.All(f => f.StartsWith("Lato") || f.StartsWith("SegoeUI")), name + ": fonts " + string.Join(", ", fonts));
    }

    // Writes one Word export, validates it and returns its text (document.xml), "" if it failed.
    static string WordSample(string name, Action<string> export)
    {
        string path = Path.Combine(shots, name + ".docx");
        try
        {
            export(path);
            using (WordprocessingDocument document = WordprocessingDocument.Open(path, false))
                CheckValid(name + ".docx", document);
            CheckNoLibraryNotice(name + ".docx", path);
            using (ZipArchive docx = ZipFile.OpenRead(path))
            using (StreamReader body = new StreamReader(docx.GetEntry("word/document.xml").Open()))
                return body.ReadToEnd();
        }
        catch (Exception e)
        {
            Check(false, name + ": Word export threw " + e);
            return "";
        }
    }

    // Writes one Excel export, validates it and runs `check` on it.
    static void ExcelSample(string name, Action<string> export, Action<XLWorkbook> check)
    {
        string path = Path.Combine(shots, name + ".xlsx");
        try
        {
            export(path);
            using (SpreadsheetDocument document = SpreadsheetDocument.Open(path, false))
                CheckValid(name + ".xlsx", document);
            CheckNoLibraryNotice(name + ".xlsx", path);
            using (ZipArchive zip = ZipFile.OpenRead(path))
            using (StreamReader workbook = new StreamReader(zip.GetEntry("xl/workbook.xml").Open()))
            {
                List<string> titles = Regex.Matches(workbook.ReadToEnd(), "Print_Titles\"[^>]*>([^<]*)<").Select(m => m.Groups[1].Value).ToList();
                if (titles.Count > 0)
                    Check(titles.All(t => Regex.IsMatch(t, @"!\$\d+:\$\d+$")), name + ".xlsx: header rows to repeat written absolute (" + titles[0] + ")");
            }
            using (XLWorkbook book = new XLWorkbook(path))
            {
                // 409 points is Excel's limit: a row that tall had its text cut.
                Check(book.Worksheets.All(sheet => sheet.RowsUsed().All(r => r.Height < 409)), name + ".xlsx: no row at Excel's height limit");
                check?.Invoke(book);
            }
        }
        catch (Exception e)
        {
            Check(false, name + ": Excel export threw " + e);
        }
    }

    // What Word or Excel would complain about (and offer to repair) on opening the file.
    static void CheckValid(string name, OpenXmlPackage package)
    {
        List<ValidationErrorInfo> errors = new OpenXmlValidator(FileFormatVersions.Office2019).Validate(package).ToList();
        foreach (ValidationErrorInfo error in errors.Take(5))
            Console.WriteLine("    " + error.Part?.Uri + " " + error.Path?.XPath + ": " + error.Description);
        Check(errors.Count == 0, name + " is valid Open XML (" + errors.Count + " errors)");
    }

    // No licence or watermark text from a library anywhere in the file.
    static void CheckNoLibraryNotice(string name, string path)
    {
        using (ZipArchive zip = ZipFile.OpenRead(path))
        {
            string all = string.Concat(zip.Entries.Where(e => e.FullName.EndsWith(".xml") || e.FullName.EndsWith(".rels"))
                .Select(e => new StreamReader(e.Open()).ReadToEnd()));
            Check(!Regex.IsMatch(all, "xceed|non-commercial|evaluation|watermark|licen[cs]e", RegexOptions.IgnoreCase), name + ": no library notice in it");
        }
    }

    static async Task<int> RenderPages(string pdfPath, string prefix)
    {
        Windows.Storage.StorageFile file = await Windows.Storage.StorageFile.GetFileFromPathAsync(Path.GetFullPath(pdfPath));
        Windows.Data.Pdf.PdfDocument pdf = await Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file);
        for (uint i = 0; i < pdf.PageCount; i++)
        {
            using (Windows.Data.Pdf.PdfPage page = pdf.GetPage(i))
            using (Windows.Storage.Streams.InMemoryRandomAccessStream png = new Windows.Storage.Streams.InMemoryRandomAccessStream())
            {
                await page.RenderToStreamAsync(png, new Windows.Data.Pdf.PdfPageRenderOptions { DestinationWidth = 1240 });
                using (FileStream output = File.Create(prefix + "-p" + (i + 1) + ".png"))
                    await png.AsStreamForRead().CopyToAsync(output);
            }
        }
        return (int)pdf.PageCount;
    }

    static void SaveGray(string source, string target)
    {
        BitmapImage color = new BitmapImage();
        color.BeginInit();
        color.CacheOption = BitmapCacheOption.OnLoad;
        color.UriSource = new Uri(Path.GetFullPath(source));
        color.EndInit();
        PngBitmapEncoder encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(color, PixelFormats.Gray8, null, 0)));
        using (FileStream output = File.Create(target))
            encoder.Save(output);
    }

    static void SavePicture(string path)
    {
        DrawingVisual visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x2E, 0x4A, 0x7B)), null, new System.Windows.Rect(0, 0, 200, 200));
            dc.DrawEllipse(Brushes.Gold, null, new System.Windows.Point(100, 100), 60, 60);
        }
        RenderTargetBitmap bitmap = new RenderTargetBitmap(200, 200, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        PngBitmapEncoder encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (FileStream output = File.Create(path))
            encoder.Save(output);
    }

    // 64 tasks (some long, amounts as typed, every Hungarian and Serbian letter), a formatted description, two
    // notebook pages (one with a paragraph longer than a page), files.
    static Project LongProject()
    {
        string[] samples =
        {
            "Űrlap kitöltése: Árvíztűrő tükörfúrógép", "Tandíj: 185 000 Ft (első részlet)", "Ćevapčići za žurku — đak, čađ, šešir",
            "Orvosi igazolás leadása a tanulmányi osztályon, a nyitvatartási időben (hétfő és szerda délelőtt), két fénymásolattal és az eredeti példánnyal együtt, előre egyeztetett időpontban",
            "Kollégiumi díj 1.234,50 € / hó", "ŐŰ nagybetűs teszt: ŐRÜLT ŰRHAJÓ", "Đorđe Šćepanović — ključ od sobe", "Könyvek: 3× jegyzet, 12 500 Ft"
        };
        List<TaskItem> tasks = new List<TaskItem>();
        for (int i = 0; i < 64; i++)
            tasks.Add(new TaskItem { Title = (i + 1) + ". " + samples[i % samples.Length], IsDone = i % 3 == 0 });

        string description = Rich(
            P(new Run("A beiratkozás teendői a "), new Bold(new Run("2026/27-es")), new Run(" tanévre. "), new Italic(new Run("Határidő: szeptember 5.")),
                new Run(" "), new Underline(new Run("Fontos:")), new Run(" "), new Run("piros szöveg") { Foreground = new SolidColorBrush(Color.FromRgb(0xEB, 0x57, 0x57)) }),
            P(new Run("Második sor, utána egy üres sor.")),
            P(),
            P(new Run("Srpski: čokolada, ćevap, džep, đak, šuma, žaba — Č Ć Dž Đ Š Ž.")));

        List<Paragraph> first = new List<Paragraph>
        {
            P(new Bold(new Run("Jegyzet 1. oldal")), new Run(" — formázással"), new LineBreak(), new Run("sortörés Shift+Enterrel")),
            P()
        };
        for (int i = 1; i <= 70; i++)
            first.Add(P(new Run(i + ". sor: hosszabb jegyzetsor, ami több helyen is tördelődhet, ha elég hosszú ahhoz, hogy ne férjen el egy sorban a lapon.")));
        first.Add(P(new Run(string.Concat(Enumerable.Repeat("Ez egy nagyon hosszú bekezdés, ami több oldalon át tart, hogy látsszon: a hosszú jegyzet is jól tördelődik. ", 120)))));

        return new Project
        {
            Id = Guid.NewGuid(),
            Name = "Beiratkozás — őszi félév (ő ű č ć đ š ž)",
            Description = description,
            Tasks = tasks,
            NotePages = new List<NotePage>
            {
                new NotePage { Body = Rich(first.ToArray()) },
                new NotePage { Body = "" },
                new NotePage { Body = Rich(P(new Run("Második oldal 😀 ✓ — emoji és szimbólum")), P(new Run("Utolsó sor."))) }
            },
            Attachments = new List<Attachment>
            {
                new Attachment { Id = Guid.NewGuid(), DisplayName = "önéletrajz_végleges.pdf", StoredFileName = Guid.NewGuid() + ".pdf", SizeBytes = 248_000, AddedDate = new DateTime(2026, 9, 1) },
                new Attachment { Id = Guid.NewGuid(), DisplayName = "fénykép.jpg", StoredFileName = Guid.NewGuid() + ".jpg", SizeBytes = 3_400_000, AddedDate = new DateTime(2026, 9, 2) }
            },
            LastModified = DateTime.Now
        };
    }

    static Paragraph P(params Inline[] inlines)
    {
        Paragraph paragraph = new Paragraph();
        paragraph.Inlines.AddRange(inlines);
        return paragraph;
    }

    // Stored the way the editor stores it (RichTextHelper).
    static string Rich(params Paragraph[] paragraphs)
    {
        FlowDocument document = new FlowDocument();
        document.Blocks.AddRange(paragraphs);
        return RichTextHelper.ToStorableString(document);
    }
}
