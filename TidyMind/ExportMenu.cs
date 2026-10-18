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
            MenuItem export = new MenuItem { Header = Loc.T("Memory.Menu.Export") };
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

        // Asks where to save, then writes. The export is written to a temporary file next to the target and only
        // moved over it once complete, so a failed export (a bad picture, the file open in Excel or Word, a full
        // disk) never leaves a half-written file or destroys the one already there. The failure is reported
        // instead of taking the app down.
        public static void SaveAs(ExportFormat format, string name, Action<string> write)
        {
            SaveFileDialog dialog = new SaveFileDialog();
            dialog.FileName = SafeFileName(name) + "_export." + GetExtension(format);
            dialog.Filter = GetFilter(format);
            if (dialog.ShowDialog() != true) return;

            string target = dialog.FileName;
            bool existed = File.Exists(target);
            // Same folder (so the move is a rename) and same extension (the Excel and Word writers check it).
            string temp = Path.Combine(Path.GetDirectoryName(target) ?? "",
                Path.GetFileNameWithoutExtension(target) + ".tmp-" + Guid.NewGuid().ToString("N").Substring(0, 8)
                + Path.GetExtension(target));
            try
            {
                write(temp);
                File.Move(temp, target, overwrite: true);
            }
            catch (Exception ex)
            {
                try { File.Delete(temp); } catch (Exception) { /* best effort: nothing else to do with it */ }
                Msg.Show(Loc.F("Export.Failed", ex.Message)
                    + (existed ? "\n\n" + Loc.T("Export.ExistingKept") : ""), Loc.T("Export.Caption"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            Msg.Show(Loc.T("Export.Complete"), Loc.T("Export.Caption"), MessageBoxButton.OK, MessageBoxImage.Information);
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
        internal static string SafeFileName(string name)
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
                case ExportFormat.Excel: return Loc.T("Export.Filter.Excel");
                case ExportFormat.Docx: return Loc.T("Export.Filter.Word");
                case ExportFormat.Pdf: return Loc.T("Export.Filter.Pdf");
                default: return Loc.T("Files.AllFilter");
            }
        }
    }
}
