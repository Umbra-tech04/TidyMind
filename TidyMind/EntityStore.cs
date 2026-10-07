using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;

namespace TidyMind
{
    // A collection memory's items (<memory>_entities.json), in card order. Shared by the collection grid and exports,
    // so an index into this list means the same item in both.
    public static class EntityStore
    {
        public static string FileName(string memoryName) => memoryName + "_entities.json";

        // Throws if the file exists but can't be read.
        public static List<Entity> Load(string memoryName)
        {
            string fileName = AppPaths.Data(FileName(memoryName));
            List<Entity> entities = File.Exists(fileName)
                ? JsonSerializer.Deserialize<List<Entity>>(File.ReadAllText(fileName)) ?? new List<Entity>()
                : new List<Entity>();

            // Stable sort: files from before Order existed (all 0) keep their saved order.
            return entities.OrderBy(e => e.Order).ToList();
        }

        // For a view that edits the list: false (with the reason) if the file exists but can't be read, and the
        // view must then not save over it.
        public static bool TryLoad(string memoryName, out List<Entity> entities, out string problem)
        {
            try
            {
                entities = Load(memoryName);
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

        // Written via a temporary file, so a crash or full disk mid-save leaves the previous file intact.
        // If it can't be written (locked, no access), says so and returns false; the file is then unchanged.
        public static bool SaveOrWarn(string memoryName, List<Entity> entities)
        {
            string fileName = FileName(memoryName);
            if (AtomicFile.TryWriteAllText(AppPaths.Data(fileName), JsonSerializer.Serialize(entities)))
                return true;

            MessageBox.Show("Couldn't save: " + fileName + " can't be written. Your last change wasn't saved.",
                "Couldn't Save", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }
}
