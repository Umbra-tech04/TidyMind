using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TidyMind
{
    // A memory's own notebook (MemoryFiles.Notebook): pages like a project's notebook (NotePage), for what applies to
    // the whole memory. Shown and edited in NotebookWindow.
    public class MemoryNotebook
    {
        public List<NotePage> Pages { get; set; } = new List<NotePage>();

        // Fields this version doesn't know (e.g. from a newer TidyMind): kept so saving doesn't drop them.
        [JsonExtensionData]
        public Dictionary<string, JsonElement>? ExtensionData { get; set; }

        // What the notebook window shows: the pages, or one empty page. Never empty.
        public List<NotePage> PagesToShow()
        {
            List<NotePage> pages = Pages.Where(p => p != null).ToList();
            if (pages.Count == 0)
                pages.Add(new NotePage());
            return pages;
        }

        // Pages with something written on them (on the UI thread: formatted pages are read through a FlowDocument).
        public int WrittenPages() => Pages.Count(p => p != null && RichTextHelper.HasText(p.Body));

        // Nothing written and nothing from another version: no file is needed for it. The editor stores a blank page as
        // "", so this needs no FlowDocument and works on any thread (a transfer package is written on a worker thread).
        internal bool IsBlank() => ExtensionData == null && Pages.All(p => p == null || (string.IsNullOrEmpty(p.Body) && p.ExtensionData == null));
    }

    public static class MemoryNotebookStore
    {
        // A memory without the file has an empty notebook. False if the file is there but can't be read (problem says
        // why); the caller must then not save over it.
        public static bool TryLoad(Profile memory, out MemoryNotebook notebook, out string problem)
        {
            notebook = new MemoryNotebook();
            problem = "";
            string file = MemoryFiles.Notebook(memory);
            if (!File.Exists(file))
                return true;
            try
            {
                notebook = JsonSerializer.Deserialize<MemoryNotebook>(File.ReadAllText(file)) ?? new MemoryNotebook();
                notebook.Pages = (notebook.Pages ?? new List<NotePage>()).Where(p => p != null).ToList();
                return true;
            }
            catch (Exception e) when (e is JsonException || e is IOException || e is UnauthorizedAccessException)
            {
                problem = e.Message;
                return false;
            }
        }

        // Written whole through AtomicFile. A notebook with nothing in it removes its file rather than leaving an empty
        // one behind. False if the file couldn't be written or removed; it is then unchanged.
        public static bool TrySave(Profile memory, MemoryNotebook notebook)
        {
            string file = MemoryFiles.Notebook(memory);
            if (!notebook.IsBlank())
                return AtomicFile.TryWriteAllText(file, JsonSerializer.Serialize(notebook));
            try
            {
                if (File.Exists(file))
                    File.Delete(file);
                return true;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return false;
            }
        }

        // For the button on the memory's page: 0 if there's nothing (or the file can't be read).
        public static int WrittenPages(Profile memory) =>
            TryLoad(memory, out MemoryNotebook notebook, out _) ? notebook.WrittenPages() : 0;
    }
}
