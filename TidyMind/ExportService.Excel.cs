using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using Spreadsheet = DocumentFormat.OpenXml.Spreadsheet;

namespace TidyMind
{
    // The Excel layout: the tasks, the files, each collection, the items' logs and the day plan are tables with a bold
    // header row (frozen, filterable, repeated on every printed page) and real date cells; descriptions and notes have a
    // sheet of their own, a paragraph per row. Every sheet starts with the export's title and date line and prints on
    // A4, one page wide. Text stays text: what the user types as text (prices, amounts) never becomes a number; only
    // fields the app itself keeps as numbers (year, km, a log's cost) are number cells.
    public static partial class ExportService
    {
        private const int XlHeaderRow = 4;
        private const double XlLineHeight = 15; // a line of 11 pt Calibri, in points
        private const int XlMaxLines = 26;      // 26 lines stay under Excel's limit of 409 points a row
        private const string DoneGray = "#7A7F86";
        private const string HeaderFill = "#E8EAED";
        private const string RowRule = "#E1E4E8";

        private sealed class XlColumn
        {
            public string Header;
            public double Width; // in Excel's character widths
        }

        // Cells are strings, DateTimes or doubles; a done row is printed gray.
        private sealed class XlTable
        {
            public readonly List<XlColumn> Columns = new List<XlColumn>();
            public readonly List<(object[] Cells, bool Done)> Rows = new List<(object[], bool)>();

            public void Column(string header, double width) => Columns.Add(new XlColumn { Header = header, Width = width });

            // As wide as its longest value (or the header), within `max` characters; longer values wrap.
            public void Column(string header, IEnumerable<string> values, double max) => Column(header, FitWidth(values, header, max));
        }

        private static void WriteXlsx(List<ExportItem> items, string title, string exported, bool oneItem, string filePath)
        {
            // Export all: a memory column says where each row comes from (the day plan has a sheet of its own).
            bool byMemory = items.Any(i => i.Memory != null);

            using (XLWorkbook workbook = new XLWorkbook())
            {
                workbook.Properties.Title = FitCell(title);
                workbook.Properties.Author = "TidyMind";

                XlTable tasks = TaskTable(items, byMemory);
                if (tasks.Rows.Count > 0)
                    AddTableSheet(workbook, Loc.T("ProjectWindow.Tasks"), title, exported, tasks);

                List<(ExportItem Item, List<(string Label, List<RichLine> Lines)> Sections)> texts = items.Where(i => i.Project != null || i.Notebook != null)
                    .Select(i => (Item: i, Sections: i.Project != null ? ProjectText(i.Project) : NotebookText(i.Notebook))).Where(t => t.Sections.Count > 0).ToList();
                if (texts.Count > 0)
                    AddTextSheet(workbook, title, exported, texts, oneItem);

                XlTable files = FileTable(items, byMemory);
                if (files.Rows.Count > 0)
                    AddTableSheet(workbook, Loc.T("ProjectWindow.Files"), title, exported, files);

                foreach (IGrouping<Profile, ExportItem> collection in items.Where(i => i.Entity != null).GroupBy(i => i.Memory))
                    AddTableSheet(workbook, collection.First().Group ?? title, title, exported, EntityTable(collection.Select(i => i.Entity).ToList()));

                foreach ((string name, XlTable records) in RecordTables(items, byMemory))
                    AddTableSheet(workbook, name, title, exported, records);

                foreach ((string name, XlTable log) in LogTables(items, byMemory))
                    AddTableSheet(workbook, name, title, exported, log);

                List<DayPlanBlock> blocks = items.Where(i => i.Blocks != null).SelectMany(i => i.Blocks).ToList();
                if (blocks.Count > 0)
                    AddTableSheet(workbook, Loc.T("Plan.Name"), title, exported, DayPlanTable(blocks));

                if (workbook.Worksheets.Count == 0)
                {
                    IXLWorksheet empty = workbook.Worksheets.Add(SanitizeSheetName(workbook, title));
                    XlTitle(empty, title, exported);
                    empty.Column(1).Width = 60;
                    empty.Cell(XlHeaderRow, 1).Value = Loc.T("Export.Empty");
                    empty.Cell(XlHeaderRow, 1).Style.Font.SetItalic().Font.SetFontColor(XLColor.FromHtml(Muted));
                    XlPrint(empty, 60, false);
                }

                workbook.SaveAs(filePath);
            }
            AbsolutePrintTitles(filePath);
        }

