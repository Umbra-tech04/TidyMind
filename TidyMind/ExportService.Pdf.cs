using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Documents;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace TidyMind
{
    // The PDF layout, shared by every PDF export (a project, an item, a whole memory, everything): a title block, then
    // each item's sections, empty ones left out. Done and open entries differ by a filled box and strikethrough, not by
    // color alone. The Word layout (ExportService.Docx.cs) is the same.
    public static partial class ExportService
    {
        // Lato is bundled with QuestPDF and covers Hungarian and Serbian Latin letters; the Windows fonts after it only
        // stand in for what Lato lacks in user text (emoji, symbols, other scripts).
        private static readonly string[] PdfFonts = { "Lato", "Segoe UI", "Segoe UI Symbol", "Segoe UI Emoji" };

        private const string OpenBox =
            "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 10 10\">"
            + "<rect x=\"0.6\" y=\"0.6\" width=\"8.8\" height=\"8.8\" rx=\"1.6\" fill=\"none\" stroke=\"#5F6368\" stroke-width=\"1.1\"/></svg>";

        private const string CheckedBox =
            "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 10 10\">"
            + "<rect x=\"0.6\" y=\"0.6\" width=\"8.8\" height=\"8.8\" rx=\"1.6\" fill=\"#5F6368\" stroke=\"#5F6368\" stroke-width=\"1.1\"/>"
            + "<path d=\"M2.6 5.2 L4.3 6.9 L7.5 3.3\" fill=\"none\" stroke=\"#FFFFFF\" stroke-width=\"1.4\" stroke-linecap=\"round\" stroke-linejoin=\"round\"/></svg>";

        private static void WritePdf(List<ExportItem> items, string title, string exported, bool oneItem, string filePath)
        {
            QuestPDF.Fluent.Document.Create(document => document.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.MarginHorizontal(50);
                page.MarginVertical(40);
                page.PageColor(Colors.White);
                page.DefaultTextStyle(style => style.FontFamily(PdfFonts).FontSize(10).FontColor(Ink).LineHeight(1.35f));

                // The full title on the first page; later pages repeat it small, so a loose page still says what it is.
                page.Header().Column(header =>
                {
                    header.Item().ShowOnce().Column(first =>
                    {
                        first.Item().Text(title).FontSize(22).Bold().LineHeight(1.15f).ClampLines(3);
                        first.Item().PaddingTop(4).Text(exported).FontSize(9).FontColor(Muted);
                    });
                    header.Item().SkipOnce().Text(title).FontSize(9).FontColor(Muted).ClampLines(1);
                    header.Item().PaddingTop(8).PaddingBottom(14).LineHorizontal(0.75f).LineColor(Rule);
                });

                page.Footer().AlignCenter().Text(text =>
                {
                    text.DefaultTextStyle(style => style.FontSize(8).FontColor(Faint));
                    text.CurrentPageNumber();
                    text.Span(" / ");
                    text.TotalPages();
                });

                page.Content().Column(column =>
                {
                    PdfFlow flow = new PdfFlow(column);
                    if (items.Count == 0)
                        EmptyNote(flow);

                    ExportItem previous = null;
                    foreach (ExportItem item in items)
                    {
                        bool underGroup = false;
                        if (StartsGroup(previous, item) && !string.IsNullOrEmpty(item.Group))
                        {
                            // Headings are drawn later (PdfFlow), so they take copies, not the loop's variables.
                            string name = item.Group;
                            float gap = flow.GapAbove(22);
                            flow.Heading(c => c.PaddingTop(gap).PaddingBottom(2).Column(g =>
                            {
                                g.Item().Text(name).FontSize(16).Bold();
                                g.Item().PaddingTop(4).LineHorizontal(0.5f).LineColor(Rule);
                            }));
                            underGroup = true;
                        }
                        previous = item;

                        if (!oneItem)
                        {
                            float gap = underGroup ? 8 : flow.GapAbove(18);
                            flow.Heading(c => c.PaddingTop(gap).Text(NameOrUnnamed(item.Name)).FontSize(14).Bold());
                        }

                        if (item.Project != null)
                            AddProject(flow, item.Project);
                        else if (item.Todos != null)
                            AddChecklist(flow, item.Todos.Select(t => (t.Text, t.IsDone)));
                        else
                            AddFields(flow, item);
                    }
                    flow.End();
                });
            })).GeneratePdf(filePath);
        }

        // Rows go into the page column one by one, so a long list or note breaks between rows. A heading is held back
        // and printed together with the row after it, so it never ends up alone at the bottom of a page.
        private sealed class PdfFlow
        {
            private readonly ColumnDescriptor column;
            private readonly List<Action<IContainer>> headings = new List<Action<IContainer>>();

            public PdfFlow(ColumnDescriptor column) => this.column = column;

            private bool isEmpty = true;

            // Space above a heading: none at the very top of the document.
            public float GapAbove(float gap) => isEmpty && headings.Count == 0 ? 0 : gap;

            public void Heading(Action<IContainer> heading)
            {
                headings.Add(heading);
            }

            // Kept on one page when it fits on one; a row taller than a page (a very long paragraph) still breaks.
            public void Row(Action<IContainer> row)
            {
                List<Action<IContainer>> parts = headings.Append(row).ToList();
                headings.Clear();
                isEmpty = false;
                column.Item().PreventPageBreak().Column(block =>
                {
                    foreach (Action<IContainer> part in parts)
                        block.Item().Element(part);
                });
            }

            public void End()
            {
                foreach (Action<IContainer> heading in headings)
                    column.Item().Element(heading);
                headings.Clear();
            }
        }

        // A section starts with its name over a dark rule, with more space above it than between its own lines.
        private static void SectionLabel(PdfFlow flow, string label)
        {
            float gap = flow.GapAbove(20);
            flow.Heading(c => c.PaddingTop(gap).PaddingBottom(6).Column(section =>
            {
                section.Item().Text(label.ToUpper(Loc.Culture)).FontSize(10).Bold().FontColor(Ink).LetterSpacing(0.04f);
                section.Item().PaddingTop(3).LineHorizontal(0.75f).LineColor(SectionRule);
            }));
        }

        // ---- Project ----------------------------------------------------------

        private static void AddProject(PdfFlow flow, Project project)
        {
            ProjectParts parts = Parts(project);
            if (parts.IsEmpty)
            {
                EmptyNote(flow);
                return;
            }

            if (parts.Description.Count > 0)
            {
                SectionLabel(flow, Loc.T("ProjectWindow.Description"));
                parts.Description.Select(PdfLine).ToList().ForEach(flow.Row);
            }

            if (parts.Tasks.Count > 0)
            {
                SectionLabel(flow, Loc.T("ProjectWindow.Tasks"));
                AddChecklist(flow, parts.Tasks.Select(t => (t.Title, t.IsDone)));
            }

            if (parts.NotePages.Count > 0)
            {
                SectionLabel(flow, Loc.T("ProjectWindow.Notes"));
                for (int i = 0; i < parts.NotePages.Count; i++)
                {
                    // Notebook pages are kept apart by a short rule.
                    if (i > 0)
                        flow.Heading(c => c.PaddingVertical(10).AlignLeft().Width(60).LineHorizontal(0.75f).LineColor(Rule));
                    parts.NotePages[i].Select(PdfLine).ToList().ForEach(flow.Row);
                }
            }

            if (parts.Files.Count > 0)
            {
                SectionLabel(flow, Loc.T("ProjectWindow.Files"));
                foreach (Attachment file in parts.Files)
                    flow.Row(c => c.PaddingVertical(2).Text(text =>
                    {
                        text.Span(NameOrUnnamed(file.DisplayName));
                        text.Span("   " + Loc.F("Export.FileDetails", AttachmentStore.FormatSize(file.SizeBytes),
                            file.AddedDate.ToString(Loc.T("Format.ShortDate"), Loc.Culture))).FontSize(8.5f).FontColor(Muted);
                    }));
            }
        }

        private static void AddChecklist(PdfFlow flow, IEnumerable<(string Text, bool Done)> entries)
        {
            (List<string> open, List<string> done) = SplitChecklist(entries);
            foreach (string text in open)
                flow.Row(c => CheckRow(c, text, false));
            if (done.Count > 0 && open.Count > 0)
                flow.Heading(c => c.PaddingTop(10).PaddingBottom(3)
                    .Text(Loc.F("Export.DoneGroup", done.Count)).FontSize(9).Bold().FontColor(Ink));
            foreach (string text in done)
                flow.Row(c => CheckRow(c, text, true));
        }

        private static void CheckRow(IContainer container, string text, bool done)
        {
            container.PaddingVertical(2.5f).Row(row =>
            {
                row.ConstantItem(18).PaddingTop(2.5f).Width(9.5f).Height(9.5f).Svg(done ? CheckedBox : OpenBox);
                row.RelativeItem().Text(line =>
                {
                    TextSpanDescriptor span = line.Span(text);
                    if (done)
                        span.FontColor(Muted).Strikethrough().DecorationColor(Faint);
                });
            });
        }

        // ---- Items, day plan and other label/value lists ------------------------

        private static void AddFields(PdfFlow flow, ExportItem item)
        {
            byte[]? png = PictureAsPng(item);
            List<(string Label, string Value)> fields = FieldsShown(item, png);

            if (png != null)
            {
                // Picture on the left, fields on the right, like the item detail view.
                flow.Row(c => c.PaddingTop(6).Row(row =>
                {
                    row.ConstantItem(84).Height(84).AlignTop().Image(png).FitArea();
                    row.ConstantItem(14);
                    row.RelativeItem().Column(info =>
                    {
                        foreach ((string label, string value) in fields)
                            info.Item().Element(f => FieldRow(f, label, value));
                    });
                }));
                return;
            }

            if (fields.Count == 0)
            {
                EmptyNote(flow);
                return;
            }
            flow.Heading(c => c.PaddingTop(4));
            foreach ((string label, string value) in fields)
                flow.Row(c => FieldRow(c, label, value));
        }

        private static void EmptyNote(PdfFlow flow) =>
            flow.Row(c => c.PaddingTop(6).Text(Loc.T("Export.Empty")).FontColor(Muted).Italic());

        private static void FieldRow(IContainer container, string label, string value)
        {
            container.PaddingVertical(2.5f).Row(row =>
            {
                row.ConstantItem(120).PaddingRight(10).Text(label).FontSize(9).FontColor(Muted);
                row.RelativeItem().Text(value);
            });
        }

        // ---- Formatted text (descriptions and notes) ------------------------------

        // The editor has no space between paragraphs (each Enter is a new one), so neither has the PDF; an empty
        // paragraph is an empty line.
        private static Action<IContainer> PdfLine(RichLine line)
        {
            if (line.Runs == null)
                return c => c.PaddingLeft(line.Indent).Text(line.Text);
            if (line.Blank)
                return c => c.Height(13.5f);

            return c => c.PaddingLeft(line.Indent).Row(row =>
            {
                if (line.Marker != null)
                    row.ConstantItem(22).Text(line.Marker).FontColor(Muted);
                row.RelativeItem().Text(text =>
                {
                    foreach (Run run in line.Runs)
                        StyledSpan(text, run);
                });
            });
        }

        private static void StyledSpan(TextDescriptor text, Run run)
        {
            TextSpanDescriptor span = text.Span(run.Text);
            (bool bold, bool italic, bool underline, string color) = RunLook(run);
            if (bold)
                span.Bold();
            if (italic)
                span.Italic();
            if (underline)
                span.Underline();
            if (color != null)
                span.FontColor(color);
        }
    }
}
