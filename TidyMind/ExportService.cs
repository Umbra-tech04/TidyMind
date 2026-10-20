using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using QuestPDF.Infrastructure;

namespace TidyMind
{
    public enum ExportFormat
    {
        Excel,
        Docx,
        Pdf
    }

    // Every export (a project, an item, a memory, everything) is a list of items; each format lays them out its own way:
    // ExportService.Pdf.cs, ExportService.Docx.cs, ExportService.Excel.cs. What they share is here and in
    // ExportService.RichText.cs.
    public static partial class ExportService
    {
        static ExportService()
        {
            QuestPDF.Settings.License = LicenseType.Community;
            QuestPDF.Settings.UseSystemFonts = true; // the fallback fonts after Lato (PdfFonts)
        }

        // The colors of every format. Exports must print well in grayscale: done and open never differ by color alone.
        private const string Ink = "#1F2328";
        private const string Muted = "#6B6F76";
        private const string Faint = "#8A8F96";
        private const string Rule = "#D5D8DC";
        private const string SectionRule = "#4A4F55";

        // Labels are in the language shown when the export is made; the file's layout is the same in every language.
        private class ExportItem
        {
            public Profile Memory;        // Export all: the memory it comes from (two memories may share a name)
            public string Group;          // the heading it goes under: the memory's name, or the day plan
            public string Name;
            public string ImagePath;      // an item's picture
            public List<(string Label, string Value)> Fields;
            public Project Project;       // a project: each format lays out its tasks, notes and files itself
            public List<TodoEntry> Todos; // a memory's to-do list, shown as a checklist
            public List<NotePage> Notebook; // a memory's own notebook, its pages as a project's notes
            public Entity Entity;         // an item: Excel puts its values in typed columns
            public List<DayPlanBlock> Blocks; // a planned day: Excel lists its blocks in a table
        }

        public static void ExportMemory(Profile profile, string filePath, ExportFormat format)
        {
            List<ExportItem> items = profile.Type == ProfileType.Collection ? BuildEntityItems(profile) : BuildProjectItems(profile);
            WriteExport(items, NameOrUnnamed(profile.Name), filePath, format);
        }

        public static void ExportAll(List<Profile> profiles, string filePath, ExportFormat format)
        {
            List<ExportItem> items = new List<ExportItem>();

            foreach (Profile profile in profiles)
            {
                if (profile.Type == ProfileType.Collection)
                    items.AddRange(BuildEntityItems(profile, grouped: true));
                else
                    items.AddRange(BuildProjectItems(profile, grouped: true));
            }
            items.AddRange(BuildDayPlanItems());

            WriteExport(items, Loc.T("Export.Title.All"), filePath, format);
        }

        // A single card (its right-click menu): the same layout as in a whole-memory export, just that one card.
        public static void ExportProject(Project project, string filePath, ExportFormat format)
        {
            WriteExport(new List<ExportItem> { ProjectItem(project) }, NameOrUnnamed(project.Name), filePath, format, oneItem: true);
        }

        public static void ExportEntity(Entity entity, string filePath, ExportFormat format)
        {
            WriteExport(new List<ExportItem> { EntityItem(entity) }, NameOrUnnamed(entity.Name), filePath, format, oneItem: true);
        }

        // All three are titled with the name of what is exported, with the same date line under it. `oneItem`: the
        // export is that single item (a project's or an item's own menu), so its name is the title and isn't repeated.
        private static void WriteExport(List<ExportItem> items, string title, string filePath, ExportFormat format, bool oneItem = false)
        {
            string exported = Loc.F("Export.Exported", DateTime.Now.ToString(Loc.T("Format.LongDate"), Loc.Culture));
            switch (format)
            {
                case ExportFormat.Excel:
                    WriteXlsx(items, title, exported, oneItem, filePath);
                    break;
                case ExportFormat.Docx:
                    WriteDocx(items, title, exported, oneItem, filePath);
                    break;
                case ExportFormat.Pdf:
                    WritePdf(items, title, exported, oneItem, filePath);
                    break;
            }
        }

        // ---- Day plan -----------------------------------------------------

        public static bool HasDayPlan() => DayPlanStore.TryLoad(out DayPlanFile plan) && plan.Blocks.Count > 0;

