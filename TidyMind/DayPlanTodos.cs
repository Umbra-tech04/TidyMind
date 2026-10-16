using System;
using System.Collections.Generic;
using System.Linq;

namespace TidyMind
{
    // A to-do a day plan block can be time for.
    public class PlanTodo
    {
        public TodoLink Link { get; init; } = new TodoLink();
        public string Text { get; init; } = "";
        public bool Done { get; init; }
        public string Where { get; init; } = "";  // Home (in the current language), or the project memory's name
        public DateTime? Due { get; init; }        // Home's to-dos only
    }

    // The two to-do lists a block can be linked to: Home's to-dos (QuickTodoService) and each project memory's to-do
    // list (MemoryTodoService). A project's own tasks can't be: they are that project's checklist, kept and saved
    // with the project in its own window.
    public static class DayPlanTodos
    {
        // Every linkable to-do, done or not. `unusable` names the lists that couldn't be read, or whose to-dos got Ids
        // that couldn't be saved (nothing could stay linked to those); they add none.
        public static List<PlanTodo> All(IEnumerable<Profile> memories, out List<string> unusable)
        {
            unusable = new List<string>();
            string homeName = Loc.T("Nav.Home");
            if (!QuickTodoService.TryRead(out List<QuickTodo> homeTodos))
                unusable.Add(homeName);
            List<PlanTodo> todos = homeTodos
                .Select(t => new PlanTodo
                {
                    Link = TodoLink.ForHome(t.Id),
                    Text = t.Text ?? "",
                    Done = t.IsDone,
                    Where = homeName,
                    Due = t.DueDate.Date
                })
                .ToList();

            foreach (Profile memory in memories.Where(m => m.Type == ProfileType.Project))
            {
                if (!MemoryTodoService.TryLoad(memory, out List<TodoEntry> entries, out bool idsSaved) || !idsSaved)
                {
                    unusable.Add(memory.Name);
                    continue;
                }
                todos.AddRange(entries.Select(t => new PlanTodo
                {
                    Link = TodoLink.ForMemory(memory, t.Id),
                    Text = t.Text ?? "",
                    Done = t.IsDone,
                    Where = memory.Name
                }));
            }
            return todos;
        }

        // Open to-dos with no block on that day: Home's due that day (today also the ones rolled over from earlier
        // days, as Home lists them), then every project memory's.
        public static List<PlanTodo> Unscheduled(List<PlanTodo> all, List<DayPlanBlock> dayBlocks, DateTime date)
        {
            bool today = date.Date == DateTime.Today;
            return all
                .Where(t => !t.Done && !dayBlocks.Any(b => t.Link.Matches(b.Todo)))
                .Where(t => t.Due == null || t.Due == date.Date || (today && t.Due < date.Date))
                .ToList();
        }

        public static PlanTodo? Find(List<PlanTodo> all, TodoLink? link) =>
            link == null ? null : all.FirstOrDefault(t => t.Link.Matches(link));

        // Ticks or unticks the to-do and, with it, every block that is time for it. False if the to-do couldn't be
        // changed (its list can't be read or written, or it was deleted); nothing changed then.
        public static bool SetDone(TodoLink link, IEnumerable<Profile> memories, bool done)
        {
            if (link.Source == TodoSource.Home)
                return QuickTodoService.SetDone(link.Id, done); // follows up with SyncBlocks itself

            Profile? memory = memories.FirstOrDefault(m => m.Id == link.MemoryId);
            if (memory == null || !MemoryTodoService.SetDone(memory, link.Id, done))
                return false;
            SyncBlocks(link, done);
            return true;
        }

        // What to say when a to-do couldn't be ticked or unticked.
        public static string CantChange(PlanTodo todo) =>
            Loc.F("Plan.Todo.CantChange", todo.Text);

        // A to-do was ticked or unticked in its list: its blocks follow. If dayplan.json can't be changed, the day
        // plan still shows the to-do's state (it reads it from the to-do), so this is only logged.
        public static void SyncBlocks(TodoLink link, bool done)
        {
            if (!DayPlanStore.SetDoneForTodo(link, done))
                UnhandledErrors.LogQuietly("day plan", "The blocks linked to a to-do couldn't be marked "
                    + (done ? "done" : "not done") + ": " + DayPlanStore.FilePath + " can't be read or written.");
        }
    }
}
