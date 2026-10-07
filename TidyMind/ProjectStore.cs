using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;

namespace TidyMind
{
    // A project memory's projects (MemoryFiles.Items), in card order. Shared by the project grid, the Home dashboard,
    // search and exports.
    public static class ProjectStore
    {
        // Throws if the file exists but can't be read.
        public static List<Project> Load(Profile memory)
        {
            string file = MemoryFiles.Items(memory);
            List<Project> projects = File.Exists(file)
                ? JsonSerializer.Deserialize<List<Project>>(File.ReadAllText(file)) ?? new List<Project>()
                : new List<Project>();

            // Projects from before Ids existed get one now, saved straight away so every view that reads this file
            // afterwards sees the same Ids. Best effort: if it can't be written, they get new ones next time.
            List<Project> withoutId = projects.Where(p => p.Id == Guid.Empty).ToList();
            foreach (Project project in withoutId)
                project.Id = Guid.NewGuid();
            if (withoutId.Count > 0)
                AtomicFile.TryWriteAllText(file, JsonSerializer.Serialize(projects));

            // Stable sort: files from before Order existed (all 0) keep their saved order.
            return projects.OrderBy(p => p.Order).ToList();
        }

        // For a view that edits the list: false (with the reason) if the file exists but can't be read, and the
        // view must then not save over it.
        public static bool TryLoad(Profile memory, out List<Project> projects, out string problem)
        {
            try
            {
                projects = Load(memory);
                problem = null;
                return true;
            }
            catch (Exception e) when (e is JsonException || e is IOException || e is UnauthorizedAccessException)
            {
                projects = null;
                problem = e.Message;
                return false;
            }
        }

        // For views that only show projects (dashboard, search): an unreadable file contributes none.
        public static List<Project> LoadOrEmpty(Profile memory)
        {
            return TryLoad(memory, out List<Project> projects, out _) ? projects : new List<Project>();
        }

        // Written via a temporary file, so a crash or full disk mid-save leaves the previous file intact.
        // If it can't be written (locked, no access), says so and returns false; the file is then unchanged.
        public static bool SaveOrWarn(Profile memory, List<Project> projects)
        {
            string file = MemoryFiles.Items(memory);
            if (AtomicFile.TryWriteAllText(file, JsonSerializer.Serialize(projects)))
                return true;

            MessageBox.Show("Couldn't save the projects of '" + memory.Name + "': this file can't be written:\n" + file
                + "\n\nYour last change wasn't saved.", "Couldn't Save", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }
}
