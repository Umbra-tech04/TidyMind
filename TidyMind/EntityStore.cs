using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;

namespace TidyMind
{
    // A collection memory's items (MemoryFiles.Items), in card order. Shared by the collection grid, search and exports.
    public static class EntityStore
    {
        // Throws if the file exists but can't be read.
        public static List<Entity> Load(Profile memory)
        {
            string file = MemoryFiles.Items(memory);
            List<Entity> entities = File.Exists(file)
                ? JsonSerializer.Deserialize<List<Entity>>(File.ReadAllText(file)) ?? new List<Entity>()
                : new List<Entity>();

            // Items from before Ids existed get one now, saved straight away so every view that reads this file
            // afterwards sees the same Ids. Best effort: if it can't be written, they get new ones next time.
            List<Entity> withoutId = entities.Where(e => e.Id == Guid.Empty).ToList();
            foreach (Entity entity in withoutId)
                entity.Id = Guid.NewGuid();
            if (withoutId.Count > 0)
                AtomicFile.TryWriteAllText(file, JsonSerializer.Serialize(entities));

            // Stable sort: files from before Order existed (all 0) keep their saved order.
            return entities.OrderBy(e => e.Order).ToList();
        }

        // For a view that edits the list: false (with the reason) if the file exists but can't be read, and the
        // view must then not save over it.
        public static bool TryLoad(Profile memory, out List<Entity> entities, out string problem)
        {
            try
            {
                entities = Load(memory);
                problem = null;
                return true;
            }
            catch (Exception e) when (e is JsonException || e is IOException || e is UnauthorizedAccessException)
            {
                entities = null;
                problem = e.Message;
                return false;
            }
        }

        // For views that only show items (search): an unreadable file contributes none.
        public static List<Entity> LoadOrEmpty(Profile memory)
        {
            return TryLoad(memory, out List<Entity> entities, out _) ? entities : new List<Entity>();
        }

        // Written via a temporary file, so a crash or full disk mid-save leaves the previous file intact.
        // If it can't be written (locked, no access), says so and returns false; the file is then unchanged.
        public static bool SaveOrWarn(Profile memory, List<Entity> entities)
        {
            string file = MemoryFiles.Items(memory);
            if (AtomicFile.TryWriteAllText(file, JsonSerializer.Serialize(entities)))
                return true;

            MessageBox.Show("Couldn't save the items of '" + memory.Name + "': this file can't be written:\n" + file
                + "\n\nYour last change wasn't saved.", "Couldn't Save", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }
}
