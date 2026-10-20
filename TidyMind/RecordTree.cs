using System;
using System.Collections.Generic;
using System.Linq;

namespace TidyMind
{
    // Records that hold records (EntityRecord.Children: a vehicle's parts, system > unit > part > …): finding one,
    // walking all of them with their path, and moving one within the outline. MaxDepth keeps the stored JSON well
    // inside what can be read back.
    public static class RecordTree
    {
        public const int MaxDepth = 8;

        public static List<EntityRecord> ChildrenOf(EntityRecord record) => record.Children ??= new List<EntityRecord>();

        // Every record with the ones above it (outermost first), depth first, in their order.
        public static IEnumerable<(EntityRecord Record, IReadOnlyList<EntityRecord> Above)> Walk(IEnumerable<EntityRecord> list) =>
            Walk(list, new List<EntityRecord>());

        private static IEnumerable<(EntityRecord, IReadOnlyList<EntityRecord>)> Walk(IEnumerable<EntityRecord> list, List<EntityRecord> above)
        {
            foreach (EntityRecord record in list.Where(r => r != null))
            {
                yield return (record, above.ToList());
                if (record.Children == null)
                    continue;
                above.Add(record);
                foreach (var inner in Walk(record.Children, above))
                    yield return inner;
                above.RemoveAt(above.Count - 1);
            }
        }

        public static EntityRecord? Find(IEnumerable<EntityRecord> list, Guid id) =>
            Walk(list).Select(x => x.Record).FirstOrDefault(r => r.Id == id);

        // The list that holds the record (the top list, or a record's Children), or null if it isn't there.
        public static List<EntityRecord>? Holder(List<EntityRecord> list, Guid id) =>
            list.Any(r => r?.Id == id) ? list
            : Walk(list).Select(x => x.Record).FirstOrDefault(r => r.Children?.Any(c => c?.Id == id) == true)?.Children;

        public static int Depth(List<EntityRecord> list, Guid id) =>
            Walk(list).Where(x => x.Record.Id == id).Select(x => x.Above.Count).FirstOrDefault();

        // How many records are under it, at any depth.
        public static int CountUnder(EntityRecord record) => Walk(record.Children ?? new List<EntityRecord>()).Count();

        // Up or down among its siblings (-1 / +1); false at either end.
        public static bool Move(List<EntityRecord> list, Guid id, int step)
        {
            List<EntityRecord>? holder = Holder(list, id);
            int at = holder?.FindIndex(r => r?.Id == id) ?? -1;
            if (holder == null || at + step < 0 || at + step >= holder.Count)
                return false;
            (holder[at], holder[at + step]) = (holder[at + step], holder[at]);
            return true;
        }

        // Whether it has a sibling above to go into without going deeper than MaxDepth.
        public static bool CanIndent(List<EntityRecord> list, Guid id)
        {
            List<EntityRecord>? holder = Holder(list, id);
            int at = holder?.FindIndex(r => r?.Id == id) ?? -1;
            return holder != null && at >= 1 && Depth(list, id) + 1 + Height(holder[at]) < MaxDepth;
        }

        // Into the sibling above it, as its last; false if it can't (CanIndent).
        public static bool Indent(List<EntityRecord> list, Guid id)
        {
            if (!CanIndent(list, id))
                return false;
            List<EntityRecord> holder = Holder(list, id)!;
            int at = holder.FindIndex(r => r?.Id == id);
            EntityRecord record = holder[at];
            holder.RemoveAt(at);
            ChildrenOf(holder[at - 1]).Add(record);
            return true;
        }

        // Out of the record holding it, right after that one; false at the top.
        public static bool Outdent(List<EntityRecord> list, Guid id)
        {
            List<EntityRecord>? holder = Holder(list, id);
            if (holder == null || holder == list)
                return false;
            EntityRecord parent = Walk(list).Select(x => x.Record).First(r => r.Children == holder);
            List<EntityRecord> outer = Holder(list, parent.Id)!;
            EntityRecord record = holder.First(r => r?.Id == id);
            holder.Remove(record);
            if (holder.Count == 0)
                parent.Children = null;
            outer.Insert(outer.IndexOf(parent) + 1, record);
            return true;
        }

        // How many levels the record and the ones under it take below itself (0: none under it).
        private static int Height(EntityRecord record) =>
            record.Children == null || record.Children.Count == 0 ? 0 : 1 + record.Children.Where(c => c != null).Max(Height);
    }
}
