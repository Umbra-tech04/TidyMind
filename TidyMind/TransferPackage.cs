using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Net;
using System.Threading;
using System.Xml;

namespace TidyMind
{
    // A .tidymind file: one project, one collection memory or one collection item, to take to another PC ("Export for
    // transfer", then Settings > Import there). Unlike a backup it holds one thing, and unlike the Excel/Word/PDF
    // exports it is read back in. A ZIP of:
    //   manifest.json                       what it is (TransferManifest)
    //   project.json                        a project, as projects.json stores it
    //   files/attachments/, files/card/     its attached files and card picture
    //   collection.json                     a collection: its entry in profiles.json,
    //   tabs.json, entities.json            its tabs and items, as stored
    //   files/items/, files/background/     its item pictures and page background
    //   item.json, files/items/             one item, as entities.json stores it, and its picture
    // Files go in under their stored names only, never a path from this PC. Reminders, Day Plan blocks and the
    // memory to-do list belong to no project or collection, so none are in a package.
    //
    // On import a package is untrusted: every entry and the JSON are checked before anything is written (Open), the
    // files are unpacked into a folder of their own in the data folder (Stage), and only then is the new copy added,
    // with new Ids and new file names for everything (CommitProject / CommitCollection / CommitItem). Any failure
    // adds nothing.
    public static class TransferPackage
    {
        public const string Extension = ".tidymind";

        // The newest format this version reads; a package in a newer one is refused. Each package is written in the
        // oldest format that holds it, so older versions keep reading projects and collections (format 1), and say
        // "made with a newer TidyMind" for an item (format 2, which they don't know).
        public const int FormatVersion = 2;
        private const int ProjectFormat = 1, CollectionFormat = 1, ItemFormat = 2;

        public const string ProjectType = "project";
        public const string CollectionType = "collection";
        public const string ItemType = "item";

        private const string ManifestApp = "TidyMind";
        private const string ManifestEntry = "manifest.json";
        private const string ProjectEntry = "project.json";
        private const string CollectionEntry = "collection.json";
        private const string TabsEntry = "tabs.json";
        private const string EntitiesEntry = "entities.json";
        private const string ItemEntry = "item.json";
        private const string FilesDir = "files/";
        internal const string AttachmentsDir = "files/attachments/";
        internal const string CardDir = "files/card/";
        internal const string ItemsDir = "files/items/";
        internal const string BackgroundDir = "files/background/";

        // Limits for a package from elsewhere: far above anything real, low enough that a crafted ZIP can't fill the
        // disk or the memory. Attachments can be large, so the total is generous; the JSON is never that big.
        internal const int MaxEntries = 20000;
        internal const long MaxJsonBytes = 64L * 1024 * 1024;
        internal const long MaxTotalBytes = 64L * 1024 * 1024 * 1024;

        private static readonly Regex HexColor = new Regex("^#[0-9A-Fa-f]{6}$");
        private static readonly Regex SafeExtension = new Regex(@"^\.[A-Za-z0-9]{1,10}$");

        // ---- Export -------------------------------------------------------

        // The project as given (Project Details passes what it shows, unsaved edits included). Missing files are left
        // out; their entries stay, as they are on this PC. Written next to the target and moved into place once
        // complete. Throws if it couldn't be written; OperationCanceledException if cancelled. Runs on a worker
        // thread, so writtenPages (WrittenPages, which reads the formatted pages) is counted on the UI thread first.
        public static void ExportProject(Project project, int writtenPages, string zipPath, IProgress<double>? progress,
            CancellationToken cancel)
        {
            List<(string Entry, string Source)> files = new List<(string, string)>();
            foreach (Attachment attachment in (project.Attachments ?? new List<Attachment>()).Where(AttachmentStore.Exists))
                files.Add((AttachmentsDir + Path.GetFileName(attachment.StoredFileName), AttachmentStore.PathOf(attachment.StoredFileName)));
            if (project.CardBackgroundType == BackgroundFill.Image && ImageStore.Cards.Exists(project.CardBackgroundImagePath))
                files.Add((CardDir + Path.GetFileName(project.CardBackgroundImagePath), ImageStore.Cards.PathOf(project.CardBackgroundImagePath)));

            TransferManifest manifest = NewManifest(ProjectType, ProjectFormat, project.Name);
            manifest.Counts = new TransferCounts
            {
                Tasks = project.Tasks?.Count ?? 0,
                NotePages = writtenPages,
                Files = project.Attachments?.Count ?? 0
            };
            WriteAtomically(zipPath, manifest, new[] { (ProjectEntry, JsonSerializer.Serialize(project)) }, files, progress, cancel);
        }

        // The memory's entry, tabs and items as read from its files. An item picture still at the user's own path
        // (from before pictures were copied in) goes in under a new name, so no path from this PC is in the package.
        public static void ExportCollection(Profile memory, List<MemoryTab> tabs, List<Entity> entities, string zipPath,
            IProgress<double>? progress, CancellationToken cancel)
        {
            List<Entity> items = Clone(entities);
            List<(string Entry, string Source)> files = PicturesOf(items);
            int pictures = files.Count;
            if (memory.BackgroundType == BackgroundFill.Image && ImageStore.Backgrounds.Exists(memory.BackgroundImagePath))
                files.Add((BackgroundDir + Path.GetFileName(memory.BackgroundImagePath), ImageStore.Backgrounds.PathOf(memory.BackgroundImagePath)));

            TransferManifest manifest = NewManifest(CollectionType, CollectionFormat, memory.Name);
            manifest.Counts = new TransferCounts { Items = items.Count, Pictures = pictures };
            WriteAtomically(zipPath, manifest, new[]
            {
                (CollectionEntry, JsonSerializer.Serialize(memory)),
                (TabsEntry, JsonSerializer.Serialize(tabs)),
                (EntitiesEntry, JsonSerializer.Serialize(items))
            }, files, progress, cancel);
        }