        // ClosedXML writes the repeated header row as "Tasks!4:4"; Excel itself writes "Tasks!$4:$4", and LibreOffice
        // ignores the first form when printing.
        private static void AbsolutePrintTitles(string filePath)
        {
            using (SpreadsheetDocument document = SpreadsheetDocument.Open(filePath, true))
            {
                Spreadsheet.DefinedNames names = document.WorkbookPart.Workbook.DefinedNames;
                if (names == null)
                    return;
                foreach (Spreadsheet.DefinedName name in names.Elements<Spreadsheet.DefinedName>().Where(n => n.Name == "_xlnm.Print_Titles"))
                    name.Text = Regex.Replace(name.Text, @"!\$?(\d+):\$?(\d+)$", m => "!$" + m.Groups[1].Value + ":$" + m.Groups[2].Value);
                document.WorkbookPart.Workbook.Save();
            }
        }

        // ---- Tables -------------------------------------------------------------

        // Every task and to-do in its own row, in the order the app shows them (TaskOrder: the open ones, then the done ones,
        // as in the PDF and Word lists); the project (or list) column only when there is more than one.
        private static XlTable TaskTable(List<ExportItem> items, bool byMemory)
        {
            List<(ExportItem Source, string Text, bool Done)> entries = items.SelectMany(i =>
                i.Project != null ? TaskOrder.OpenFirst(Parts(i.Project).Tasks, t => t.IsDone).Select(t => (i, t.Title, t.IsDone))
                : i.Todos != null ? TaskOrder.OpenFirst(i.Todos, t => t.IsDone).Select(t => (i, t.Text, t.IsDone))
                : Enumerable.Empty<(ExportItem, string, bool)>()).ToList();
            bool byProject = entries.Select(e => e.Source).Distinct().Count() > 1;

            XlTable table = new XlTable();
            if (byMemory)
                table.Column(Loc.T("Export.Column.Memory"), entries.Select(e => e.Source.Group), 30);
            if (byProject)
                table.Column(Loc.T("Export.Column.Project"), entries.Select(e => NameOrUnnamed(e.Source.Name)), 40);
            table.Column(Loc.T("Export.Column.Status"), 13);
            // A page wide in portrait; with both the memory and the project column it prints landscape (XlPrint).
            table.Column(Loc.T("Export.Column.Task"), Math.Max(60, 100 - table.Columns.Sum(c => c.Width)));
            foreach ((ExportItem source, string text, bool done) in entries)
            {
                List<object> cells = new List<object>();
                if (byMemory)
                    cells.Add(source.Group ?? "");
                if (byProject)
                    cells.Add(NameOrUnnamed(source.Name));
                cells.Add(XlStatus(done));
                cells.Add(text ?? "");
                table.Rows.Add((cells.ToArray(), done));
            }
            return table;
        }

        private static XlTable FileTable(List<ExportItem> items, bool byMemory)
        {
            List<(ExportItem Item, List<Attachment> Files)> projects = items.Where(i => i.Project != null)
                .Select(i => (Item: i, Files: Parts(i.Project).Files)).Where(p => p.Files.Count > 0).ToList();
            bool byProject = projects.Count > 1;

            XlTable table = new XlTable();
            if (byMemory)
                table.Column(Loc.T("Export.Column.Memory"), projects.Select(p => p.Item.Group), 30);
            if (byProject)
                table.Column(Loc.T("Export.Column.Project"), projects.Select(p => NameOrUnnamed(p.Item.Name)), 40);
            table.Column(Loc.T("Export.Column.File"), 40);
            table.Column(Loc.T("Export.Column.Size"), 11);
            table.Column(Loc.T("Export.Column.Added"), 14);
            foreach ((ExportItem project, List<Attachment> attachments) in projects)
                foreach (Attachment file in attachments)
                {
                    List<object> cells = new List<object>();
                    if (byMemory)
                        cells.Add(project.Group ?? "");
                    if (byProject)
                        cells.Add(NameOrUnnamed(project.Name));
                    cells.Add(NameOrUnnamed(file.DisplayName));
                    cells.Add(AttachmentStore.FormatSize(file.SizeBytes));
                    cells.Add(file.AddedDate.Date);
                    table.Rows.Add((cells.ToArray(), false));
                }
            return table;
        }

