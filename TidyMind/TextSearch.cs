using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TidyMind
{
    // How every search box matches text: ignoring case and accents, so "arvizturo" finds "Árvíztűrő" (and "ő", "ű"
    // match "o", "u"). Both sides are decomposed (FormD) and their combining marks dropped.
    public static class TextSearch
    {
        public static bool Contains(string text, string query) => IndexOf(text, query, out _) >= 0;

        // Where `query` starts in `text` and how many of text's characters it covers (for highlighting the match in
        // the original, accented text); -1 if it isn't there or the query is empty.
        public static int IndexOf(string text, string query, out int length)
        {
            length = 0;
            if (string.IsNullOrEmpty(text))
                return -1;
            return new SearchableText(text).IndexOf(FoldQuery(query), out length);
        }

        // A query folded once, to look for in many prepared texts (SearchableText.IndexOf).
        public static string FoldQuery(string query) => string.IsNullOrEmpty(query) ? "" : Fold(query, null);

        // Lower-case, accents removed. `origin` (if given) gets, for each folded character, the index of the
        // original character it came from.
        internal static string Fold(string text, List<int>? origin)
        {
            StringBuilder folded = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c < 128 || char.IsSurrogate(c)) // plain ASCII (the common case) and emoji halves: nothing to strip
                {
                    Add(folded, origin, char.ToLowerInvariant(c), i);
                    continue;
                }

                foreach (char part in c.ToString().Normalize(NormalizationForm.FormD))
                {
                    if (!IsMark(part))
                        Add(folded, origin, char.ToLowerInvariant(part), i);
                }
            }
            return folded.ToString();
        }

        private static void Add(StringBuilder folded, List<int>? origin, char c, int from)
        {
            folded.Append(c);
            origin?.Add(from);
        }

        internal static bool IsMark(char c) => CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark;
    }

    // A text folded once by TextSearch's rules, to be searched again and again: global search prepares every entry
    // (note pages included) when its index is built, so each key typed only compares strings that are ready.
    public sealed class SearchableText
    {
        private readonly string folded;
        private readonly List<int> origin; // for each folded character, the index of the original one

        public SearchableText(string? text)
        {
            Text = text ?? "";
            origin = new List<int>(Text.Length);
            folded = TextSearch.Fold(Text, origin);
        }

        public string Text { get; }

        // foldedQuery: from TextSearch.FoldQuery. Where the match starts in Text and how many of Text's characters it
        // covers; -1 if it isn't there or the query is empty.
        public int IndexOf(string foldedQuery, out int length)
        {
            length = 0;
            if (foldedQuery.Length == 0)
                return -1;

            int at = folded.IndexOf(foldedQuery, System.StringComparison.Ordinal);
            if (at < 0)
                return -1;

            int start = origin[at];
            int end = origin[at + foldedQuery.Length - 1] + 1;
            while (end < Text.Length && TextSearch.IsMark(Text[end])) // an accent typed as a separate character belongs to the match
                end++;
            length = end - start;
            return start;
        }
    }
}