        // One item of a collection, with its picture; its tab, Id and place are new where it is imported.
        public static void ExportItem(Entity entity, string zipPath, IProgress<double>? progress, CancellationToken cancel)
        {
            Entity item = Clone(new List<Entity> { entity })[0];
            List<(string Entry, string Source)> files = PicturesOf(new List<Entity> { item });
            TransferManifest manifest = NewManifest(ItemType, ItemFormat, item.Name);
            manifest.Counts = new TransferCounts { Pictures = files.Count };
            WriteAtomically(zipPath, manifest, new[] { (ItemEntry, JsonSerializer.Serialize(item)) }, files, progress, cancel);
        }

        // The items' pictures to pack (the items are copies, changed here): a missing one is dropped from its item,
        // and one still at the user's own path (from before pictures were copied in) gets a new name, so no path from
        // this PC is in the package.
        private static List<(string Entry, string Source)> PicturesOf(List<Entity> items)
        {
            List<(string Entry, string Source)> files = new List<(string, string)>();
            foreach (Entity item in items.Where(i => !string.IsNullOrEmpty(i.ImagePath)))
            {
                string? source = EntityImages.FullPath(item);
                if (source == null || !File.Exists(source))
                {
                    item.ImagePath = null;
                    continue;
                }
                if (EntityImages.IsLegacyPath(item.ImagePath))
                    item.ImagePath = Guid.NewGuid().ToString("N") + Path.GetExtension(source).ToLowerInvariant();
                files.Add((ItemsDir + Path.GetFileName(item.ImagePath), source));
            }
            return files;
        }

        private static TransferManifest NewManifest(string type, int format, string name) => new TransferManifest
        {
            App = ManifestApp,
            Format = format,
            AppVersion = Updates.CurrentVersion.ToString(3),
            Type = type,
            Name = name ?? "",
            Exported = DateTime.UtcNow
        };

