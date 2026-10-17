using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace TidyMind
{
    // The one question before a selection is deleted, and the one report if part of it couldn't be: the same words
    // for every list. The delete itself is each list's own (the same code as deleting one item).
    public static class BulkDelete
    {
        private const int NamesShown = 5;

        // "Delete 7 projects?", up to five of their names, "and 2 more", then `warning` (if any) on its own line.
        // question: a plural key ("Bulk.Delete.Projects"). Delete / Cancel; `serious` makes Cancel the default.
        public static bool Confirm(Window? owner, string question, IReadOnlyList<string> names, string? warning = null,
            bool serious = false)
        {
            return Msg.ConfirmDelete(owner, Text(question, names, warning), Loc.T("Common.ConfirmDelete"), Loc.T("Common.Delete"), serious);
        }

        // The same for taking files out of a project (they go once it's saved): Remove / Cancel.
        public static bool ConfirmRemove(Window? owner, string question, IReadOnlyList<string> names)
        {
            return Msg.ConfirmDelete(owner, Text(question, names, null), Loc.T("Files.RemoveManyCaption"), Loc.T("Files.Remove"));
        }

        private static string Text(string question, IReadOnlyList<string> names, string? warning)
        {
            string text = Loc.Plural(question, names.Count) + "\n\n" + NameList(names);
            return warning == null ? text : text + "\n\n" + warning;
        }

        // After a batch in which some couldn't be deleted (each item has its own outside step, such as a reminder's
        // scheduled task): which are still there, and why.
        public static void ReportKept(Window? owner, IReadOnlyList<string> kept, string reason)
        {
            Msg.Show(owner, Loc.Plural("Bulk.Kept", kept.Count) + "\n\n" + NameList(kept) + "\n\n" + reason,
                Loc.T("Common.CouldntDelete"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // One per line, the rest counted.
        internal static string NameList(IReadOnlyList<string> names)
        {
            IEnumerable<string> lines = names.Take(NamesShown).Select(name => "•  " + Shorten(name));
            if (names.Count > NamesShown)
                lines = lines.Append(Loc.Plural("Bulk.More", names.Count - NamesShown));
            return string.Join("\n", lines);
        }

        // A long name (a to-do can be a paragraph) stays on one line of the box.
        private static string Shorten(string name)
        {
            string line = (name ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (line.Length <= 60)
                return line;
            int cut = char.IsHighSurrogate(line[58]) ? 58 : 59; // never half an emoji
            return line.Substring(0, cut).TrimEnd() + "…";
        }
    }
}
