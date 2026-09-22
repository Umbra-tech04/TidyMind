using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ClosedXML.Excel;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using Xceed.Document.NET;
using Xceed.Words.NET;

namespace TidyMind
{
    public enum ExportFormat
    {
        Excel,
        Docx,
        Pdf
    }

    public static class ExportService
    {
        static ExportService()
        {
            QuestPDF.Settings.License = LicenseType.Community;
        }

        private class ExportItem
        {
            public string Group;
            public string Name;
            public string ImagePath;
            public List<(string Label, string Value)> Fields;
        }

        public static void ExportMemory(Profile profile, string filePath, ExportFormat format)
        {
            if (format == ExportFormat.Excel)
            {
                using (XLWorkbook workbook = new XLWorkbook())
                {
                    AddMemorySheet(workbook, profile);
                    workbook.SaveAs(filePath);
                }
                return;
            }

            List<ExportItem> items;
            string title;

            if (profile.Type == ProfileType.Collection)
            {
                items = BuildEntityItems(LoadEntities(profile.Name));
                title = profile.Name + " — Collection Export";
            }
            else
            {
                items = BuildProjectItems(LoadProjects(profile.Name));
                title = profile.Name + " — Project Export";
            }

            WriteExport(items, title, filePath, format);
        }

        public static void ExportAll(List<Profile> profiles, string filePath, ExportFormat format)
        {
            if (format == ExportFormat.Excel)
            {
                using (XLWorkbook workbook = new XLWorkbook())
                {
                    foreach (Profile profile in profiles)
                        AddMemorySheet(workbook, profile);
                    workbook.SaveAs(filePath);
                }
                return;
            }

            List<ExportItem> items = new List<ExportItem>();

            foreach (Profile profile in profiles)
            {
                if (profile.Type == ProfileType.Collection)
                    items.AddRange(BuildEntityItems(LoadEntities(profile.Name), profile.Name));
                else
                    items.AddRange(BuildProjectItems(LoadProjects(profile.Name), profile.Name));
            }

            WriteExport(items, "TidyMind — Export All", filePath, format);
        }

        private static List<Project> LoadProjects(string profileName)
        {
            string fileName = profileName + ".json";
            if (!File.Exists(fileName))
                return new List<Project>();

            return JsonSerializer.Deserialize<List<Project>>(File.ReadAllText(fileName));
        }

        private static List<Entity> LoadEntities(string profileName)
        {
            string fileName = profileName + "_entities.json";
            if (!File.Exists(fileName))
                return new List<Entity>();

            return JsonSerializer.Deserialize<List<Entity>>(File.ReadAllText(fileName));
        }

        private static List<ExportItem> BuildProjectItems(List<Project> projects, string group = null)
        {
            List<ExportItem> items = new List<ExportItem>();

            foreach (Project project in projects)
            {
                ExportItem item = new ExportItem();
                item.Group = group;
                item.Name = project.Name;
                item.Fields = new List<(string, string)>
                {
                    ("Description", project.Description ?? ""),
                    ("Status", project.Status.ToString()),
                    ("Tasks", FormatTasks(project.Tasks)),
                    ("Notes", project.Notes ?? "")
                };

                items.Add(item);
            }

            return items;
        }

        private static List<ExportItem> BuildEntityItems(List<Entity> entities, string group = null)
        {
            List<ExportItem> items = new List<ExportItem>();

            foreach (Entity entity in entities)
            {
                ExportItem item = new ExportItem();
                item.Group = group;
                item.Name = entity.Name;
                item.ImagePath = entity.ImagePath;
                item.Fields = new List<(string, string)>
                {
                    ("Brand", entity.Brand ?? ""),
                    ("Color", entity.Color ?? ""),
                    ("Material", entity.Material ?? ""),
                    ("Season", entity.Season ?? ""),
                    ("Purchase Price", entity.PurchasePrice ?? ""),
                    ("Sizes", FormatSizes(entity.Sizes)),
                    ("Notes", entity.Notes ?? ""),
                    ("Image Path", entity.ImagePath ?? "")
                };

                items.Add(item);
            }

            return items;
        }

        private static string FormatTasks(List<TaskItem> tasks)
        {
            if (tasks == null || tasks.Count == 0)
                return "";

            List<string> parts = new List<string>();
            foreach (TaskItem task in tasks)
                parts.Add(task.Title + " (" + (task.IsDone ? "done" : "undone") + ")");

            return string.Join("; ", parts);
        }

        private static string FormatSizes(List<SizeQuantity> sizes)
        {
            if (sizes == null || sizes.Count == 0)
                return "";

            List<string> parts = new List<string>();
            foreach (SizeQuantity sq in sizes)
                parts.Add(sq.Size + "/" + sq.Condition + " x" + sq.Quantity);

            return string.Join("; ", parts);
        }

        private static void WriteExport(List<ExportItem> items, string title, string filePath, ExportFormat format)
        {
            switch (format)
            {
                case ExportFormat.Docx:
                    WriteDocx(items, title, filePath);
                    break;
                case ExportFormat.Pdf:
                    WritePdf(items, title, filePath);
                    break;
            }
        }

