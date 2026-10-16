using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace TidyMind
{
    // A project memory's inline to-do list (MemoryFiles.Todos).
    public static class MemoryTodoService
    {
        // False if the file exists but can't be read; the caller must then not save over it.
        public static bool TryLoad(Profile memory, out List<TodoEntry> todos) => TryLoad(memory, out todos, out _);

        // idsSaved: false when to-dos from before Ids existed got one that couldn't be written, so the next read
        // gives them different ones (nothing can be linked to them yet).
        public static bool TryLoad(Profile memory, out List<TodoEntry> todos, out bool idsSaved)
        {
            todos = new List<TodoEntry>();
            idsSaved = true;
            string file = MemoryFiles.Todos(memory);
            if (!File.Exists(file))
                return true;

            try
            {
                todos = JsonSerializer.Deserialize<List<TodoEntry>>(File.ReadAllText(file)) ?? new List<TodoEntry>();
            }
            catch (Exception e) when (e is JsonException || e is IOException || e is UnauthorizedAccessException)
            {
                return false;
            }

            // To-dos from before Ids existed get one now, saved straight away so every view (and a day plan block
            // linked to one) sees the same Id. Best effort: if it can't be written, they get new ones next time.
            List<TodoEntry> withoutId = todos.Where(t => t.Id == Guid.Empty).ToList();
            foreach (TodoEntry todo in withoutId)
                todo.Id = Guid.NewGuid();
            if (withoutId.Count > 0)
                idsSaved = AtomicFile.TryWriteAllText(file, JsonSerializer.Serialize(todos));
            return true;
        }

        // Ticks or unticks one to-do by Id. False if the list couldn't be read or written, or it isn't there.
        public static bool SetDone(Profile memory, Guid id, bool done)
        {
            if (!TryLoad(memory, out List<TodoEntry> todos))
                return false;
            TodoEntry todo = todos.FirstOrDefault(t => t.Id == id);
            if (todo == null)
                return false;
            todo.IsDone = done;
            return TrySave(memory, todos);
        }

        // An emptied list removes its file rather than leaving an empty one behind. False if the file couldn't be
        // written or removed; it is then unchanged.
        public static bool TrySave(Profile memory, List<TodoEntry> todos)
        {
            string file = MemoryFiles.Todos(memory);
            if (todos.Count > 0)
                return AtomicFile.TryWriteAllText(file, JsonSerializer.Serialize(todos));

            try
            {
                if (File.Exists(file))
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
