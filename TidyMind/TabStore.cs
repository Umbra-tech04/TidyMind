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

        // Separate files per type: a Project and a Collection may share a memory name.
        public static string FileName(string memoryName, ProfileType type)
        {
            return type == ProfileType.Collection
                ? memoryName + "_entities_tabs.json"
                : memoryName + "_tabs.json";
        }

        // Loads the memory's tabs (sorted), creating the default tab first if none exist.
        public static List<MemoryTab> LoadOrCreate(string memoryName, ProfileType type)
        {
            string file = FileName(memoryName, type);
            List<MemoryTab> tabs = File.Exists(file)
                ? JsonSerializer.Deserialize<List<MemoryTab>>(File.ReadAllText(file)) ?? new List<MemoryTab>()
                : new List<MemoryTab>();

            if (tabs.Count == 0)
            {
                tabs.Add(new MemoryTab { Id = Guid.NewGuid(), Name = DefaultTabName, Order = 0 });
                Save(memoryName, type, tabs);
            }

            return tabs.OrderBy(t => t.Order).ToList();
        }

        public static void Save(string memoryName, ProfileType type, List<MemoryTab> tabs)
        {
            File.WriteAllText(FileName(memoryName, type), JsonSerializer.Serialize(tabs));
        }
    }
}
