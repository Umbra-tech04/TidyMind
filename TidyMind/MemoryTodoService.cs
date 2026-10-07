using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace TidyMind
{
    // A project memory's inline to-do list, in {memoryName}_todos.json.
    public static class MemoryTodoService
    {
        public static string FileName(string memoryName) => memoryName + "_todos.json";

        public static string PathOf(string memoryName) => AppPaths.Data(FileName(memoryName));

        // False if the file exists but can't be read; the caller must then not save over it.
        public static bool TryLoad(string memoryName, out List<TodoEntry> todos)
        {
            todos = new List<TodoEntry>();
            string file = PathOf(memoryName);
            if (!File.Exists(file))
                return true;

            try
            {
                todos = JsonSerializer.Deserialize<List<TodoEntry>>(File.ReadAllText(file)) ?? new List<TodoEntry>();
                return true;
            }
            catch (Exception e) when (e is JsonException || e is IOException || e is UnauthorizedAccessException)
            {
                return false;
            }
        }

        // An emptied list removes its file rather than leaving an empty one behind. False if the file couldn't be
        // written or removed; it is then unchanged.
        public static bool TrySave(string memoryName, List<TodoEntry> todos)
        {
            string file = PathOf(memoryName);
            if (todos.Count > 0)
                return AtomicFile.TryWriteAllText(file, JsonSerializer.Serialize(todos));

            try
            {
                File.Delete(file);
                return true;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return false;
            }
        }
    }
}
