using System;
using System.IO;

namespace TidyMind
{
    // Where a memory's own files live: Memories\{Profile.Id}\ in the data folder. Named by Id, so the memory's display
    // name never touches the file system: any name works, renaming moves nothing, and no two memories (or a memory
    // and an app-wide file such as profiles.json) can end up sharing a file.
    public static class MemoryFiles
    {
        private const string FolderName = "Memories";

        public static string Folder(Profile memory) =>
            AppPaths.Data(Path.Combine(FolderName, memory.Id.ToString("N")));

        // The memory's projects (a project memory) or items (a collection).
        public static string Items(Profile memory) =>
            Path.Combine(Folder(memory), memory.Type == ProfileType.Collection ? "entities.json" : "projects.json");

        public static string Tabs(Profile memory) => Path.Combine(Folder(memory), "tabs.json");

        // A project memory's inline to-do list.
        public static string Todos(Profile memory) => Path.Combine(Folder(memory), "todos.json");

        // After the memory has been removed from profiles.json. Best effort: a folder that can't be deleted now is
        // left behind, unused (nothing refers to its Id any more).
        public static void DeleteFolder(Profile memory)
        {
            try
            {
                string folder = Folder(memory);
                if (Directory.Exists(folder))
                    Directory.Delete(folder, recursive: true);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
            }
        }
    }
}