        private static XlTable DayPlanTable(List<DayPlanBlock> blocks)
        {
            XlTable table = new XlTable();
            table.Column(Loc.T("Export.Column.Date"), 13);
            table.Column(Loc.T("Export.Column.Time"), 13);
            table.Column(Loc.T("Export.Column.Block"), 32);
            table.Column(Loc.T("Export.Column.Notes"), 30);
            table.Column(Loc.T("Export.Column.Status"), 13);
            foreach (DayPlanBlock block in blocks)
                table.Rows.Add((new object[] { block.Date.Date, block.TimeRange(), block.Title ?? "", block.Notes?.Trim() ?? "", XlStatus(block.Done) },
                    block.Done));
            return table;
        }

        // "☑ Done" or "☐ Open": the word to filter by, the box to see at a glance.
        private static string XlStatus(bool done) => (done ? "☑ " : "☐ ") + Loc.T(done ? "Export.Status.Done" : "Export.Status.Open");

        private static double FitWidth(IEnumerable<string> values, string header, double max) =>
            Math.Clamp(values.Select(v => (v ?? "").Length).Append(header.Length).Max() + 3, 10, max);

        // A column per field any of the items has, in the order the item form shows them; empty columns are left out.
        // Pictures aren't exported to Excel; logs have sheets of their own (LogTables).
        private static XlTable EntityTable(List<Entity> entities)
        {
            List<string> headers = new List<string> { Loc.T("Export.Column.Name") };
            List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
            foreach (Entity entity in entities)
            {
                Dictionary<string, object> row = new Dictionary<string, object> { [headers[0]] = NameOrUnnamed(entity.Name) };
                foreach ((string label, object value) in EntityValues(entity))
                {
                    if (value is string text && string.IsNullOrWhiteSpace(text))
                        continue;
                    if (!headers.Contains(label))
                        headers.Add(label);
                    row[label] = value;
                }
                rows.Add(row);
            }

            XlTable table = new XlTable();
            foreach (string header in headers)
            {
                List<object> values = rows.Where(r => r.ContainsKey(header)).Select(r => r[header]).ToList();
                table.Column(header, values.All(v => v is DateTime) ? 13
                    : values.All(v => v is double) ? Math.Max(10, header.Length + 3)
                    : Math.Clamp(values.Select(v => v.ToString().Split('\n').Max(l => l.Length)).Append(header.Length).Max() + 3, 10, 45));
            }
            foreach (Dictionary<string, object> row in rows)
                table.Rows.Add((headers.Select(h => row.TryGetValue(h, out object v) ? v : "").ToArray(), false));
            return table;
        }

        // An item's values with their labels: a type's date fields as dates and its number fields as numbers (the unit
        // in the column's name), the rest as the text shown everywhere else; a garment all as text (KeepsOwnExport).
        private static IEnumerable<(string Label, object Value)> EntityValues(Entity entity)
        {
            IReadOnlyList<FieldDefinition> definitions = EntityTypes.Fields(entity.EntityType);
            if (KeepsOwnExport(entity))
            {
                foreach ((string label, string value) in ClothingFields(entity))
                    yield return (label, value);
                yield break;
            }

            foreach (FieldDefinition field in definitions)
            {
                string? value = field.Read(entity);
                if (!string.IsNullOrWhiteSpace(value))
                    yield return (XlLabel(field, entity), XlValue(field, value, entity));
            }
            yield return (Loc.T("Entity.Field.Notes"), entity.Notes ?? "");
        }

        private static string XlLabel(FieldDefinition field, Entity owner) =>
            field.Unit == null ? field.Label : field.Label + " (" + Units.Label(field.Unit, owner) + ")";

        // A stored value of a date or number field (culture-invariant, checked when typed in) as a date or number cell;
        // any other as it is shown.
        private static object XlValue(FieldDefinition field, string stored, Entity owner) =>
            field.Kind == ValueKind.Date || field.Kind == ValueKind.Number ? TypedValue(field.Kind, stored) : field.Show(stored, owner);

