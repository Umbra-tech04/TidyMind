using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace TidyMind
{
    // App-wide daily to-dos in quicktodos.json (not tied to any memory, like reminders.json).
    public static class QuickTodoService
    {
        private const string TodosFile = "quicktodos.json";
        private const int KeepCompletedDays = 7;

        public static List<QuickTodo> LoadTodos()
        {
            return TryRead(out List<QuickTodo> todos) ? todos : new List<QuickTodo>();
        }

        // Due on that day, done ones included (callers decide whether to show them), oldest first.
        public static List<QuickTodo> GetTodosForDate(DateTime date) => DueOn(LoadTodos(), date);

        // Unfinished todos from before today, oldest due date first.
        public static List<QuickTodo> GetOverdueIncomplete(DateTime today) => Overdue(LoadTodos(), today);

        // What the to-do list shows: today's and tomorrow's todos, plus unfinished ones rolled over from earlier
        // days (and those ticked off today, so they stay with today's finished ones). Unordered; the list sorts.
        public static List<QuickTodo> GetCurrentList(DateTime today)
        {
            DateTime tomorrow = today.Date.AddDays(1);
            return LoadTodos()
                .Where(t => t.DueDate.Date == today.Date || t.DueDate.Date == tomorrow
                            || (t.DueDate.Date < today.Date && (!t.IsDone || t.CompletedDate?.Date == today.Date)))
                .ToList();
        }

        // Null if the file couldn't be read (and so wasn't touched).
        public static QuickTodo Add(string text, DateTime dueDate)
        {
            QuickTodo todo = new QuickTodo
            {
                Id = Guid.NewGuid(),
                Text = text.Trim(),
                DueDate = dueDate.Date,
                CreatedDate = DateTime.Now
            };

            return Update(todos =>
            {
                todos.Add(todo);
                return true;
            }) ? todo : null;
        }

        public static bool SetDone(Guid id, bool done)
        {
            return Update(todos =>
            {
                QuickTodo todo = todos.FirstOrDefault(t => t.Id == id);
                if (todo == null) return false;

                todo.IsDone = done;
                todo.CompletedDate = done ? DateTime.Now : (DateTime?)null;
                return true;
            });
        }

        public static bool Rename(Guid id, string text)
        {
            return Update(todos =>
            {
                QuickTodo todo = todos.FirstOrDefault(t => t.Id == id);
                if (todo == null) return false;

                todo.Text = text.Trim();
                return true;
            });
        }

        public static bool Delete(Guid id) => Update(todos => todos.RemoveAll(t => t.Id == id) > 0);

        // Todos finished before CompletedDate existed have none; their due date stands in for it.
        public static void RemoveOldCompleted(DateTime today)
        {
            DateTime cutoff = today.Date.AddDays(-KeepCompletedDays);
            Update(todos => todos.RemoveAll(t => t.IsDone && (t.CompletedDate ?? t.DueDate) < cutoff) > 0);
        }

        private static List<QuickTodo> DueOn(List<QuickTodo> todos, DateTime date)
        {
            return todos.Where(t => t.DueDate.Date == date.Date).OrderBy(t => t.CreatedDate).ToList();
        }

        private static List<QuickTodo> Overdue(List<QuickTodo> todos, DateTime today)
        {
            return todos
                .Where(t => !t.IsDone && t.DueDate.Date < today.Date)
                .OrderBy(t => t.DueDate)
                .ThenBy(t => t.CreatedDate)
                .ToList();
        }

        // A file that can't be read is left alone instead of being overwritten with an empty list;
        // false also when the write itself fails (callers show "Couldn't save").
        private static bool Update(Func<List<QuickTodo>, bool> change)
        {
            if (!TryRead(out List<QuickTodo> todos) || !change(todos))
                return false;

            return AtomicFile.TryWriteAllText(TodosFile, JsonSerializer.Serialize(todos));
        }

        private static bool TryRead(out List<QuickTodo> todos)
        {
            todos = new List<QuickTodo>();
            if (!File.Exists(TodosFile))
                return true;

            try
            {
                todos = JsonSerializer.Deserialize<List<QuickTodo>>(File.ReadAllText(TodosFile)) ?? new List<QuickTodo>();
                return true;
            }
            catch (Exception e) when (e is JsonException || e is IOException || e is UnauthorizedAccessException)
            {
                return false;
            }
        }
    }
}
