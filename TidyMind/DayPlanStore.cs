using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace TidyMind
{
    // dayplan.json (DayPlanFile). Every change reads the file fresh, changes it and writes it whole (AtomicFile), so
    // nothing works from a stale copy. A file that can't be read is never written over: changes fail instead and the
    // views say why.
    public static class DayPlanStore
    {
        public static string FilePath => AppPaths.Data("dayplan.json");

        private static bool unreadableLogged;

        // False if the file exists but can't be read (it is then left as it is); `plan` is empty then.
        public static bool TryLoad(out DayPlanFile plan)
        {
            plan = new DayPlanFile();
            if (!File.Exists(FilePath))
                return true;

            try
            {
                plan = JsonSerializer.Deserialize<DayPlanFile>(File.ReadAllText(FilePath)) ?? new DayPlanFile();
                plan.Blocks ??= new List<DayPlanBlock>();
                plan.Blocks.RemoveAll(b => b == null);
                return true;
            }
            catch (Exception e) when (e is JsonException || e is IOException || e is UnauthorizedAccessException)
            {
                // Once per run: every view that shows the plan asks again.
                if (!unreadableLogged)
                    UnhandledErrors.LogQuietly("reading " + FilePath, e);
                unreadableLogged = true;
                plan = new DayPlanFile();
                return false;
            }
        }

        // That day's blocks by start (then end, then title); empty if the file can't be read.
        public static List<DayPlanBlock> BlocksOn(DayPlanFile plan, DateTime date) =>
            plan.Blocks.Where(b => b.Date.Date == date.Date)
                .OrderBy(b => b.StartMinute).ThenBy(b => b.EndMinute).ThenBy(b => b.Title, Loc.Comparer)
                .ToList();

        // Adds the block, or replaces the stored one with the same Id (keeping fields this version doesn't know).
        public static bool Save(DayPlanBlock block) => Update(plan =>
        {
            int index = plan.Blocks.FindIndex(b => b.Id == block.Id);
            if (index < 0)
            {
                plan.Blocks.Add(block);
                return true;
            }
            block.ExtensionData ??= plan.Blocks[index].ExtensionData;
            plan.Blocks[index] = block;
            return true;
        });

        // Changes the stored block (read fresh, so nothing else about it goes back to an older copy). `changed` is
        // the block as written; false and null if it couldn't be read or written, or isn't there any more.
        public static bool Change(Guid id, Action<DayPlanBlock> change, out DayPlanBlock? changed)
        {
            DayPlanBlock? found = null;
            bool saved = Update(plan =>
            {
                found = plan.Blocks.FirstOrDefault(b => b.Id == id);
                if (found == null)
                    return false;
                change(found);
                return true;
            });
            changed = saved ? found : null;
            return saved;
        }

        // True also when it was already gone.
        public static bool Delete(Guid id) => Update(plan => plan.Blocks.RemoveAll(b => b.Id == id) > 0, nothingToDo: true);

        // A to-do was ticked or unticked: every block that is time for it follows. True if nothing needed changing.
        public static bool SetDoneForTodo(TodoLink link, bool done) => Update(plan =>
        {
            List<DayPlanBlock> linked = plan.Blocks.Where(b => link.Matches(b.Todo) && b.Done != done).ToList();
            linked.ForEach(b => b.Done = done);
            return linked.Count > 0;
        }, nothingToDo: true);

        // change returns false when it changed nothing: then nothing is written, and the result is `nothingToDo`.
        private static bool Update(Func<DayPlanFile, bool> change, bool nothingToDo = false)
        {
            if (!TryLoad(out DayPlanFile plan))
                return false;
            if (!change(plan))
                return nothingToDo;
            return AtomicFile.TryWriteAllText(FilePath, JsonSerializer.Serialize(plan));
        }
    }
}
