using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;

namespace TidyMind
{
    // The Word layout: the PDF's (ExportService.Pdf.cs) in an editable document. The same title block, sections,
    // checklists, formatted notes and files, empty sections left out, in the same colors and the same order. Written
    // with the Open XML SDK; Word's units: twips (1/20 point) for lengths, half-points for font sizes.
    public static partial class ExportService
    {
        private const string DocxFont = "Segoe UI";
        private const string DocxSymbolFont = "Segoe UI Symbol"; // the ☐ and ☑ of a checklist
        private const string BoxInk = "#5F6368";

        // A4 (595.3 x 841.9 points) with the PDF's margins, in points; the text is as wide as the PDF's.
        private const int DocxMarginH = 50;
        private const int DocxMarginV = 40;
        private const int DocxTextWidth = 595 - 2 * DocxMarginH;

        private static void WriteDocx(List<ExportItem> items, string title, string exported, bool oneItem, string filePath)
        {
            using (WordprocessingDocument document = WordprocessingDocument.Create(filePath, WordprocessingDocumentType.Document))
            {
                document.PackageProperties.Title = XmlSafe(title);
                document.PackageProperties.Creator = "TidyMind";

                MainDocumentPart main = document.AddMainDocumentPart();
                main.Document = new Document(new Body());
                main.AddNewPart<StyleDefinitionsPart>().Styles = DocxStyles();
                main.AddNewPart<DocumentSettingsPart>().Settings = new Settings(new Compatibility(new CompatibilitySetting
                {
                    Name = CompatSettingNameValues.CompatibilityMode, Uri = "http://schemas.microsoft.com/office/word", Val = "15"
                }));

                // The full title on the first page; later pages repeat it small, so a loose page still says what it is.
                // Every page is numbered.
                Paragraph running = new Paragraph(new ParagraphProperties
                {
                    ParagraphBorders = new ParagraphBorders { BottomBorder = DocxRule(Rule, 4, 4) },
                    SpacingBetweenLines = DocxSpacing(0, 14)
                }, DocxRun(title, size: 9, color: Muted));
                HeaderPart laterHeader = main.AddNewPart<HeaderPart>();
                laterHeader.Header = new Header(running);
                HeaderPart firstHeader = main.AddNewPart<HeaderPart>();
                firstHeader.Header = new Header(new Paragraph());
                FooterPart laterFooter = main.AddNewPart<FooterPart>();
                laterFooter.Footer = PageNumberFooter();
                FooterPart firstFooter = main.AddNewPart<FooterPart>();
                firstFooter.Footer = PageNumberFooter();

                DocxFlow flow = new DocxFlow(main);
                flow.Line(0, 4).Append(DocxRun(title, size: 22, bold: true));
                Paragraph date = flow.Line(0, 14);
                date.ParagraphProperties.ParagraphBorders = new ParagraphBorders { BottomBorder = DocxRule(Rule, 6, 8) };
                date.Append(DocxRun(exported, size: 9, color: Muted));
                flow.AtTop = true;
                if (items.Count == 0)
                    DocxEmpty(flow);

                ExportItem previous = null;
                foreach (ExportItem item in items)
                {
                    bool underGroup = false;
                    if (StartsGroup(previous, item) && !string.IsNullOrEmpty(item.Group))
                    {
                        Paragraph heading = flow.Heading(22, 2);
                        heading.ParagraphProperties.ParagraphBorders = new ParagraphBorders { BottomBorder = DocxRule(Rule, 4, 4) };
                        heading.Append(DocxRun(item.Group, size: 16, bold: true));
                        underGroup = true;
                    }
                    previous = item;

                    if (!oneItem)
                        flow.Heading(underGroup ? 8 : 18, 0).Append(DocxRun(NameOrUnnamed(item.Name), size: 14, bold: true));

                    if (item.Project != null)
                        DocxProject(flow, item.Project);
                    else if (item.Notebook != null)
                        DocxNotePages(flow, NotebookPages(item.Notebook));
                    else if (item.Todos != null)
                        DocxChecklist(flow, item.Todos.Select(t => (t.Text, t.IsDone)));
                    else
                        DocxFields(flow, item);
                }

                flow.Body.Append(new SectionProperties(
                    new HeaderReference { Type = HeaderFooterValues.Default, Id = main.GetIdOfPart(laterHeader) },
                    new HeaderReference { Type = HeaderFooterValues.First, Id = main.GetIdOfPart(firstHeader) },
                    new FooterReference { Type = HeaderFooterValues.Default, Id = main.GetIdOfPart(laterFooter) },
                    new FooterReference { Type = HeaderFooterValues.First, Id = main.GetIdOfPart(firstFooter) },
                    new PageSize { Width = 11906, Height = 16838 },
                    new PageMargin
                    {
                        Top = DocxMarginV * 20, Bottom = DocxMarginV * 20, Left = DocxMarginH * 20, Right = DocxMarginH * 20,
                        Header = 400, Footer = 500, Gutter = 0
                    },
                    new TitlePage()));
                main.Document.Save();
            }
        }

