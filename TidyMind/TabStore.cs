using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace TidyMind
{
    public static class TabStore
    {
        public const string DefaultTabName = "Unnamed";

        // Loads the memory's tabs (sorted). With no tabs yet (no file, or an empty one), they are made from the tabs
        // the memory's items already point at, in the items' order, so a lost tabs file costs only the tab names and
        // never which items belong together; with no such items, just the default tab.
        // False (with the reason) if the file exists but can't be read: nothing is created or saved then.
        public static bool TryLoadOrCreate(Profile memory, IEnumerable<Guid> itemTabIds,
            out List<MemoryTab> tabs, out string problem)
        {
            string file = MemoryFiles.Tabs(memory);
            tabs = new List<MemoryTab>();
            problem = null;

            if (File.Exists(file))
            {
                try
                {
                    tabs = JsonSerializer.Deserialize<List<MemoryTab>>(File.ReadAllText(file)) ?? new List<MemoryTab>();
                }
                catch (Exception e) when (e is JsonException || e is IOException || e is UnauthorizedAccessException)
                {
                    tabs = null;
                    problem = e.Message;
                    return false;
                }
            }

            if (tabs.Count == 0)
            {
                List<Guid> ids = itemTabIds.Where(id => id != Guid.Empty).Distinct().ToList();
                if (ids.Count == 0)
                    ids.Add(Guid.NewGuid());

                tabs = ids.Select((id, i) => new MemoryTab
                {
                    Id = id,
                    Name = i == 0 ? DefaultTabName : DefaultTabName + " " + (i + 1),
                    Order = i
                }).ToList();
                TrySave(memory, tabs);
            }

            tabs = tabs.OrderBy(t => t.Order).ToList();
            return true;
        }

        // Written via a temporary file, like the memory's other files. False if it couldn't be written; the file is
        // then unchanged.
        public static bool TrySave(Profile memory, List<MemoryTab> tabs)
        {
            return AtomicFile.TryWriteAllText(MemoryFiles.Tabs(memory), JsonSerializer.Serialize(tabs));
        }
    }
}