        // A stored value of a date or number field (culture-invariant, checked when typed in) as a date or number cell.
        private static object TypedValue(ValueKind kind, string stored) => kind switch
        {
            ValueKind.Date when Values.ParseDate(stored) is DateTime date => date,
            ValueKind.Number when double.TryParse(stored, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) => number,
            _ => stored
        };

        // Each kind of log the items have (service, fuel) as a table: a row per entry, newest first per item, a column per
        // log column. The fuel log's computed consumption is left out: Excel has the entries to work it out from.
        private static IEnumerable<(string Name, XlTable Table)> LogTables(List<ExportItem> items, bool byMemory)
        {
            List<ExportItem> entities = items.Where(i => i.Entity != null).ToList();
            foreach (LogDefinition log in entities.SelectMany(i => EntityTypes.Logs(i.Entity.EntityType)).Distinct())
            {
                List<(ExportItem Item, Dictionary<string, string> Row)> entries = entities
                    .SelectMany(i => EntityTypes.Sorted(log, EntityTypes.Rows(i.Entity, log)).Select(row => (Item: i, Row: row))).ToList();
                if (entries.Count == 0)
                    continue;

                XlTable table = new XlTable();
                if (byMemory)
                    table.Column(Loc.T("Export.Column.Memory"), entries.Select(e => e.Item.Group), 30);
                table.Column(Loc.T("Export.Column.Name"), entries.Select(e => NameOrUnnamed(e.Item.Name)), 30);
                foreach (LogColumn column in log.Columns)
                    table.Column(column.Label, column.Kind == ValueKind.Date ? 13
                        : column.Kind == ValueKind.Number ? Math.Max(10, column.Label.Length + 3)
                        : FitWidth(entries.Select(e => e.Row.TryGetValue(column.Key, out string v) ? v : ""), column.Label, 40));
                foreach ((ExportItem item, Dictionary<string, string> row) in entries)
                {
                    List<object> cells = new List<object>();
                    if (byMemory)
                        cells.Add(item.Group ?? "");
                    cells.Add(NameOrUnnamed(item.Name));
                    foreach (LogColumn column in log.Columns)
                        cells.Add(row.TryGetValue(column.Key, out string value) && !string.IsNullOrWhiteSpace(value) ? TypedValue(column.Kind, value) : "");
                    table.Rows.Add((cells.ToArray(), false));
                }
                yield return (log.Label, table);
            }
        }

        // Each kind of record list the items have (a vehicle's documents, wheel sets, parts) as a table: a row per record (a
        // nested one named with the ones above it), a column per field (a file by its name, specs on one line), dates and
        // numbers typed.
        private static IEnumerable<(string Name, XlTable Table)> RecordTables(List<ExportItem> items, bool byMemory)
        {
            List<ExportItem> entities = items.Where(i => i.Entity != null).ToList();
            foreach (RecordDefinition records in entities.Where(i => !KeepsOwnExport(i.Entity)).SelectMany(i => EntityTypes.Records(i.Entity.EntityType)).Distinct())
            {
                List<(ExportItem Item, EntityRecord Record, IReadOnlyList<EntityRecord> Above)> entries = entities
                    .SelectMany(i => RecordTree.Walk(records.Read(i.Entity)).Select(x => (Item: i, x.Record, x.Above))).ToList();
                if (entries.Count == 0)
                    continue;

                XlTable table = new XlTable();
                if (byMemory)
                    table.Column(Loc.T("Export.Column.Memory"), entries.Select(e => e.Item.Group), 30);
                table.Column(Loc.T("Export.Column.Name"), entries.Select(e => NameOrUnnamed(e.Item.Name)), 30);
                List<object[]> cells = entries.Select(e => records.Fields.Select(f => records.TitleKeys.Contains(f.Key) && e.Above.Count > 0
                    ? string.Join(" › ", e.Above.Select(records.Title).Append(records.Title(e.Record)))
                    : RecordCell(f, e.Record, e.Item.Entity)).ToArray()).ToList();
                for (int c = 0; c < records.Fields.Length; c++)
                {
                    FieldDefinition field = records.Fields[c];
                    List<object> column = cells.Select(row => row[c]).ToList();
                    table.Column(field.Label, column.All(v => v is DateTime || "".Equals(v)) ? 13
                        : FitWidth(column.Select(v => v.ToString()), field.Label, 40));
                }
                foreach (((ExportItem item, _, _), object[] values) in entries.Zip(cells))
                {
                    List<object> row = new List<object>();
                    if (byMemory)
                        row.Add(item.Group ?? "");
                    row.Add(NameOrUnnamed(item.Name));
                    row.AddRange(values);
                    table.Rows.Add((row.ToArray(), false));
                }
                yield return (records.Label, table);
            }
        }