        // Segoe UI 10 in the PDF's ink, in the export's language, no space between paragraphs: what every paragraph
        // starts from.
        private static Styles DocxStyles()
        {
            RunPropertiesBaseStyle run = new RunPropertiesBaseStyle
            {
                RunFonts = new RunFonts { Ascii = DocxFont, HighAnsi = DocxFont, ComplexScript = DocxFont, EastAsia = DocxFont },
                Color = new Color { Val = Ink.TrimStart('#') },
                FontSize = new FontSize { Val = "20" },
                FontSizeComplexScript = new FontSizeComplexScript { Val = "20" },
                Languages = new Languages { Val = Loc.Culture.Name }
            };
            ParagraphPropertiesBaseStyle paragraph = new ParagraphPropertiesBaseStyle
            {
                SpacingBetweenLines = new SpacingBetweenLines { Before = "0", After = "0", Line = "259", LineRule = LineSpacingRuleValues.Auto }
            };
            return new Styles(
                new DocDefaults(new RunPropertiesDefault(run), new ParagraphPropertiesDefault(paragraph)),
                new Style(new StyleName { Val = "Normal" }, new PrimaryStyle()) { Type = StyleValues.Paragraph, StyleId = "Normal", Default = true });
        }

        private static Footer PageNumberFooter()
        {
            Paragraph number = new Paragraph(new ParagraphProperties { Justification = new Justification { Val = JustificationValues.Center } });
            number.Append(new SimpleField(DocxRun("1", size: 8, color: Faint)) { Instruction = " PAGE " });
            number.Append(DocxRun(" / ", size: 8, color: Faint));
            number.Append(new SimpleField(DocxRun("1", size: 8, color: Faint)) { Instruction = " NUMPAGES " });
            return new Footer(number);
        }

        // Paragraphs go into the document one after another. A heading is kept with the line after it, so it never ends
        // up alone at the bottom of a page.
        private sealed class DocxFlow
        {
            public readonly MainDocumentPart Main;
            public readonly Body Body;
            private uint pictures;

            public DocxFlow(MainDocumentPart main)
            {
                Main = main;
                Body = main.Document.Body;
            }

            // Right under the title: a heading there gets no space above it.
            public bool AtTop;

            public Paragraph Line(double before, double after)
            {
                AtTop = false;
                Paragraph line = new Paragraph(new ParagraphProperties { SpacingBetweenLines = DocxSpacing(before, after) });
                Body.Append(line);
                return line;
            }

            public Paragraph Heading(double gap, double after)
            {
                Paragraph heading = Line(AtTop ? 0 : gap, after);
                heading.ParagraphProperties.KeepNext = new KeepNext();
                return heading;
            }

            public void Table(Table table)
            {
                AtTop = false;
                Body.Append(table);
            }

            public uint NextPictureId() => ++pictures;
        }

        // Points to twips, as Word stores lengths.
        private static string Twips(double points) => ((int)Math.Round(points * 20)).ToString(CultureInfo.InvariantCulture);

        private static SpacingBetweenLines DocxSpacing(double before, double after) =>
            new SpacingBetweenLines { Before = Twips(before), After = Twips(after) };