        private static void WriteAtomically(string zipPath, TransferManifest manifest, IEnumerable<(string Entry, string Json)> data,
            List<(string Entry, string Source)> files, IProgress<double>? progress, CancellationToken cancel)
        {
            string target = Path.GetFullPath(zipPath);
            string temp = Path.Combine(Path.GetDirectoryName(target) ?? "",
                Path.GetFileNameWithoutExtension(target) + ".tmp-" + Guid.NewGuid().ToString("N").Substring(0, 8) + Extension);
            try
            {
                using (ZipArchive zip = ZipFile.Open(temp, ZipArchiveMode.Create))
                {
                    WriteText(zip, ManifestEntry, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
                    foreach ((string entry, string json) in data)
                        WriteText(zip, entry, json);

                    List<(string Entry, string Source)> unique = files.DistinctBy(f => f.Entry).ToList();
                    long total = Math.Max(1, unique.Sum(f => new FileInfo(f.Source).Length));
                    long done = 0;
                    Stopwatch sinceReport = Stopwatch.StartNew();
                    byte[] buffer = new byte[1 << 20];
                    foreach ((string entryName, string source) in unique)
                    {
                        // Fastest: pictures and most attachments are compressed already, so more effort gains little.
                        ZipArchiveEntry entry = zip.CreateEntry(entryName, CompressionLevel.Fastest);
                        using (FileStream from = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        using (Stream to = entry.Open())
                        {
                            int read;
                            while ((read = from.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                cancel.ThrowIfCancellationRequested();
                                to.Write(buffer, 0, read);
                                done += read;
                                if (sinceReport.ElapsedMilliseconds >= 50)
                                {
                                    progress?.Report((double)done / total);
                                    sinceReport.Restart();
                                }
                            }
                        }
                    }
                }
                File.Move(temp, target, overwrite: true);
            }
            catch
            {
                try { File.Delete(temp); } catch (Exception) { /* best effort: nothing else to do with it */ }
                throw;
            }
        }

        private static void WriteText(ZipArchive zip, string name, string text)
        {
            using (StreamWriter writer = new StreamWriter(zip.CreateEntry(name).Open()))
                writer.Write(text);
        }

        // ---- Open (checks only, writes nothing) ------------------------------

        // Reads and checks the whole package's table of contents and its JSON; the files themselves are read in Stage.
        // Throws TransferException (the message says why, for the user) for anything that isn't a package this
        // version can import.
        public static OpenedPackage Open(string zipPath)
        {
            try
            {
                if (!StartsLikeZip(zipPath))
                    throw new TransferException(Loc.T("Transfer.Error.NotPackage"));
                using (ZipArchive zip = ZipFile.OpenRead(zipPath))
                    return Read(zip, zipPath);
            }
            catch (Exception e) when (e is InvalidDataException || e is IOException || e is UnauthorizedAccessException
                                      || e is NotSupportedException)
            {
                throw new TransferException(Loc.F("Transfer.Error.Unreadable", e.Message));
            }
        }

        // "PK\3\4" (a ZIP with entries) or "PK\5\6" (an empty one): anything else isn't a package at all.
        private static bool StartsLikeZip(string path)
        {
            byte[] head = new byte[4];
            using (FileStream file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                if (file.Read(head, 0, 4) < 4)
                    return false;
            return head[0] == 'P' && head[1] == 'K' && ((head[2] == 3 && head[3] == 4) || (head[2] == 5 && head[3] == 6));
        }

        private static OpenedPackage Read(ZipArchive zip, string zipPath)
        {
            if (zip.Entries.Count > MaxEntries)
                throw new TransferException(Loc.F("Transfer.Error.TooManyFiles", zip.Entries.Count));

            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                if (!IsSafeEntryName(entry.FullName))
                    throw new TransferException(Loc.F("Transfer.Error.Unsafe", entry.FullName));
                if (!names.Add(entry.FullName))
                    throw new TransferException(Loc.F("Transfer.Error.Twice", entry.FullName));
                total += entry.Length;
                if (total > MaxTotalBytes)
                    throw new TransferException(Loc.F("Transfer.Error.TooLarge", AttachmentStore.FormatSize(MaxTotalBytes)));
            }

            ZipArchiveEntry? manifestEntry = zip.GetEntry(ManifestEntry);
            if (manifestEntry == null)
                throw new TransferException(Loc.T("Transfer.Error.NotPackage"));
            TransferManifest? manifest = ReadJson<TransferManifest>(manifestEntry, out _);
            if (manifest == null || manifest.App != ManifestApp || manifest.Format < 1)
                throw new TransferException(Loc.T("Transfer.Error.NotPackage"));
            if (manifest.Format > FormatVersion)
                throw new TransferException(Loc.F("Transfer.Error.Newer", manifest.AppVersion));

            OpenedPackage package = new OpenedPackage(zipPath, manifest);
            if (manifest.Type == ProjectType)
            {
                package.Project = ReadJson<Project>(Required(zip, ProjectEntry), out _) ?? throw Damaged(ProjectEntry);
                Clean(package.Project);
                package.ProjectJson = JsonSerializer.Serialize(package.Project);
            }
            else if (manifest.Type == CollectionType)
            {
                package.Collection = ReadJson<Profile>(Required(zip, CollectionEntry), out package.CollectionJson)
                                     ?? throw Damaged(CollectionEntry);
                package.Tabs = (zip.GetEntry(TabsEntry) is ZipArchiveEntry tabs ? ReadJson<List<MemoryTab>>(tabs, out _) : null)
                               ?? new List<MemoryTab>();
                package.Entities = ReadJson<List<Entity>>(Required(zip, EntitiesEntry), out _) ?? throw Damaged(EntitiesEntry);
                package.Tabs.RemoveAll(t => t == null);
                package.Entities.RemoveAll(e => e == null);
                foreach (Entity item in package.Entities)
                    Clean(item);
                package.EntitiesJson = JsonSerializer.Serialize(package.Entities);
            }
            else if (manifest.Type == ItemType)
            {
                // Held as a list of one, so pictures, counts and staging work as for a collection's items.
                Entity item = ReadJson<Entity>(Required(zip, ItemEntry), out _) ?? throw Damaged(ItemEntry);
                Clean(item);
                package.Entities = new List<Entity> { item };
                package.EntitiesJson = JsonSerializer.Serialize(package.Entities);
            }
            else
                throw new TransferException(Loc.T("Transfer.Error.NotPackage"));

            package.FileBytes = zip.Entries.Where(e => e.FullName.StartsWith(FilesDir)).Sum(e => e.Length);
            package.FileEntries = zip.Entries.Where(e => e.FullName.StartsWith(FilesDir) && !e.FullName.EndsWith("/"))
                .Select(e => e.FullName).ToHashSet(StringComparer.Ordinal);
            return package;
        }

        // Relative, forward slashes, no "..", no drive or colon, no empty part: an entry that would land outside the
        // folder it is unpacked into ("zip slip") makes the whole package unusable.
        internal static bool IsSafeEntryName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Contains('\\') || name.Contains(':') || name.StartsWith("/")
                || Path.IsPathRooted(name) || name.Any(char.IsControl))
                return false;
            string[] parts = name.TrimEnd('/').Split('/');
            return parts.All(p => p.Length > 0 && p != "." && p != "..");
        }

        // ---- Cleaning what the JSON holds ----------------------------------------

        // A package's JSON is someone else's: nulls inside lists (which the views don't expect) are dropped, and
        // formatted text is only kept as XAML when it is plain FlowDocument text (SafeRichText).
        private static void Clean(Project project)
        {
            project.Tasks = (project.Tasks ?? new List<TaskItem>()).Where(t => t != null).ToList();
            project.NotePages = project.NotePages?.Where(p => p != null).ToList();
            project.Attachments = (project.Attachments ?? new List<Attachment>()).Where(a => a != null).ToList();
            project.Description = SafeRichText(project.Description);
            project.Notes = SafeRichText(project.Notes);
            foreach (NotePage page in project.NotePages ?? new List<NotePage>())
                page.Body = SafeRichText(page.Body);
        }

        private static void Clean(Entity item)
        {
            item.Sizes = (item.Sizes ?? new List<SizeQuantity>()).Where(s => s != null).ToList();
            item.Logs = item.Logs?.Where(log => log.Value != null).ToDictionary(log => log.Key,
                log => log.Value.Where(row => row != null)
                    .Select(row => row.Where(cell => cell.Value != null).ToDictionary(cell => cell.Key, cell => cell.Value)).ToList());
        }

        // The text elements the editors write (TextRange.Save, the format toolbar). Loading XAML can make any object
        // it names, so text from a package is only loaded as XAML when it holds nothing else: these elements, the
        // presentation and xml namespaces, attribute values that are no markup extension, and attached properties
        // of text types only. Anything else keeps its words, as plain text.
        private static readonly HashSet<string> TextElements = new HashSet<string>(StringComparer.Ordinal)
        {
            "Section", "Paragraph", "Run", "Span", "Bold", "Italic", "Underline", "LineBreak", "List", "ListItem"
        };
        private static readonly HashSet<string> AttachedOwners = new HashSet<string>(StringComparer.Ordinal)
        {
            "NumberSubstitution", "Typography", "TextElement", "Block", "Inline", "Paragraph", "FlowDocument"
        };
        private const string PresentationNs = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        private const string XmlNs = "http://www.w3.org/XML/1998/namespace";
        private const string XmlnsNs = "http://www.w3.org/2000/xmlns/";

        internal static string SafeRichText(string? stored)
        {
            if (string.IsNullOrEmpty(stored) || !RichTextHelper.IsMarkup(stored))
                return stored!;
            try
            {
                if (IsPlainFlowText(stored))
                    return stored;
            }
            catch (XmlException)
            {
            }
            return TextOf(stored);
        }

        private static bool IsPlainFlowText(string xaml)
        {
            XmlReaderSettings settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using (XmlReader reader = XmlReader.Create(new StringReader(xaml), settings))
            {
                while (reader.Read())
                {
                    if (reader.NodeType == XmlNodeType.ProcessingInstruction || reader.NodeType == XmlNodeType.DocumentType)
                        return false;
                    if (reader.NodeType != XmlNodeType.Element)
                        continue;
                    if (reader.NamespaceURI != PresentationNs || !TextElements.Contains(reader.LocalName))
                        return false;
                    while (reader.MoveToNextAttribute())
                    {
                        bool allowed = reader.NamespaceURI == XmlnsNs ? reader.Value == PresentationNs
                            : reader.NamespaceURI == XmlNs
                            || (reader.NamespaceURI.Length == 0 && !reader.Value.TrimStart().StartsWith("{")
                                && (!reader.LocalName.Contains('.') || AttachedOwners.Contains(reader.LocalName.Split('.')[0])));
                        if (!allowed)
                            return false;
                    }
                }
            }
            return true;
        }

        // The words of markup that isn't loaded: a line per paragraph or line break, no tags, and nothing from a
        // property element (<Section.Resources>…), which is never text on the page.
        private static string TextOf(string markup)
        {
            string shown = Regex.Replace(markup, @"<([\w:]+\.[\w.]+)[^>]*?(/>|>.*?</\1\s*>)", "", RegexOptions.Singleline);
            string lines = Regex.Replace(shown, @"</Paragraph>|<LineBreak\s*/>|</ListItem>", "\n");
            string text = WebUtility.HtmlDecode(Regex.Replace(lines, "<[^>]*>", ""));
            return string.Join("\n", text.Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd())).Trim('\n');
        }