        // One item per planned day, in date order, a line per block. An unreadable dayplan.json is left out, like a
        // memory's unreadable to-do list.
        private static List<ExportItem> BuildDayPlanItems()
        {
            if (!DayPlanStore.TryLoad(out DayPlanFile plan))
                return new List<ExportItem>();

            return plan.Blocks.Select(b => b.Date.Date).Distinct().OrderBy(d => d)
                .Select(day =>
                {
                    List<DayPlanBlock> blocks = DayPlanStore.BlocksOn(plan, day).ToList();
                    return new ExportItem
                    {
                        Group = Loc.T("Plan.Name"),
                        Name = day.ToString(Loc.T("Format.FullDate"), Loc.Culture),
                        Fields = blocks.Select(b => (b.TimeRange(), DescribeBlock(b))).ToList(),
                        Blocks = blocks
                    };
                })
                .ToList();
        }

        private static string DescribeBlock(DayPlanBlock block) =>
            (block.Done ? Loc.F("Export.BlockDone", block.Title) : block.Title)
            + (string.IsNullOrWhiteSpace(block.Notes) ? "" : " — " + block.Notes.Trim());

        // ---- Memories, projects and items ----------------------------------------

        // The memory's notebook and inline to-do list (if it has them) come first, then its projects. `grouped` (export
        // all): under the memory's heading.
        private static List<ExportItem> BuildProjectItems(Profile memory, bool grouped = false)
        {
            List<ExportItem> items = NotebookItems(memory);

            List<TodoEntry> todos = LoadTodos(memory);
            if (todos.Count > 0)
                items.Add(new ExportItem { Name = Loc.T("Home.Todo.Title"), Todos = todos });

            foreach (Project project in ProjectStore.Load(memory))
                items.Add(ProjectItem(project));

            return grouped ? UnderMemory(items, memory) : items;
        }

        private static ExportItem ProjectItem(Project project)
        {
            return new ExportItem { Name = project.Name, Project = project };
        }

        private static List<ExportItem> BuildEntityItems(Profile memory, bool grouped = false)
        {
            List<ExportItem> items = NotebookItems(memory);
            items.AddRange(EntityStore.Load(memory).Select(EntityItem));
            return grouped ? UnderMemory(items, memory) : items;
        }

        private static List<ExportItem> UnderMemory(List<ExportItem> items, Profile memory)
        {
            foreach (ExportItem item in items)
            {
                item.Memory = memory;
                item.Group = NameOrUnnamed(memory.Name);
            }
            return items;
        }

        private static ExportItem EntityItem(Entity entity)
        {
            return new ExportItem
            {
                Name = entity.Name,
                Entity = entity,
                ImagePath = EntityImages.FullPath(entity),
                // A vehicle: its fields, notes, records and logs. A garment (and a type this version doesn't know): the
                // fields and sizes as its exports have always shown them.
                Fields = KeepsOwnExport(entity) ? ClothingFields(entity) : EntityTypes.Describe(entity)
            };
        }

        private static bool KeepsOwnExport(Entity entity) =>
            entity.EntityType == EntityTypes.Clothing || EntityTypes.Tabs(entity.EntityType).Count == 0;

        // A garment, as label and text.
        private static List<(string Label, string Value)> ClothingFields(Entity entity) => new List<(string, string)>
        {
            (Loc.T("Entity.Field.Brand"), entity.Brand ?? ""),
            (Loc.T("Entity.Field.Color"), entity.Color ?? ""),
            (Loc.T("Entity.Field.Material"), Choices.Label(entity.Material)),
            (Loc.T("Entity.Field.Season"), Choices.Label(entity.Season)),
            (Loc.T("Entity.Field.PurchasePrice"), entity.PurchasePrice ?? ""),
            (Loc.T("Entity.Sizes"), string.Join("\n", (entity.Sizes ?? new List<SizeQuantity>())
                .Select(sq => Loc.F("Export.SizeRow", sq.Size, Choices.Label(sq.Condition), sq.Quantity)))),
            (Loc.T("Entity.Field.Notes"), entity.Notes ?? "")
        };

        // The memory's notebook, if anything is written in it. An unreadable file just leaves it out, like the to-do list.
        private static List<ExportItem> NotebookItems(Profile memory)
        {
            List<ExportItem> items = new List<ExportItem>();
            if (MemoryNotebookStore.TryLoad(memory, out MemoryNotebook notebook, out _) && NotebookPages(notebook.Pages).Count > 0)
                items.Add(new ExportItem { Name = Loc.T("Notebook.Title"), Notebook = notebook.Pages });
            return items;
        }

