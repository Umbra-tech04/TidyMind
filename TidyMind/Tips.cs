using System.Collections.Generic;
using System.Linq;

namespace TidyMind
{
    // One entry of Tips & shortcuts (TipsWindow): the keys or gesture ("Tips.<Id>.Keys") and one sentence on what it
    // does ("Tips.<Id>.Text"), under its group ("Tips.Group.<Group>").
    public sealed class Tip
    {
        public Tip(string group, string id)
        {
            Group = group;
            Id = id;
        }

        public string Group { get; }
        public string Id { get; }

        public string GroupTitle => Loc.T("Tips.Group." + Group);
        public string Keys => Loc.T("Tips." + Id + ".Keys");
        public string Text => Loc.T("Tips." + Id + ".Text");
    }

    // Every gesture and shortcut the app has that a button doesn't already show, by where it works. Add an entry only
    // for something the code does; its texts go into all three Strings files.
    public static class Tips
    {
        public static readonly IReadOnlyList<Tip> All = new (string Group, string[] Ids)[]
        {
            ("Everywhere", new[] { "Search", "QuickNote", "Reminders", "Calendar", "Undo", "Redo", "RightClick", "Escape" }),
            ("Sidebar", new[] { "MemoryMenu", "MemoryDrag", "MemorySelect" }),
            ("Pages", new[] { "CardMenu", "CardSelect", "Marquee", "CardDrag", "Background", "TabRename", "TabMenu", "TabDrag", "TodoFold" }),
            ("Projects", new[] { "TaskSelect", "TaskMenu", "FileMenu", "FileDrop", "ProjectEscape" }),
            ("Notebook", new[] { "PageTurn", "PageSelect", "Format", "Paste" }),
            ("Todos", new[] { "TodoAdd", "TodoSelect", "WeekDay" }),
            ("Plan", new[] { "CalendarDay", "DialogSave", "PlanCreate", "PlanMove", "PlanEdit", "PlanTodo", "PlanSelect" }),
            ("Items", new[] { "ItemSearch", "ItemPictures", "ItemEscape", "PictureDrag", "CustomLayout", "ChecklistSelect" })
        }.SelectMany(g => g.Ids.Select(id => new Tip(g.Group, id))).ToList();
    }
}
