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

        public static string Folder(Profile memory) => Folder(memory.Id);

        private static string Folder(Guid memoryId) => AppPaths.Data(Path.Combine(FolderName, memoryId.ToString("N")));

        // The memory's projects (a project memory) or items (a collection).
        public static string Items(Profile memory) =>
            Path.Combine(Folder(memory), memory.Type == ProfileType.Collection ? "entities.json" : "projects.json");

        public static string Tabs(Profile memory) => Path.Combine(Folder(memory), "tabs.json");

        // A project memory's inline to-do list.
        public static string Todos(Profile memory) => Path.Combine(Folder(memory), "todos.json");

        // The memory's own notebook (MemoryNotebookStore), for anything about the whole memory. Only there once written in.
        public static string Notebook(Profile memory) => Path.Combine(Folder(memory), "notebook.json");

        // A custom collection's item layout (CustomLayoutStore); a collection without it isn't custom. By Id too: a
        // custom item's type names its collection (CustomTypes).
        public static string Layout(Profile memory) => Layout(memory.Id);

        public static string Layout(Guid memoryId) => Path.Combine(Folder(memoryId), "layout.json");

        // After the memory has been removed from profiles.json. Best effort: a folder that can't be deleted now is
        // left behind, unused (nothing refers to its Id any more). A deleted memory whose delete can still be undone keeps
        // its folder (UndoService).
        public static void DeleteFolder(Profile memory) => DeleteFolder(memory.Id);

        public static void DeleteFolder(Guid memoryId)
        {
            if (UndoService.IsHeld(HeldFile.Folder(memoryId)))
                return;
            try
            {
                string folder = Folder(memoryId);
                if (Directory.Exists(folder))
                    Directory.Delete(folder, recursive: true);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
            }
        }
    }
}
