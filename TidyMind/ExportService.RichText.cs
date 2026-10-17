using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Documents;
using WpfSolidBrush = System.Windows.Media.SolidColorBrush;

namespace TidyMind
{
    // A stored description or note page read once into lines with their look, which every format lays out its own way.
    public static partial class ExportService
    {
        // One paragraph of a stored description or note page.
        private sealed class RichLine
        {
            public bool Blank;
            public float Indent;   // in points
            public string Marker;  // a list item's bullet or number
            public List<Run> Runs; // the paragraph's text; null for a table or embedded control, which has only Text
            public string Text;
        }

        // A stored description or note page (FlowDocument XAML, or plain text from older versions) as one line per
        // paragraph, with its bold, italic, underline, color and line breaks. Empty lines between paragraphs stay,
        // empty ones at the start and end don't. Nothing at all for an empty text.
        private static List<RichLine> RichLines(string stored)
        {
            // Black, not the system text color a document gets without a window (white in a high-contrast theme): plain
            // text from older versions has no color of its own.
            FlowDocument document = new FlowDocument { Foreground = System.Windows.Media.Brushes.Black };
            RichTextHelper.FromStorableString(document, stored);
            List<RichLine> lines = new List<RichLine>();
            AddBlocks(document.Blocks, lines, 0);

            int first = lines.FindIndex(l => !l.Blank);
            int last = lines.FindLastIndex(l => !l.Blank);
            return first < 0 ? new List<RichLine>() : lines.Skip(first).Take(last - first + 1).ToList();
        }

        private static void AddBlocks(IEnumerable<Block> blocks, List<RichLine> lines, float indent)
        {
            foreach (Block block in blocks)
            {
                switch (block)
                {
                    case Paragraph paragraph:
                        lines.Add(ParagraphLine(paragraph, indent, null));
                        break;
                    case List list:
                        int number = list.StartIndex;
                        foreach (ListItem listItem in list.ListItems)
                        {
                            string marker = list.MarkerStyle == TextMarkerStyle.Decimal ? number + "." : "•";
                            number++;
                            bool firstBlock = true;
                            foreach (Block inner in listItem.Blocks)
                            {
                                if (firstBlock && inner is Paragraph p)
                                    lines.Add(ParagraphLine(p, indent, marker));
                                else
                                    AddBlocks(new[] { inner }, lines, indent + 22);
                                firstBlock = false;
                            }
                        }
                        break;
                    case Section section:
                        AddBlocks(section.Blocks, lines, indent);
                        break;
                    default:
                        // A table or embedded control (only from formatting pasted in): its text, unformatted.
                        string text = new TextRange(block.ContentStart, block.ContentEnd).Text.TrimEnd();
                        lines.Add(new RichLine { Blank = text.Length == 0, Indent = indent, Text = text });
                        break;
                }
            }
        }

        private static RichLine ParagraphLine(Paragraph paragraph, float indent, string marker)
        {
            List<Run> runs = Runs(paragraph.Inlines).ToList();
            bool blank = marker == null && runs.All(r => r.Text.Trim().Length == 0 && r.Text.IndexOf('\n') < 0);
            return new RichLine { Blank = blank, Indent = indent, Marker = marker, Runs = runs };
        }

        // Every piece of text in reading order; a line break (Shift+Enter) becomes a run holding "\n".
        private static IEnumerable<Run> Runs(InlineCollection inlines)
        {
            foreach (Inline inline in inlines)
            {
                switch (inline)
                {
                    case Run run:
                        yield return run;
                        break;
                    case LineBreak:
                        yield return new Run("\n");
                        break;
                    case Span span:
                        foreach (Run inner in Runs(span.Inlines))
                            yield return inner;
                        break;
                }
            }
        }

        // A run's own look. Color is null for the default text color (and plain black), which prints as the ink.
        private static (bool Bold, bool Italic, bool Underline, string Color) RunLook(Run run)
        {
            bool bold = run.FontWeight.ToOpenTypeWeight() >= FontWeights.SemiBold.ToOpenTypeWeight();
            bool italic = run.FontStyle == FontStyles.Italic || run.FontStyle == FontStyles.Oblique;

            // Underline is set on the Underline element around the run, not inherited by it: look up the parents too.
            bool underline = false;
            for (TextElement element = run; element != null && !underline; element = element.Parent as TextElement)
            {
                TextDecorationCollection decorations = element is Inline i ? i.TextDecorations
                    : element is Paragraph p ? p.TextDecorations : null;
                underline = decorations != null && decorations.Any(d => d.Location == TextDecorationLocation.Underline);
            }

            string color = null;
            if (run.Foreground is WpfSolidBrush brush && brush.Color.A > 0 && !IsDefaultInk(brush.Color))
                color = "#" + brush.Color.R.ToString("X2") + brush.Color.G.ToString("X2") + brush.Color.B.ToString("X2");
            return (bold, italic, underline, color);
        }

        private static bool IsDefaultInk(System.Windows.Media.Color color) =>
            (color.R == 0 && color.G == 0 && color.B == 0) || (color.R == 0x1C && color.G == 0x1B && color.B == 0x19);
    }
}