        // A paragraph's bottom rule: `eighths` of a point thick, `space` points below the text.
        private static BottomBorder DocxRule(string color, uint eighths, uint space) =>
            new BottomBorder { Val = BorderValues.Single, Size = eighths, Space = space, Color = color.TrimStart('#') };

        // One run of text in one look. A line break in the text becomes a Word line break, a tab a tab.
        private static Run DocxRun(string text, double size = 0, string color = null, bool bold = false, bool italic = false,
            bool underline = false, bool strike = false, string font = null, int letterSpacing = 0)
        {
            RunProperties look = new RunProperties();
            if (font != null)
                look.RunFonts = new RunFonts { Ascii = font, HighAnsi = font, ComplexScript = font, EastAsia = font };
            if (bold)
                look.Bold = new Bold();
            if (italic)
                look.Italic = new Italic();
            if (strike)
                look.Strike = new Strike();
            if (color != null)
                look.Color = new Color { Val = color.TrimStart('#') };
            if (letterSpacing != 0)
                look.Spacing = new Spacing { Val = letterSpacing };
            if (size > 0)
                look.FontSize = new FontSize { Val = ((int)Math.Round(size * 2)).ToString(CultureInfo.InvariantCulture) };
            if (underline)
                look.Underline = new Underline { Val = UnderlineValues.Single };

            Run run = new Run(look);
            string[] lines = XmlSafe(text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0)
                    run.Append(new Break());
                string[] parts = lines[i].Split('\t');
                for (int j = 0; j < parts.Length; j++)
                {
                    if (j > 0)
                        run.Append(new TabChar());
                    if (parts[j].Length > 0)
                        run.Append(new Text(parts[j]) { Space = SpaceProcessingModeValues.Preserve });
                }
            }
            return run;
        }

        // A section starts with its name over a dark rule, with more space above it than between its own lines.
        private static void DocxSectionLabel(DocxFlow flow, string label)
        {
            Paragraph heading = flow.Heading(20, 6);
            heading.ParagraphProperties.ParagraphBorders = new ParagraphBorders { BottomBorder = DocxRule(SectionRule, 6, 3) };
            heading.Append(DocxRun(label.ToUpper(Loc.Culture), size: 10, bold: true, letterSpacing: 8));
        }

        private static void DocxEmpty(DocxFlow flow)
        {
            flow.Line(6, 0).Append(DocxRun(Loc.T("Export.Empty"), color: Muted, italic: true));
        }

        // ---- Project ----------------------------------------------------------

        private static void DocxProject(DocxFlow flow, Project project)
        {
            ProjectParts parts = Parts(project);
            if (parts.IsEmpty)
            {
                DocxEmpty(flow);
                return;
            }
            if (parts.Description.Count > 0)
            {
                DocxSectionLabel(flow, Loc.T("ProjectWindow.Description"));
                DocxRichText(flow, parts.Description);
            }

            if (parts.Tasks.Count > 0)
            {
                DocxSectionLabel(flow, Loc.T("ProjectWindow.Tasks"));
                DocxChecklist(flow, parts.Tasks.Select(t => (t.Title, t.IsDone)));
            }

            if (parts.NotePages.Count > 0)
            {
                DocxSectionLabel(flow, Loc.T("ProjectWindow.Notes"));
                DocxNotePages(flow, parts.NotePages);
            }

            if (parts.Files.Count > 0)
            {
                DocxSectionLabel(flow, Loc.T("ProjectWindow.Files"));
                foreach (Attachment file in parts.Files)
                {
                    Paragraph line = flow.Line(2, 2);
                    line.Append(DocxRun(NameOrUnnamed(file.DisplayName)));
                    line.Append(DocxRun("   " + Loc.F("Export.FileDetails", AttachmentStore.FormatSize(file.SizeBytes),
                        file.AddedDate.ToString(Loc.T("Format.ShortDate"), Loc.Culture)), size: 8.5, color: Muted));
                }
            }
        }

