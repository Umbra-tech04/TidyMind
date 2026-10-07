using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace TidyMind
{
    // Formatted descriptions live in the existing string field as WPF FlowDocument XAML (a "<Section …>" fragment,
    // as written by TextRange.Save). It's plain XML text, so System.Text.Json stores it like any other string.
    public static class RichTextHelper
    {
        private const string PresentationNamespace = "schemas.microsoft.com/winfx/2006/xaml/presentation";

        public static string ToStorableString(FlowDocument document)
        {
            TextRange range = new TextRange(document.ContentStart, document.ContentEnd);
            if (string.IsNullOrWhiteSpace(range.Text))
                return "";

            using (MemoryStream stream = new MemoryStream())
            {
                range.Save(stream, DataFormats.Xaml);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        public static void FromStorableString(FlowDocument document, string stored)
        {
            document.Blocks.Clear();
            if (string.IsNullOrEmpty(stored))
                return;

            if (IsMarkup(stored))
            {
                try
                {
                    TextRange range = new TextRange(document.ContentStart, document.ContentEnd);
                    using (MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(stored)))
                        range.Load(stream, DataFormats.Xaml);
                    return;
                }
                catch (Exception)
                {
                    document.Blocks.Clear(); // unreadable markup: fall back to showing it as text rather than losing it
                }
            }

            // Plain text from before descriptions were formatted: one paragraph per line.
            foreach (string line in stored.Replace("\r\n", "\n").Split('\n'))
                document.Blocks.Add(new Paragraph(new Run(line)));
        }

        // Formatting copied inside the app (WPF rich text) is kept; anything pasted from outside comes in as plain
        // text, so web pages or Word can't drag in their own fonts and sizes.
        public static void PasteOutsideTextAsPlain(RichTextBox editor)
        {
            DataObject.AddPastingHandler(editor, (s, e) =>
            {
                if (e.DataObject.GetDataPresent(DataFormats.Xaml) || !e.DataObject.GetDataPresent(DataFormats.UnicodeText))
                    return;
                DataObject plain = new DataObject();
                plain.SetData(DataFormats.UnicodeText, e.DataObject.GetData(DataFormats.UnicodeText));
                e.DataObject = plain;
            });
        }

        // For places that show a description or note without formatting (e.g. exports).
        public static string ToPlainText(string stored)
        {
            if (string.IsNullOrEmpty(stored) || !IsMarkup(stored))
                return stored ?? "";

            FlowDocument document = new FlowDocument();
            FromStorableString(document, stored);
            return new TextRange(document.ContentStart, document.ContentEnd).Text.TrimEnd();
        }

        private static bool IsMarkup(string stored)
        {
            return stored.TrimStart().StartsWith("<Section", StringComparison.Ordinal)
                && stored.Contains(PresentationNamespace);
        }
    }
}