        private static object RecordCell(FieldDefinition field, EntityRecord record, Entity owner)
        {
            if (field.Kind == ValueKind.File)
                return record.File?.DisplayName ?? "";
            if (field.Kind == ValueKind.Specs)
                return string.Join("; ", (record.Specs ?? new List<SpecLine>()).Where(s => s != null).Select(s => s.Name + ": " + s.Value));
            if (field.Kind == ValueKind.Pictures)
                return record.Pictures?.Count > 0 ? (object)record.Pictures.Count : "";
            string? stored = record.Get(field.Key);
            return string.IsNullOrWhiteSpace(stored) ? "" : XlValue(field, stored, owner);
        }

        private static void AddTableSheet(XLWorkbook workbook, string name, string title, string exported, XlTable table)
        {
            IXLWorksheet sheet = workbook.Worksheets.Add(SanitizeSheetName(workbook, name));
            XlTitle(sheet, title, exported);
            int columns = table.Columns.Count;

            for (int c = 0; c < columns; c++)
            {
                sheet.Column(c + 1).Width = table.Columns[c].Width;
                IXLCell header = sheet.Cell(XlHeaderRow, c + 1);
                header.Value = table.Columns[c].Header;
                header.Style.Font.SetBold().Font.SetFontColor(XLColor.FromHtml(Ink))
                    .Fill.SetBackgroundColor(XLColor.FromHtml(HeaderFill))
                    .Border.SetBottomBorder(XLBorderStyleValues.Thin).Border.SetBottomBorderColor(XLColor.FromHtml(SectionRule))
                    .Alignment.SetVertical(XLAlignmentVerticalValues.Center).Alignment.SetWrapText();
            }
            XlFitRow(sheet.Row(XlHeaderRow), table.Columns.Select(c => (c.Header, c.Width)), bold: true);

            int row = XlHeaderRow;
            foreach ((object[] cells, bool done) in table.Rows)
            {
                // A text too long for one row (Excel's limit) goes on in the rows under it, the other cells left empty.
                List<List<string>> pieces = cells.Select((value, c) => value is string text ? RowPieces(FitCell(text), table.Columns[c].Width) : null).ToList();
                int rowsNeeded = pieces.Max(p => p?.Count ?? 1);
                for (int part = 0; part < rowsNeeded; part++)
                {
                    row++;
                    for (int c = 0; c < columns; c++)
                    {
                        IXLCell cell = sheet.Cell(row, c + 1);
                        if (pieces[c] != null)
                            cell.Value = part < pieces[c].Count ? pieces[c][part] : "";
                        else if (part == 0 && cells[c] is DateTime date)
                        {
                            cell.Value = date;
                            cell.Style.DateFormat.Format = XlDateFormat();
                        }
                        else if (part == 0 && cells[c] is double number)
                            cell.Value = number;
                        cell.Style.Alignment.SetWrapText().Alignment.SetVertical(XLAlignmentVerticalValues.Top)
                            .Alignment.SetHorizontal(XLAlignmentHorizontalValues.Left)
                            .Border.SetBottomBorder(XLBorderStyleValues.Thin).Border.SetBottomBorderColor(XLColor.FromHtml(RowRule));
                        if (done)
                            cell.Style.Font.SetFontColor(XLColor.FromHtml(DoneGray));
                    }
                    XlFitRow(sheet.Row(row), pieces.Select((p, c) => (p != null && part < p.Count ? p[part] : "", table.Columns[c].Width)));
                }
            }

            sheet.Range(XlHeaderRow, 1, row, columns).SetAutoFilter();
            sheet.SheetView.FreezeRows(XlHeaderRow);
            XlPrint(sheet, table.Columns.Sum(c => c.Width), true);
        }