        // An unreadable file just leaves the to-do list out of the export.
        private static List<TodoEntry> LoadTodos(Profile memory)
        {
            return MemoryTodoService.TryLoad(memory, out List<TodoEntry> todos) ? todos : new List<TodoEntry>();
        }

        // ---- Shared by the formats ------------------------------------------------

        private static string NameOrUnnamed(string name) => string.IsNullOrWhiteSpace(name) ? Loc.T("Export.Unnamed") : name;

        // Whether `item` starts a new heading (a memory in export all, or the day plan).
        private static bool StartsGroup(ExportItem previous, ExportItem item) =>
            previous == null || previous.Memory != item.Memory || previous.Group != item.Group;

        // A project's sections as every format shows them; empty ones are left out.
        private sealed class ProjectParts
        {
            public List<RichLine> Description;
            public List<TaskItem> Tasks;
            public List<List<RichLine>> NotePages; // notebook pages with something on them
            public List<Attachment> Files;

            public bool IsEmpty => Description.Count == 0 && Tasks.Count == 0 && NotePages.Count == 0 && Files.Count == 0;
        }

        private static ProjectParts Parts(Project project) => new ProjectParts
        {
            Description = RichLines(project.Description),
            Tasks = project.Tasks?.Where(t => t != null).ToList() ?? new List<TaskItem>(),
            NotePages = NotebookPages(project.PagesOrLegacyNote()),
            Files = project.Attachments?.Where(a => a != null).ToList() ?? new List<Attachment>()
        };

        // Notebook pages with something on them, as lines (a project's notes, a memory's notebook).
        private static List<List<RichLine>> NotebookPages(List<NotePage> pages) =>
            pages.Where(p => p != null).Select(p => RichLines(p.Body)).Where(p => p.Count > 0).ToList();

        // Open entries in their own order, then the done ones (shown under a "Done" label when there are open ones too):
        // what is left to do reads first.
        private static (List<string> Open, List<string> Done) SplitChecklist(IEnumerable<(string Text, bool Done)> entries)
        {
            List<(string Text, bool Done)> all = entries.ToList();
            return (all.Where(e => !e.Done).Select(e => e.Text ?? "").ToList(), all.Where(e => e.Done).Select(e => e.Text ?? "").ToList());
        }

        // The label/value lines shown for an item or a planned day: the empty ones left out, and an item's picture path
        // when the picture itself can't be shown.
        private static List<(string Label, string Value)> FieldsShown(ExportItem item, byte[]? png)
        {
            List<(string Label, string Value)> fields = item.Fields.Where(f => !string.IsNullOrWhiteSpace(f.Value)).ToList();
            if (png == null && item.Entity != null && !string.IsNullOrEmpty(item.ImagePath))
                fields.Add((Loc.T("Export.ImagePath"), item.ImagePath));
            return fields;
        }

        // Text without the characters XML can't hold (control characters pasted in from elsewhere, a lone half of an
        // emoji): writing them would fail a Word or Excel export.
        private static string XmlSafe(string text)
        {
            if (text.All(XmlConvert.IsXmlChar))
                return text;
            StringBuilder clean = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                if (XmlConvert.IsXmlChar(text[i]))
                    clean.Append(text[i]);
                else if (i + 1 < text.Length && XmlConvert.IsXmlSurrogatePair(text[i + 1], text[i]))
                    clean.Append(text[i]).Append(text[++i]);
            }
            return clean.ToString();
        }

        // The item's picture as PNG, or null if there is none or it can't be read (damaged, or a format the Word and
        // PDF libraries don't take, such as WebP): that item is then exported without its picture instead of the
        // whole export failing. Read through WPF, which decodes every format the app shows, so both writers always
        // get a plain PNG.
        private static byte[]? PictureAsPng(ExportItem item)
        {
            if (string.IsNullOrEmpty(item.ImagePath) || !File.Exists(item.ImagePath))
                return null;

            try
            {
                System.Windows.Media.Imaging.BitmapImage bitmap = ImageCache.Decode(item.ImagePath, 400);
                if (bitmap == null)
                    return null;

                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using (MemoryStream png = new MemoryStream())
                {
                    encoder.Save(png);
                    return png.ToArray();
                }
            }
            catch (Exception)
            {
                // A damaged file can fail inside the decoder in ways Decode doesn't expect (e.g. a COMException);
                // whatever it is, it only means "no picture" here.
                return null;
            }
        }
    }
}