        // Null where it can't be told (a network drive): the copy then simply fails if the disk fills.
        private static long? FreeSpace(string folder)
        {
            try
            {
                return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!).AvailableFreeSpace;
            }
            catch (Exception e) when (e is ArgumentException || e is IOException)
            {
                return null;
            }
        }

        private static ZipArchiveEntry Required(ZipArchive zip, string name) => zip.GetEntry(name) ?? throw Damaged(name);

        private static TransferException Damaged(string entry) =>
            new TransferException(Loc.F("Transfer.Error.Missing", entry));

        // A JSON entry, read whole (it's small) so its text can be deserialized again for a fresh copy on commit.
        private static T? ReadJson<T>(ZipArchiveEntry entry, out string json)
        {
            TransferException tooLarge = new TransferException(Loc.F("Transfer.Error.PartTooLarge", entry.FullName,
                AttachmentStore.FormatSize(MaxJsonBytes)));
            if (entry.Length > MaxJsonBytes)
                throw tooLarge;
            using (Stream stream = entry.Open())
            using (StreamReader reader = new StreamReader(new LimitedStream(stream, MaxJsonBytes, tooLarge)))
                json = reader.ReadToEnd();
            try
            {
                return JsonSerializer.Deserialize<T>(json);
            }
            catch (JsonException e)
            {
                throw new TransferException(Loc.F("Transfer.Error.Damaged", entry.FullName, e.Message));
            }
        }

        // ---- Stage: the files, unpacked into a folder of their own ---------------