        // A text in pieces that each fit one row: whole lines together where they fit, a line too long on its own cut
        // between words.
        private static List<string> RowPieces(string text, double width)
        {
            if (XlLines(text, width, XlRegular) <= XlMaxLines)
                return new List<string> { text };

            int chunk = (int)(width * XlMaxLines * 0.8); // characters that surely fit in XlMaxLines lines
            List<string> pieces = new List<string>();
            string current = null;
            foreach (string line in text.Split('\n').SelectMany(l => XlLines(l, width, XlRegular) <= XlMaxLines ? new[] { l } : Chunks(l, chunk)))
            {
                string joined = current == null ? line : current + "\n" + line;
                if (current != null && XlLines(joined, width, XlRegular) > XlMaxLines)
                {
                    pieces.Add(current);
                    current = line;
                }
                else
                    current = joined;
            }
            pieces.Add(current);
            return pieces;
        }

        // ---- Descriptions and notes -----------------------------------------------------

        // A project's description and notes (notebook pages one after the other, an empty line between them); only the
        // ones with something in them.
        private static List<(string Label, List<RichLine> Lines)> ProjectText(Project project)
        {
            ProjectParts parts = Parts(project);
            return new List<(string Label, List<RichLine> Lines)>
            {
                (Loc.T("ProjectWindow.Description"), parts.Description),
                (Loc.T("ProjectWindow.Notes"), JoinPages(parts.NotePages))
            }.Where(s => s.Lines.Count > 0).ToList();
        }

        // A memory's notebook (the item is named Notebook): its pages as one Notes section.
        private static List<(string Label, List<RichLine> Lines)> NotebookText(List<NotePage> notebook)
        {
            List<RichLine> pages = JoinPages(NotebookPages(notebook));
            return pages.Count == 0 ? new List<(string, List<RichLine>)>() : new List<(string, List<RichLine>)> { (Loc.T("ProjectWindow.Notes"), pages) };
        }

        // Notebook pages one after the other, an empty line between them.
        private static List<RichLine> JoinPages(List<List<RichLine>> pages)
        {
            List<RichLine> lines = new List<RichLine>();
            foreach (List<RichLine> page in pages)
            {
                if (lines.Count > 0)
                    lines.Add(new RichLine { Blank = true, Runs = new List<System.Windows.Documents.Run>() });
                lines.AddRange(page);
            }
            return lines;
        }

        // One wide column: per project its name, then each section's label over a dark rule, then its paragraphs.
        private static void AddTextSheet(XLWorkbook workbook, string title, string exported,
            List<(ExportItem Item, List<(string Label, List<RichLine> Lines)> Sections)> projects, bool oneItem)
        {
            const double Width = 105;
            IXLWorksheet sheet = workbook.Worksheets.Add(SanitizeSheetName(workbook, Loc.T("Export.Sheet.Text")));
            XlTitle(sheet, title, exported);
            sheet.Column(1).Width = Width;

            int row = XlHeaderRow - 1;
            ExportItem previous = null;
            foreach ((ExportItem item, List<(string Label, List<RichLine> Lines)> sections) in projects)
            {
                if (StartsGroup(previous, item) && !string.IsNullOrEmpty(item.Group))
                {
                    row++; // an empty row above
                    IXLCell heading = sheet.Cell(++row, 1);
                    heading.Value = FitCell(item.Group);
                    heading.Style.Font.SetBold().Font.SetFontSize(15)
                        .Border.SetBottomBorder(XLBorderStyleValues.Thin).Border.SetBottomBorderColor(XLColor.FromHtml(Rule));
                    sheet.Row(row).Height = 24;
                }
                previous = item;

                // A single project's export already has its name as the title.
                if (!oneItem)
                {
                    IXLCell name = sheet.Cell(++row, 1);
                    name.Value = FitCell(NameOrUnnamed(item.Name));
                    name.Style.Font.SetBold().Font.SetFontSize(13).Alignment.SetVertical(XLAlignmentVerticalValues.Bottom);
                    sheet.Row(row).Height = 26;
                }

                foreach ((string label, List<RichLine> lines) in sections)
                {
                    // More room above a section than between its lines.
                    IXLCell section = sheet.Cell(++row, 1);
                    section.Value = label.ToUpper(Loc.Culture);
                    section.Style.Font.SetBold().Font.SetFontColor(XLColor.FromHtml(Ink)).Alignment.SetVertical(XLAlignmentVerticalValues.Bottom)
                        .Border.SetBottomBorder(XLBorderStyleValues.Thin).Border.SetBottomBorderColor(XLColor.FromHtml(SectionRule));
                    sheet.Row(row).Height = 26;
                    sheet.Row(++row).Height = 5; // a little air under the rule

                    foreach (RichLine line in lines)
                        XlParagraph(sheet, ref row, line, Width);
                }
                row++;
            }

            XlPrint(sheet, Width, false);
        }

