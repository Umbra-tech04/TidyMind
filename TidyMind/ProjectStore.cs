using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;

namespace TidyMind
{
    // A project memory's projects (<memory>.json), in card order. Shared by the project grid and the Home dashboard,
    // so an index into this list means the same project in both.
    public static class ProjectStore
    {
        public static List<Project> Load(string memoryName)
        {
            string fileName = memoryName + ".json";
            List<Project> projects = File.Exists(fileName)
                ? JsonSerializer.Deserialize<List<Project>>(File.ReadAllText(fileName)) ?? new List<Project>()
                : new List<Project>();

            // Stable sort: files from before Order existed (all 0) keep their saved order.
            return projects.OrderBy(p => p.Order).ToList();
        }

        // Written via a temporary file, so a crash or full disk mid-save leaves the previous file intact.
        // If it can't be written (locked, no access), says so and returns false; the file is then unchanged.
        public static bool SaveOrWarn(string memoryName, List<Project> projects)
        {
            string fileName = memoryName + ".json";
            if (AtomicFile.TryWriteAllText(fileName, JsonSerializer.Serialize(projects)))
                return true;

            MessageBox.Show("Couldn't save: " + fileName + " can't be written. Your last change wasn't saved.",
                "Couldn't Save", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }
}