        // A notebook's pages (a project's notes, a memory's notebook), kept apart by a short rule: the border of an
        // empty line indented to 60 points.
        private static void DocxNotePages(DocxFlow flow, List<List<RichLine>> pages)
        {
            for (int i = 0; i < pages.Count; i++)
            {
                if (i > 0)
                {
                    Paragraph rule = flow.Line(0, 10);
                    rule.ParagraphProperties.KeepNext = new KeepNext();
                    rule.ParagraphProperties.ParagraphBorders = new ParagraphBorders { BottomBorder = DocxRule(Rule, 6, 1) };
                    rule.ParagraphProperties.Indentation = new Indentation { Right = Twips(DocxTextWidth - 60) };
                }
                DocxRichText(flow, pages[i]);
            }
        }

        private static void DocxChecklist(DocxFlow flow, IEnumerable<(string Text, bool Done)> entries)
        {
            (List<string> open, List<string> done) = SplitChecklist(entries);
            foreach (string text in open)
                DocxCheckRow(flow, text, false);
            if (done.Count > 0 && open.Count > 0)
                flow.Heading(10, 3).Append(DocxRun(Loc.F("Export.DoneGroup", done.Count), size: 9, bold: true));
            foreach (string text in done)
                DocxCheckRow(flow, text, true);
        }
        // The box hangs in the margin of the text, so a long entry wraps under its own first word.
        private static void DocxCheckRow(DocxFlow flow, string text, bool done)
        {
            Paragraph line = flow.Line(2.5, 2.5);
            line.ParagraphProperties.Indentation = new Indentation { Left = Twips(18), Hanging = Twips(18) };
            line.Append(DocxRun(done ? "☑" : "☐", color: BoxInk, font: DocxSymbolFont));
            line.Append(DocxRun("\t"));
            if (text.Length > 0)
                line.Append(done ? DocxRun(text, color: Muted, strike: true) : DocxRun(text));
        }

        // ---- Formatted text (descriptions and notes) ------------------------------

        // A line per paragraph, like the editor: no space between them, an empty paragraph is an empty line.
        private static void DocxRichText(DocxFlow flow, List<RichLine> lines)
        {
            foreach (RichLine richLine in lines)
            {
                Paragraph line = flow.Line(0, 0);
                if (richLine.Runs == null)
                {
                    line.ParagraphProperties.Indentation = new Indentation { Left = Twips(richLine.Indent) };
                    line.Append(DocxRun(richLine.Text));
                    continue;
                }
                if (richLine.Blank)
                    continue;

                if (richLine.Marker != null)
                {
                    line.ParagraphProperties.Indentation = new Indentation { Left = Twips(richLine.Indent + 22), Hanging = Twips(22) };
                    line.Append(DocxRun(richLine.Marker + "\t", color: Muted));
                }
                else if (richLine.Indent > 0)
                    line.ParagraphProperties.Indentation = new Indentation { Left = Twips(richLine.Indent) };

                foreach (System.Windows.Documents.Run run in richLine.Runs.Where(r => r.Text.Length > 0))
                {
                    (bool bold, bool italic, bool underline, string color) = RunLook(run);
                    line.Append(DocxRun(run.Text, color: color, bold: bold, italic: italic, underline: underline));
                }
            }
        }

        // ---- Items, day plan and other label/value lists ------------------------