        // A paragraph with its formatting; one too long for a row (Excel's limit) goes on in the rows under it, as plain
        // text.
        private static void XlParagraph(IXLWorksheet sheet, ref int row, RichLine line, double width)
        {
            if (line.Blank)
            {
                row++;
                return;
            }

            string marker = line.Marker != null ? line.Marker + " " : "";
            string plain = XmlSafe(line.Runs == null ? line.Text : marker + string.Concat(line.Runs.Select(r => r.Text)));
            int indent = (int)Math.Round(line.Indent / 22);
            double textWidth = width - indent * 3; // an indent level takes about three characters
            bool bold = line.Runs?.Any(r => r.Text.Trim().Length > 0 && RunLook(r).Bold) == true;
            List<string> pieces = RowPieces(plain, textWidth);

            if (pieces.Count > 1 || line.Runs == null)
            {
                foreach (string piece in pieces)
                {
                    IXLCell cell = sheet.Cell(++row, 1);
                    cell.Value = piece;
                    XlTextCell(cell, indent);
                    XlFitRow(sheet.Row(row), new[] { (piece, textWidth) });
                }
                return;
            }

            IXLCell rich = sheet.Cell(++row, 1);
            if (marker.Length > 0)
                rich.GetRichText().AddText(marker).SetFontColor(XLColor.FromHtml(Muted));
            foreach (System.Windows.Documents.Run run in line.Runs.Where(r => r.Text.Length > 0))
            {
                IXLRichString text = rich.GetRichText().AddText(XmlSafe(run.Text));
                (bool isBold, bool italic, bool underline, string color) = RunLook(run);
                if (isBold)
                    text.SetBold();
                if (italic)
                    text.SetItalic();
                if (underline)
                    text.SetUnderline();
                if (color != null)
                    text.SetFontColor(XLColor.FromHtml(color));
            }
            XlTextCell(rich, indent);
            XlFitRow(sheet.Row(row), new[] { (plain, textWidth) }, bold);
        }

        private static void XlTextCell(IXLCell cell, int indent)
        {
            cell.Style.Alignment.SetWrapText().Alignment.SetVertical(XLAlignmentVerticalValues.Top);
            if (indent > 0)
                cell.Style.Alignment.SetIndent(indent);
        }

        // Pieces of at most `size` characters, cut at a space where there is one, never between the halves of an emoji.
        private static IEnumerable<string> Chunks(string text, int size)
        {
            int start = 0;
            while (text.Length - start > size)
            {
                int cut = text.LastIndexOf(' ', start + size, size);
                if (cut <= start)
                {
                    cut = start + size;
                    if (char.IsHighSurrogate(text[cut - 1]))
                        cut--;
                }
                yield return text.Substring(start, cut - start);
                start = text[cut] == ' ' ? cut + 1 : cut;
            }
            yield return text.Substring(start);
        }

        // ---- Sheet setup -------------------------------------------------------------

        private static void XlTitle(IXLWorksheet sheet, string title, string exported)
        {
            sheet.Cell(1, 1).Value = FitCell(title);
            sheet.Cell(1, 1).Style.Font.SetBold().Font.SetFontSize(16).Font.SetFontColor(XLColor.FromHtml(Ink));
            sheet.Row(1).Height = 24;
            sheet.Cell(2, 1).Value = exported;
            sheet.Cell(2, 1).Style.Font.SetFontSize(9).Font.SetFontColor(XLColor.FromHtml(Muted));
        }