        // Unpacks every file the package's JSON refers to into a new folder in the data folder (so moving them into
        // place later is a rename), each under a new name. Nothing in the app's own folders is touched. Dispose the
        // result to remove what's left of the folder. Safe on a worker thread.
        public static StagedImport Stage(OpenedPackage package, IProgress<double>? progress, CancellationToken cancel)
        {
            RemoveLeftoverStaging();
            string folder = AppPaths.Data("Import-" + Guid.NewGuid().ToString("N") + ".copying");
            StagedImport staged = new StagedImport(package, folder);
            try
            {
                Directory.CreateDirectory(folder);
                if (FreeSpace(folder) is long free && free < package.FileBytes + 50L * 1024 * 1024)
                    throw new TransferException(Loc.F("Transfer.Error.NoSpace", AttachmentStore.FormatSize(package.FileBytes)));

                List<string> wanted = package.ReferencedFiles().Where(package.FileEntries.Contains).Distinct().ToList();
                using (ZipArchive zip = ZipFile.OpenRead(package.ZipPath))
                {
                    long total = Math.Max(1, wanted.Sum(n => zip.GetEntry(n)?.Length ?? 0));
                    long done = 0;
                    Stopwatch sinceReport = Stopwatch.StartNew();
                    byte[] buffer = new byte[1 << 20];
                    foreach (string name in wanted)
                    {
                        // Under a new name: no name from the package ever reaches the file system.
                        ZipArchiveEntry entry = zip.GetEntry(name) ?? throw Damaged(name);
                        string path = Path.Combine(folder, Guid.NewGuid().ToString("N") + ExtensionOf(name));

                        // Exactly what the entry says it holds: a ZIP that lies about its sizes can't fill the disk, and
                        // a short (damaged) file isn't taken for a whole one.
                        using (Stream from = entry.Open())
                        using (FileStream to = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        {
                            long written = 0;
                            int read;
                            while ((read = from.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                cancel.ThrowIfCancellationRequested();
                                written += read;
                                if (written > entry.Length)
                                    throw new TransferException(Loc.F("Transfer.Error.SizeWrong", name));
                                to.Write(buffer, 0, read);
                                done += read;
                                if (sinceReport.ElapsedMilliseconds >= 50)
                                {
                                    progress?.Report((double)done / total);
                                    sinceReport.Restart();
                                }
                            }
                            if (written != entry.Length)
                                throw new TransferException(Loc.F("Transfer.Error.SizeWrong", name));
                        }
                        staged.Files[name] = path;
                    }
                }
                return staged;
            }
            catch (Exception e)
            {
                staged.Dispose();
                if (e is InvalidDataException)
                    throw new TransferException(Loc.F("Transfer.Error.Unreadable", e.Message));
                throw;
            }
        }

        // A staging folder left by an import that was cut off (the app closed or crashed mid-way): never data.
        private static void RemoveLeftoverStaging()
        {
            try
            {
                if (!Directory.Exists(AppPaths.DataFolder))
                    return;
                foreach (string folder in Directory.EnumerateDirectories(AppPaths.DataFolder, "Import-*.copying"))
                    try { Directory.Delete(folder, recursive: true); } catch (Exception) { /* in use or locked: next time */ }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
            }
        }

        private static string ExtensionOf(string name)
        {
            string extension = Path.GetExtension(name);
            return SafeExtension.IsMatch(extension) ? extension.ToLowerInvariant() : "";
        }

        // ---- Commit: the new copy -------------------------------------------------

        // Into a project memory, on its first tab and after its last project, as a new project: new Id, new
        // attachment Ids and file names, " (2)" after a name already used there. On the UI thread. Throws
        // TransferException with nothing added if the memory can't be read or written.
        public static Project CommitProject(StagedImport staged, Profile target)
        {
            OpenedPackage package = staged.Package;
            if (!ProjectStore.TryLoad(target, out List<Project> projects, out string problem)
                || !TryFirstTab(target, projects.Select(p => p.TabId), out Guid tabId, out problem))
                throw new TransferException(Loc.F("Transfer.Error.TargetUnreadable", target.Name, problem));

            Project project = JsonSerializer.Deserialize<Project>(package.ProjectJson)!;
            project.Id = Guid.NewGuid();
            project.Name = UniqueName(package.Name, projects.Select(p => p.Name));
            project.TabId = tabId;
            project.Order = projects.Count == 0 ? 0 : projects.Max(p => p.Order) + 1;

            List<(string From, string To)> moves = new List<(string, string)>();
            foreach (Attachment attachment in project.Attachments)
            {
                attachment.Id = Guid.NewGuid();
                attachment.DisplayName ??= "";
                string? source = staged.FileFor(AttachmentsDir, attachment.StoredFileName);
                // One the package doesn't hold stays listed (as missing, as it was), under a name no file has.
                attachment.StoredFileName = Guid.NewGuid().ToString("N") + ExtensionOf(source ?? attachment.StoredFileName ?? "");
                if (source != null)
                    moves.Add((source, AttachmentStore.PathOf(attachment.StoredFileName)));
            }
            ApplyBackground(staged, CardDir, ImageStore.Cards, project.GetCardBackground(), project.SetCardBackground, moves);

            MoveInto(moves);
            projects.Add(project);
            if (!AtomicFile.TryWriteAllText(MemoryFiles.Items(target), JsonSerializer.Serialize(projects)))
            {
                Undo(moves);
                throw new TransferException(Loc.F("Transfer.Error.CantWrite", MemoryFiles.Items(target)));
            }
            return project;
        }

        // As a new collection memory after the last one: new Ids for it, its tabs and items (each item kept on its
        // own tab), new picture file names. profiles is the app's list; saved here (the memory is added last, so a
        // failure leaves profiles.json and the list as they were).
        public static Profile CommitCollection(StagedImport staged, List<Profile> profiles, string color)
        {
            OpenedPackage package = staged.Package;
            Profile memory = JsonSerializer.Deserialize<Profile>(package.CollectionJson)!;
            memory.Id = Guid.NewGuid();
            memory.Type = ProfileType.Collection;
            memory.Name = UniqueName(package.Name,
                profiles.Where(p => p.Type == ProfileType.Collection).Select(p => p.Name));
            memory.Order = profiles.Count == 0 ? 0 : profiles.Max(p => p.Order) + 1;
            memory.IsSample = null; // the guided tour's example on the other PC is the user's own copy here
            memory.TodoCollapsed = null;
            if (memory.Color == null || !HexColor.IsMatch(memory.Color))
                memory.Color = color;

            List<(string From, string To)> moves = new List<(string, string)>();
            ApplyBackground(staged, BackgroundDir, ImageStore.Backgrounds, memory.GetBackground(), memory.SetBackground, moves);

            // Every old tab Id gets a new one; an item on a tab the package doesn't list goes to the first tab (or,
            // with no tabs at all, keeps its group and the view names the tabs as for an old memory).
            List<MemoryTab> tabs = Clone(package.Tabs);
            Dictionary<Guid, Guid> newTab = new Dictionary<Guid, Guid>();
            foreach (MemoryTab tab in tabs)
            {
                if (!newTab.TryGetValue(tab.Id, out Guid id))
                    newTab[tab.Id] = id = Guid.NewGuid();
                tab.Id = id;
            }
            tabs = tabs.GroupBy(t => t.Id).Select(g => g.First()).ToList();

            List<Entity> items = JsonSerializer.Deserialize<List<Entity>>(package.EntitiesJson)!;
            foreach (Entity item in items)
            {
                item.Id = Guid.NewGuid();
                if (!newTab.TryGetValue(item.TabId, out Guid tabId))
                    newTab[item.TabId] = tabId = tabs.Count > 0 ? tabs[0].Id : Guid.NewGuid();
                item.TabId = tabId;
                TakePicture(staged, item, moves);
            }

            bool added = false;
            try
            {
                if (tabs.Count > 0 && !TabStore.TrySave(memory, tabs))
                    throw new TransferException(Loc.F("Transfer.Error.CantWrite", MemoryFiles.Tabs(memory)));
                if (!AtomicFile.TryWriteAllText(MemoryFiles.Items(memory), JsonSerializer.Serialize(items)))
                    throw new TransferException(Loc.F("Transfer.Error.CantWrite", MemoryFiles.Items(memory)));
                MoveInto(moves);
                profiles.Add(memory);
                if (!ProfileManager.TrySaveProfiles(profiles))
                {
                    profiles.Remove(memory);
                    throw new TransferException(Loc.F("Transfer.Error.CantWrite", AppPaths.Data("profiles.json")));
                }
                added = true;
                return memory;
            }
            finally
            {
                if (!added)
                {
                    Undo(moves);
                    MemoryFiles.DeleteFolder(memory);
                }
            }
        }

        // One item into a collection memory, on its first tab and after its last item: new Id and picture file name,
        // " (2)" after a name already used there. With no target (there's no collection yet) it goes into a new
        // collection called newCollectionName, added to profiles and saved last, as CommitCollection does. Returns
        // the collection it went into. Throws TransferException with nothing added if that can't be read or written.
        public static Profile CommitItem(StagedImport staged, Profile? target, List<Profile> profiles, string color,
            string newCollectionName)
        {
            OpenedPackage package = staged.Package;
            bool created = target == null;
            Profile memory = target ?? new Profile
            {
                Id = Guid.NewGuid(),
                Name = UniqueName(newCollectionName, profiles.Where(p => p.Type == ProfileType.Collection).Select(p => p.Name)),
                Type = ProfileType.Collection,
                Color = color,
                Order = profiles.Count == 0 ? 0 : profiles.Max(p => p.Order) + 1
            };

            List<Entity> items = new List<Entity>();
            Guid tabId = Guid.NewGuid(); // a new collection's view names its first tab, as for an old memory
            if (!created && (!EntityStore.TryLoad(memory, out items, out string problem)
                             || !TryFirstTab(memory, items.Select(i => i.TabId), out tabId, out problem)))
                throw new TransferException(Loc.F("Transfer.Error.TargetUnreadable", memory.Name, problem));

            Entity item = JsonSerializer.Deserialize<List<Entity>>(package.EntitiesJson)![0];
            item.Id = Guid.NewGuid();
            item.Name = UniqueName(package.Name, items.Select(i => i.Name));
            item.TabId = tabId;
            item.Order = items.Count == 0 ? 0 : items.Max(i => i.Order) + 1;
            List<(string From, string To)> moves = new List<(string, string)>();
            TakePicture(staged, item, moves);

            bool added = false;
            try
            {
                MoveInto(moves);
                items.Add(item);
                if (!AtomicFile.TryWriteAllText(MemoryFiles.Items(memory), JsonSerializer.Serialize(items)))
                    throw new TransferException(Loc.F("Transfer.Error.CantWrite", MemoryFiles.Items(memory)));
                if (created)
                {
                    profiles.Add(memory);
                    if (!ProfileManager.TrySaveProfiles(profiles))
                    {
                        profiles.Remove(memory);
                        throw new TransferException(Loc.F("Transfer.Error.CantWrite", AppPaths.Data("profiles.json")));
                    }
                }
                added = true;
                return memory;
            }
            finally
            {
                if (!added)
                {
                    Undo(moves);
                    if (created)
                        MemoryFiles.DeleteFolder(memory);
                }
            }
        }

        // The memory's first tab, read without writing anything (a failed import must leave the memory as it was).
        // With no tabs file yet, the tab its first project or item is on, or a new one: the view then makes the tabs
        // from those, as for an old memory (TabStore.TryLoadOrCreate). False (with the reason) if it can't be read.
        private static bool TryFirstTab(Profile memory, IEnumerable<Guid> tabsInUse, out Guid tabId, out string problem)
        {
            problem = null!;
            tabId = Guid.Empty;
            string file = MemoryFiles.Tabs(memory);
            try
            {
                if (File.Exists(file) && JsonSerializer.Deserialize<List<MemoryTab>>(File.ReadAllText(file)) is List<MemoryTab> tabs
                    && tabs.FirstOrDefault(t => t != null) != null)
                    tabId = tabs.Where(t => t != null).OrderBy(t => t.Order).First().Id;
            }
            catch (Exception e) when (e is JsonException || e is IOException || e is UnauthorizedAccessException)
            {
                problem = e.Message;
                return false;
            }
            if (tabId == Guid.Empty)
                tabId = tabsInUse.FirstOrDefault(id => id != Guid.Empty);
            if (tabId == Guid.Empty)
                tabId = Guid.NewGuid();
            return true;
        }

        // An item's picture, if the package held it, under a new file name; otherwise the item has none.
        private static void TakePicture(StagedImport staged, Entity item, List<(string From, string To)> moves)
        {
            string? source = staged.FileFor(ItemsDir, item.ImagePath);
            item.ImagePath = source == null ? null : Guid.NewGuid().ToString("N") + ExtensionOf(source);
            if (source != null)
                moves.Add((source, EntityImages.Store.PathOf(item.ImagePath)));
        }

        // A picture background whose file came along gets a new file name; one without its file (or with a type or
        // color this version can't show) falls back to no background.
        private static void ApplyBackground(StagedImport staged, string dir, ImageStore store, BackgroundSetting background,
            Action<BackgroundSetting> set, List<(string From, string To)> moves)
        {
            BackgroundFill type = Enum.IsDefined(background.Type) ? background.Type : BackgroundFill.None;
            if (type == BackgroundFill.Image && staged.FileFor(dir, background.Image) is string source)
            {
                string name = Guid.NewGuid().ToString("N") + ExtensionOf(source);
                moves.Add((source, store.PathOf(name)));
                set(new BackgroundSetting(BackgroundFill.Image, null, name));
            }
            else if (type == BackgroundFill.Color && background.Color != null && HexColor.IsMatch(background.Color))
                set(new BackgroundSetting(BackgroundFill.Color, background.Color, null));
            else
                set(new BackgroundSetting(BackgroundFill.None, null, null));
        }

        // Staged files into the app's folders (new names, so nothing there is replaced). A file the package refers
        // to twice is copied for the second, so each owner has a file of its own (and deleting one never takes the
        // other's). Throws, after taking out the ones already moved, if one can't be moved.
        private static void MoveInto(List<(string From, string To)> moves)
        {
            List<(string From, string To)> done = new List<(string, string)>();
            Dictionary<string, string> movedTo = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string current = "";
            try
            {
                foreach ((string from, string to) in moves)
                {
                    current = to;
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    if (movedTo.TryGetValue(from, out string? first))
                        File.Copy(first, to);
                    else
                    {
                        File.Move(from, to);
                        movedTo[from] = to;
                    }
                    done.Add((from, to));
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Undo(done);
                throw new TransferException(Loc.F("Transfer.Error.CantWrite", current));
            }
            moves.Clear();
            moves.AddRange(done);
        }

        // Best effort: the files are new copies nothing refers to.
        private static void Undo(List<(string From, string To)> moved)
        {
            foreach ((_, string to) in moved)
                try { File.Delete(to); } catch (Exception) { /* a stray copy is harmless */ }
        }

        // "Name", or "Name (2)", "Name (3)"… if that is taken (ignoring case, as memory names are compared).
        public static string UniqueName(string name, IEnumerable<string> taken)
        {
            HashSet<string> used = new HashSet<string>(taken.Where(t => t != null), StringComparer.OrdinalIgnoreCase);
            if (!used.Contains(name))
                return name;
            for (int i = 2; ; i++)
            {
                string candidate = name + " (" + i + ")";
                if (!used.Contains(candidate))
                    return candidate;
            }
        }

        // Pages with something written on them: the count the preview shows. On the UI thread (formatted pages are
        // read through a FlowDocument).
        internal static int WrittenPages(Project project) =>
            project.PagesOrLegacyNote().Count(p => RichTextHelper.ToPlainText(p.Body).Trim().Length > 0);

        private static List<T> Clone<T>(List<T> list) => JsonSerializer.Deserialize<List<T>>(JsonSerializer.Serialize(list))!;

        // Stops reading past `limit` bytes, so an entry that unpacks to more than it says can't take all the memory.
        private sealed class LimitedStream : Stream
        {
            private readonly Stream inner;
            private readonly TransferException tooLarge;
            private long left;

            public LimitedStream(Stream inner, long limit, TransferException tooLarge)
            {
                this.inner = inner;
                this.tooLarge = tooLarge;
                left = limit;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                int read = inner.Read(buffer, offset, (int)Math.Min(count, left + 1));
                left -= read;
                if (left < 0)
                    throw tooLarge;
                return read;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }

    // The package's manifest.json. Counts are what the exporting app saw; the preview counts from the data itself.
    public class TransferManifest
    {
        public string App { get; set; } = "";
        public int Format { get; set; }
        public string AppVersion { get; set; } = "";
        public string Type { get; set; } = "";       // TransferPackage.ProjectType or CollectionType
        public string Name { get; set; } = "";
        public DateTime Exported { get; set; }        // UTC
        public TransferCounts Counts { get; set; } = new TransferCounts();

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? ExtensionData { get; set; }
    }

    public class TransferCounts
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? Tasks { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? NotePages { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? Files { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? Items { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? Pictures { get; set; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? ExtensionData { get; set; }
    }

    // A package that passed TransferPackage.Open: what the preview shows and what Stage unpacks.
    public sealed class OpenedPackage
    {
        internal OpenedPackage(string zipPath, TransferManifest manifest)
        {
            ZipPath = zipPath;
            Manifest = manifest;
        }

        public string ZipPath { get; }
        public TransferManifest Manifest { get; }
        public bool IsCollection => Manifest.Type == TransferPackage.CollectionType;
        public bool IsItem => Manifest.Type == TransferPackage.ItemType;

        public Project? Project { get; internal set; }
        public Profile? Collection { get; internal set; }
        public List<MemoryTab> Tabs { get; internal set; } = new List<MemoryTab>();
        public List<Entity> Entities { get; internal set; } = new List<Entity>();

        // The JSON as read, deserialized again on commit for a copy of its own.
        internal string ProjectJson = "", CollectionJson = "", EntitiesJson = "";
        internal long FileBytes;
        internal HashSet<string> FileEntries = new HashSet<string>();

        // The name it is imported under (before " (2)" if taken): its own, else the manifest's.
        public string Name => new[] { IsCollection ? Collection?.Name : IsItem ? Entities.FirstOrDefault()?.Name : Project?.Name, Manifest.Name, "TidyMind" }
            .Select(n => (n ?? "").Trim()).First(n => n.Length > 0);
        public int Tasks => Project?.Tasks?.Count(t => t != null) ?? 0;
        public int NotePages => Project == null ? 0 : TransferPackage.WrittenPages(Project);
        public int Files => Project?.Attachments?.Count(a => a != null) ?? 0;
        public int Items => Entities.Count;
        public int Pictures => Entities.Count(e => FileEntries.Contains(TransferPackage.ItemsDir + Path.GetFileName(e.ImagePath ?? "")));

        // Made by a newer TidyMind in the same format: imported, and the fields this version doesn't know are kept.
        public bool FromNewerApp => Version.TryParse(Manifest.AppVersion, out Version? made) && made > Updates.CurrentVersion;

        internal IEnumerable<string> ReferencedFiles()
        {
            if (Project != null)
            {
                foreach (Attachment attachment in (Project.Attachments ?? new List<Attachment>()).Where(a => a?.StoredFileName != null))
                    yield return TransferPackage.AttachmentsDir + Path.GetFileName(attachment.StoredFileName);
                if (Project.CardBackgroundImagePath != null)
                    yield return TransferPackage.CardDir + Path.GetFileName(Project.CardBackgroundImagePath);
            }
            if (Collection?.BackgroundImagePath != null)
                yield return TransferPackage.BackgroundDir + Path.GetFileName(Collection.BackgroundImagePath);
            foreach (Entity item in Entities.Where(e => !string.IsNullOrEmpty(e.ImagePath)))
                yield return TransferPackage.ItemsDir + Path.GetFileName(item.ImagePath);
        }
    }

    // The package's files, unpacked (TransferPackage.Stage). Dispose removes the folder and whatever of it wasn't
    // moved into the app's folders.
    public sealed class StagedImport : IDisposable
    {
        internal StagedImport(OpenedPackage package, string folder)
        {
            Package = package;
            Folder = folder;
        }

        public OpenedPackage Package { get; }
        internal string Folder { get; }
        internal Dictionary<string, string> Files { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

        // The unpacked file for a stored name in one of the package's folders, if the package held it.
        internal string? FileFor(string dir, string? storedName)
        {
            if (string.IsNullOrEmpty(storedName))
                return null;
            return Files.TryGetValue(dir + Path.GetFileName(storedName), out string? path) && File.Exists(path) ? path : null;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Folder))
                    Directory.Delete(Folder, recursive: true);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
            }
        }
    }

    // Why a package can't be exported or imported, in words for the user.
    public class TransferException : Exception
    {
        public TransferException(string message) : base(message) { }
    }
}
