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

        // False if the file exists but can't be read; the caller must then not save over it.
        public static bool TryLoad(string memoryName, out List<TodoEntry> todos)
        {
            todos = new List<TodoEntry>();
            string file = FileName(memoryName);
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

        // An emptied list removes its file rather than leaving an empty one behind.
        public static void Save(string memoryName, List<TodoEntry> todos)
        {
            string file = FileName(memoryName);
            if (todos.Count == 0)
                File.Delete(file);
            else
                File.WriteAllText(file, JsonSerializer.Serialize(todos));
        }
    }
}