        // A4, one page wide (as many pages tall as it takes), the table's header on every page, numbered pages.
        private static void XlPrint(IXLWorksheet sheet, double width, bool repeatHeader)
        {
            sheet.PageSetup.PaperSize = XLPaperSize.A4Paper;
            sheet.PageSetup.PageOrientation = width > 110 ? XLPageOrientation.Landscape : XLPageOrientation.Portrait;
            sheet.PageSetup.FitToPages(1, 0);
            sheet.PageSetup.Margins.SetLeft(0.5).SetRight(0.5).SetTop(0.6).SetBottom(0.7).SetHeader(0.3).SetFooter(0.3);
            if (repeatHeader)
                sheet.PageSetup.SetRowsToRepeatAtTop(XlHeaderRow, XlHeaderRow);
            sheet.PageSetup.Footer.Center.AddText(XLHFPredefinedText.PageNumber, XLHFOccurrence.AllPages);
            sheet.PageSetup.Footer.Center.AddText(" / ", XLHFOccurrence.AllPages);
            sheet.PageSetup.Footer.Center.AddText(XLHFPredefinedText.NumberOfPages, XLHFOccurrence.AllPages);
        }

        // The language's numeric short date ("2026. 10. 17.", "17.10.2026.", "10/17/2026") as an Excel date format.
        private static string XlDateFormat() => Loc.Culture.DateTimeFormat.ShortDatePattern.Replace('M', 'm');

        // Excel doesn't size rows to wrapped text written by a program: the row gets the height its tallest cell needs,
        // measured in Calibri 11 (Excel's default font) at the column's width.
        private static void XlFitRow(IXLRow row, IEnumerable<(string Text, double Width)> cells, bool bold = false)
        {
            int lines = cells.Select(c => XlLines(c.Text, c.Width, bold ? XlBold : XlRegular)).DefaultIfEmpty(1).Max();
            if (lines > 1)
                row.Height = Math.Min(lines * XlLineHeight, 409); // Excel's limit; RowPieces keeps text under it
        }

        private static readonly Typeface XlRegular = new Typeface("Calibri");
        private static readonly Typeface XlBold = new Typeface(new FontFamily("Calibri"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        private const double XlFontPixels = 15; // Excel draws 11 pt Calibri 15 pixels high (not 14.67) on a 96-dpi screen

        private static int XlLines(string text, double widthChars, Typeface font)
        {
            if (string.IsNullOrEmpty(text))
                return 1;
            // Excel's column width in pixels for Calibri 11 (7 pixels a character, 5 of padding), less the cell's margins.
            double pixels = Math.Truncate(widthChars * 7 + 5) - 7;
            FormattedText formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, font,
                XlFontPixels, Brushes.Black, 1.0) { MaxTextWidth = pixels };
            return Math.Max(1, (int)Math.Ceiling(formatted.Height / (font.FontFamily.LineSpacing * XlFontPixels) - 0.01));
        }

        // User text as a cell can hold it: without the characters XML can't store (XmlSafe), and at most 32,767
        // characters, past which ClosedXML throws, failing the whole export: longer text is cut, and says so. Never cut
        // between the halves of an emoji.
        private const int MaxCellText = 32000;

        private static string FitCell(string text)
        {
            text = XmlSafe(text);
            if (text.Length <= MaxCellText)
                return text;
            int keep = char.IsHighSurrogate(text[MaxCellText - 1]) ? MaxCellText - 1 : MaxCellText;
            return text.Substring(0, keep) + " … " + Loc.T("Export.CutMark");
        }

        private static string SanitizeSheetName(XLWorkbook workbook, string name)
        {
            // Excel also refuses a name starting or ending with an apostrophe, and compares names ignoring case, so
            // memories called "Work" and "work" need different sheet names.
            char[] invalid = { '\\', '/', '*', '?', ':', '[', ']' };
            string clean = new string((name ?? Loc.T("Export.Sheet")).Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c)
                .ToArray()).Trim('\'');

            if (clean.Length > 31)
                clean = clean.Substring(0, 31).TrimEnd('\'');
            if (string.IsNullOrWhiteSpace(clean))
                clean = Loc.T("Export.Sheet");

            string result = clean;
            int suffix = 1;

            while (workbook.Worksheets.Any(ws => string.Equals(ws.Name, result, StringComparison.OrdinalIgnoreCase)))
            {
                string suffixText = " (" + suffix + ")";
                int baseLength = Math.Min(clean.Length, 31 - suffixText.Length);
                result = clean.Substring(0, baseLength).TrimEnd('\'') + suffixText;
                suffix++;
            }

            return result;
        }
    }
}