        // A borderless table: labels in a narrow muted column, values beside them, and the item's picture (if it has
        // one) on the left, like the item detail view.
        private static void DocxFields(DocxFlow flow, ExportItem item)
        {
            byte[]? png = PictureAsPng(item);
            List<(string Label, string Value)> fields = FieldsShown(item, png);

            if (png == null && fields.Count == 0)
            {
                DocxEmpty(flow);
                return;
            }

            int[] widths = png != null ? new[] { 98, 120, DocxTextWidth - 218 } : new[] { 120, DocxTextWidth - 120 };
            Table table = new Table(new TableProperties
            {
                TableWidth = new TableWidth { Width = Twips(DocxTextWidth), Type = TableWidthUnitValues.Dxa },
                TableLayout = new TableLayout { Type = TableLayoutValues.Fixed },
                TableCellMarginDefault = new TableCellMarginDefault
                {
                    TopMargin = new TopMargin { Width = "0", Type = TableWidthUnitValues.Dxa },
                    TableCellLeftMargin = new TableCellLeftMargin { Width = 0, Type = TableWidthValues.Dxa },
                    BottomMargin = new BottomMargin { Width = "0", Type = TableWidthUnitValues.Dxa },
                    TableCellRightMargin = new TableCellRightMargin { Width = 200, Type = TableWidthValues.Dxa }
                }
            }, new TableGrid(widths.Select(w => new GridColumn { Width = Twips(w) })));

            int rows = Math.Max(fields.Count, 1);
            for (int i = 0; i < rows; i++)
            {
                List<TableCell> cells = new List<TableCell>();
                double above = i == 0 ? 6 : 2.5;
                if (png != null)
                    cells.Add(i == 0 ? DocxCell(widths[0], DocxPicture(flow, png), new VerticalMerge { Val = MergedCellValues.Restart })
                        : DocxCell(widths[0], new Paragraph(), new VerticalMerge()));
                (string label, string value) = i < fields.Count ? fields[i] : ("", "");
                cells.Add(DocxCell(widths[^2], DocxCellText(above, DocxRun(label, size: 9, color: Muted))));
                cells.Add(DocxCell(widths[^1], DocxCellText(above, DocxRun(value))));
                TableRow row = new TableRow(new TableRowProperties(new CantSplit()));
                row.Append(cells);
                table.Append(row);
            }
            flow.Table(table);
        }

        private static TableCell DocxCell(int width, Paragraph content, VerticalMerge merge = null)
        {
            TableCellProperties properties = new TableCellProperties
            {
                TableCellWidth = new TableCellWidth { Width = Twips(width), Type = TableWidthUnitValues.Dxa },
                VerticalMerge = merge
            };
            return new TableCell(properties, content);
        }

        private static Paragraph DocxCellText(double above, Run run) =>
            new Paragraph(new ParagraphProperties { SpacingBetweenLines = DocxSpacing(above, 2.5) }, run);

        // The picture fitted into 84 points, as in the PDF.
        private static Paragraph DocxPicture(DocxFlow flow, byte[] png)
        {
            ImagePart part = flow.Main.AddImagePart(ImagePartType.Png);
            using (MemoryStream data = new MemoryStream(png))
                part.FeedData(data);

            (int width, int height) = PngSize(png);
            double scale = 84.0 / Math.Max(width, height);
            const long EmuPerPoint = 12700;
            long cx = (long)Math.Round(width * scale) * EmuPerPoint;
            long cy = (long)Math.Round(height * scale) * EmuPerPoint;
            uint id = flow.NextPictureId();
            string name = "Picture " + id;

            DW.Inline inline = new DW.Inline(
                new DW.Extent { Cx = cx, Cy = cy },
                new DW.EffectExtent { LeftEdge = 0, TopEdge = 0, RightEdge = 0, BottomEdge = 0 },
                new DW.DocProperties { Id = id, Name = name },
                new DW.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoChangeAspect = true }),
                new A.Graphic(new A.GraphicData(
                    new PIC.Picture(
                        new PIC.NonVisualPictureProperties(
                            new PIC.NonVisualDrawingProperties { Id = 0, Name = name },
                            new PIC.NonVisualPictureDrawingProperties()),
                        new PIC.BlipFill(new A.Blip { Embed = flow.Main.GetIdOfPart(part) }, new A.Stretch(new A.FillRectangle())),
                        new PIC.ShapeProperties(
                            new A.Transform2D(new A.Offset { X = 0, Y = 0 }, new A.Extents { Cx = cx, Cy = cy }),
                            new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle })))
                { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" }))
            {
                DistanceFromTop = 0, DistanceFromBottom = 0, DistanceFromLeft = 0, DistanceFromRight = 0
            };
            return new Paragraph(new ParagraphProperties { SpacingBetweenLines = DocxSpacing(6, 0) }, new Run(new Drawing(inline)));
        }

        // Width and height from the PNG header (the IHDR chunk, right after the 8-byte signature).
        private static (int Width, int Height) PngSize(byte[] png) =>
            ((png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19], (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23]);
    }
}