        // Excel export: one worksheet per memory, columns matched to that memory's
        // type (Projects vs Collections), rather than forcing everything through the
        // shared Fields list the Docx/Pdf writers use — that's what lets a Collection's
        // variable number of sizes become their own dedicated columns.
        private static void AddMemorySheet(XLWorkbook workbook, Profile profile)
        {
            if (profile.Type == ProfileType.Collection)
                AddEntitySheet(workbook, profile.Name, LoadEntities(profile.Name));
            else
                AddProjectSheet(workbook, profile.Name, LoadProjects(profile.Name));
        }

        // Card layout: each entity/project is its own vertical block — a merged,
        // dark title row with the name, then one Label/Value row per field — instead
        // of one wide row per item. Only 2 columns exist on the whole sheet.
        private static void AddEntitySheet(XLWorkbook workbook, string sheetTitle, List<Entity> entities)
        {
            IXLWorksheet sheet = workbook.Worksheets.Add(SanitizeSheetName(workbook, sheetTitle));
            int row = 1;

            foreach (Entity entity in entities)
            {
                WriteCardHeader(sheet, ref row, entity.Name);

                WriteCardField(sheet, ref row, "Brand", entity.Brand);
                WriteCardField(sheet, ref row, "Color", entity.Color);
                WriteCardField(sheet, ref row, "Material", entity.Material);
                WriteCardField(sheet, ref row, "Season", entity.Season);
                WriteCardField(sheet, ref row, "Purchase Price", entity.PurchasePrice);
                WriteCardField(sheet, ref row, "Notes", entity.Notes);
                WriteSizeRows(sheet, ref row, entity.Sizes);

                row += 2;
            }

            FinalizeCardSheet(sheet);
        }

        private static void AddProjectSheet(XLWorkbook workbook, string sheetTitle, List<Project> projects)
        {
            IXLWorksheet sheet = workbook.Worksheets.Add(SanitizeSheetName(workbook, sheetTitle));
            int row = 1;

            foreach (Project project in projects)
            {
                WriteCardHeader(sheet, ref row, project.Name);

                WriteCardField(sheet, ref row, "Description", project.Description);
                WriteCardField(sheet, ref row, "Status", project.Status.ToString());
                WriteCardField(sheet, ref row, "Tasks", FormatTasks(project.Tasks));
                WriteCardField(sheet, ref row, "Notes", project.Notes);

                row += 2;
            }

            FinalizeCardSheet(sheet);
        }

        private static void WriteCardHeader(IXLWorksheet sheet, ref int row, string name)
        {
            sheet.Range(row, 1, row, 2).Merge();

            IXLCell cell = sheet.Cell(row, 1);
            cell.Value = string.IsNullOrWhiteSpace(name) ? "(unnamed)" : name;
            cell.Style.Font.Bold = true;
            cell.Style.Font.FontSize = 13;
            cell.Style.Font.FontColor = XLColor.White;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#252526");
            cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;

            row++;
        }

        private static void WriteCardField(IXLWorksheet sheet, ref int row, string label, string value)
        {
            StyleCardLabel(sheet, row, label);
            sheet.Cell(row, 2).Value = string.IsNullOrEmpty(value) ? "-" : value;
            row++;
        }

        private static void WriteSizeRows(IXLWorksheet sheet, ref int row, List<SizeQuantity> sizes)
        {
            if (sizes == null || sizes.Count == 0)
            {
                WriteCardField(sheet, ref row, "Sizes", "-");
                return;
            }

            bool first = true;
            foreach (SizeQuantity sq in sizes)
            {
                StyleCardLabel(sheet, row, first ? "Sizes" : "");
                sheet.Cell(row, 2).Value = "Size " + sq.Size + " - " + sq.Condition + " x" + sq.Quantity;
                first = false;
                row++;
            }
        }

        private static void StyleCardLabel(IXLWorksheet sheet, int row, string label)
        {
            IXLCell cell = sheet.Cell(row, 1);
            cell.Value = label;
            cell.Style.Font.Bold = true;
            cell.Style.Font.FontColor = XLColor.FromHtml("#666666");
            cell.Style.Fill.BackgroundColor = row % 2 == 0 ? XLColor.FromHtml("#F2F2F2") : XLColor.White;
        }

        private static void FinalizeCardSheet(IXLWorksheet sheet)
        {
            sheet.Columns(1, 2).AdjustToContents();
        }

        private static string SanitizeSheetName(XLWorkbook workbook, string name)
        {
            char[] invalid = { '\\', '/', '*', '?', ':', '[', ']' };
            string clean = new string((name ?? "Sheet").Select(c => invalid.Contains(c) ? '_' : c).ToArray());

            if (string.IsNullOrWhiteSpace(clean))
                clean = "Sheet";
            if (clean.Length > 31)
                clean = clean.Substring(0, 31);

            string result = clean;
            int suffix = 1;

            while (workbook.Worksheets.Any(ws => ws.Name == result))
            {
                string suffixText = " (" + suffix + ")";
                int baseLength = Math.Min(clean.Length, 31 - suffixText.Length);
                result = clean.Substring(0, baseLength) + suffixText;
                suffix++;
            }

            return result;
        }

