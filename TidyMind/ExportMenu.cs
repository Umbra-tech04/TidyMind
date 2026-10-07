using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace TidyMind
{
    // The UI side of exporting: picking where to save, running the export, saying how it went. Shared by the
    // memory exports (main window) and the per-card "Export" submenu on project and collection cards.
    public static class ExportMenu
    {
        // An "Export" menu item with Excel / Word / PDF under it. name: the suggested file name (without extension).
        // write: does the export to the chosen path in the chosen format.
        public static MenuItem Submenu(Func<string> name, Action<string, ExportFormat> write)
        {
            MenuItem export = new MenuItem { Header = "Export" };
            export.Items.Add(FormatItem("Excel", ExportFormat.Excel, name, write));
            export.Items.Add(FormatItem("Word", ExportFormat.Docx, name, write));
            export.Items.Add(FormatItem("PDF", ExportFormat.Pdf, name, write));
            return export;
        }

        private static MenuItem FormatItem(string header, ExportFormat format, Func<string> name, Action<string, ExportFormat> write)
        {
            MenuItem item = new MenuItem { Header = header };
            item.Click += (s, e) => SaveAs(format, name(), path => write(path, format));
            return item;
        }

        // Asks where to save, then writes. A failed write (most often: the file is open in Excel or Word) is
        // reported instead of taking the app down.
        public static void SaveAs(ExportFormat format, string name, Action<string> write)
        {
            SaveFileDialog dialog = new SaveFileDialog();
            dialog.FileName = SafeFileName(name) + "_export." + GetExtension(format);
            dialog.Filter = GetFilter(format);
            if (dialog.ShowDialog() != true) return;

            try
            {
                write(dialog.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show("The export couldn't be saved.\n\n" + ex.Message, "Export",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            MessageBox.Show("Export complete.", "Export", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private const int MaxNameLength = 100;

        private static readonly string[] ReservedNames =
        {
            "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        // Card and memory names can be anything, but the suggested file name must be one Windows accepts: no
        // characters a file name can't hold (e.g. "Q1/Q2"), no trailing dots or spaces (Windows drops them), not a
        // device name like CON, and short enough to leave room for the folder and "_export.xlsx".
        private static string SafeFileName(string name)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            string clean = new string((name ?? "").Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
            if (clean.Length > MaxNameLength)
                clean = clean.Substring(0, MaxNameLength);
            clean = clean.TrimEnd('.', ' ');

            if (clean.Length == 0)
                return "TidyMind";
            if (ReservedNames.Contains(clean, StringComparer.OrdinalIgnoreCase))
                return clean + "_";
            return clean;
        }

        private static string GetExtension(ExportFormat format)
        {
            switch (format)
            {
                case ExportFormat.Excel: return "xlsx";
                case ExportFormat.Docx: return "docx";
                case ExportFormat.Pdf: return "pdf";
                default: return "xlsx";
            }
        }

        private static string GetFilter(ExportFormat format)
        {
            switch (format)
            {
                case ExportFormat.Excel: return "Excel files (*.xlsx)|*.xlsx";
                case ExportFormat.Docx: return "Word documents (*.docx)|*.docx";
                case ExportFormat.Pdf: return "PDF files (*.pdf)|*.pdf";
                default: return "All files (*.*)|*.*";
            }
        }
    }
}