        private static bool HasImage(ExportItem item)
        {
            return !string.IsNullOrEmpty(item.ImagePath) && File.Exists(item.ImagePath);
        }

        private static void WriteDocx(List<ExportItem> items, string title, string filePath)
        {
            using (Xceed.Document.NET.Document document = DocX.Create(filePath))
            {
                document.InsertParagraph(title).FontSize(20).Bold();
                document.InsertParagraph();

                string currentGroup = null;

                foreach (ExportItem item in items)
                {
                    if (item.Group != currentGroup)
                    {
                        currentGroup = item.Group;
                        if (!string.IsNullOrEmpty(currentGroup))
                        {
                            document.InsertParagraph();
                            document.InsertParagraph(currentGroup).FontSize(16).Bold();
                        }
                    }

                    document.InsertParagraph();

                    if (HasImage(item))
                    {
                        // Image on the left, fields on the right — same idea as the
                        // entity detail overlay in CollectionWindow.
                        Xceed.Document.NET.Image image = document.AddImage(item.ImagePath);
                        Picture picture = image.CreatePicture(120, 120);

                        Xceed.Document.NET.Table table = document.AddTable(1, 2);
                        table.Design = TableDesign.None;
                        table.AutoFit = AutoFit.Fixed;
                        table.SetWidths(new float[] { 130f, 380f });

                        table.Rows[0].Cells[0].Paragraphs[0].AppendPicture(picture);

                        Cell infoCell = table.Rows[0].Cells[1];
                        infoCell.Paragraphs[0].Append(item.Name).FontSize(13).Bold();
                        AppendFields(infoCell, item.Fields);

                        document.InsertTable(table);
                    }
                    else
                    {
                        document.InsertParagraph(item.Name).FontSize(13).Bold();
                        AppendFields(document, item.Fields);
                    }
                }

                document.Save();
            }
        }

        // Container is Document or Cell — both expose InsertParagraph the same way.
        private static void AppendFields(Container container, List<(string Label, string Value)> fields)
        {
            foreach (var field in fields)
            {
                if (field.Label == "Image Path")
                    continue;

                Paragraph p = container.InsertParagraph();
                p.Append(field.Label + ": ").Bold();
                p.Append(string.IsNullOrEmpty(field.Value) ? "-" : field.Value);
            }
        }

        // Renders with QuestPDF instead of PDFsharp — PDFsharp's manual page/graphics
        // handling (XGraphics, raw page sizing, font resolution) was crashing the app.
        // QuestPDF's layout model handles pagination and text wrapping itself, so none
        // of that bookkeeping is needed here.
        private static void WritePdf(List<ExportItem> items, string title, string filePath)
        {
            QuestPDF.Fluent.Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(30);
                    page.DefaultTextStyle(x => x.FontSize(10));

                    page.Content().Column(column =>
                    {
                        column.Spacing(6);

                        column.Item().Text(title).FontSize(18).Bold();

                        string currentGroup = null;

                        foreach (ExportItem item in items)
                        {
                            if (item.Group != currentGroup)
                            {
                                currentGroup = item.Group;
                                if (!string.IsNullOrEmpty(currentGroup))
                                    column.Item().PaddingTop(12).Text(currentGroup).FontSize(14).Bold();
                            }

                            if (HasImage(item))
                            {
                                // Image on the left, fields on the right — same idea as the
                                // entity detail overlay in CollectionWindow.
                                column.Item().PaddingTop(8).Row(row =>
                                {
                                    row.ConstantItem(80).Height(80).Image(item.ImagePath).FitArea();
                                    row.ConstantItem(12);

                                    row.RelativeItem().Column(info =>
                                    {
                                        info.Spacing(4);
                                        info.Item().Text(item.Name).FontSize(12).Bold();

                                        foreach (var field in item.Fields)
                                        {
                                            if (field.Label == "Image Path")
                                                continue;

                                            string value = string.IsNullOrEmpty(field.Value) ? "-" : field.Value;

                                            info.Item().Text(text =>
                                            {
                                                text.Span(field.Label + ": ").Bold();
                                                text.Span(value);
                                            });
                                        }
                                    });
                                });
                            }
                            else
                            {
                                column.Item().PaddingTop(8).Text(item.Name).FontSize(12).Bold();

                                foreach (var field in item.Fields)
                                {
                                    string value = string.IsNullOrEmpty(field.Value) ? "-" : field.Value;

                                    column.Item().Text(text =>
                                    {
                                        text.Span(field.Label + ": ").Bold();
                                        text.Span(value);
                                    });
                                }
                            }
                        }
                    });
                });
            }).GeneratePdf(filePath);
        }
    }
}
