using System;
using System.Collections.Generic;
using System.Linq;

namespace TidyMind
{
    public enum SearchResultKind
    {
        Memory,
        Project,
        Entity,
        File, // a project's attached file; opens the project
        Note  // a page of a project's notes; opens the project at that page
    }

    // One searchable thing plus enough to navigate back to it.
    public class SearchResult
    {
        public SearchResultKind Kind { get; set; }
        public string Title { get; set; }
        public string Location { get; set; }
        public Profile Profile { get; set; }

        // A note page's text: searched instead of the title (its first line), and shown around the match.
        public string? Text { get; set; }
        public int Page { get; set; } // which note page (0-based), for Note results

        // Text (or else the title) folded for searching, made once: Build prepares every entry, so typing a query
        // only compares ready strings.
        public SearchableText Searchable => searchable ??= new SearchableText(Text ?? Title);
        private SearchableText? searchable;

        // The project's or item's Id (Guid.Empty for a memory itself).
        public Guid ItemId { get; set; }
    }

    public static class SearchIndexBuilder
    {
        private const string Arrow = "  →  ";

        // Read fresh on every open: the files are small and this can never go stale.
        public static List<SearchResult> Build(IEnumerable<Profile> profiles)
        {
            List<SearchResult> index = new List<SearchResult>();

            foreach (Profile profile in profiles)
            {
                index.Add(new SearchResult
                {
                    Kind = SearchResultKind.Memory,
                    Title = profile.Name,
                    Location = profile.Type == ProfileType.Collection ? "Collection memory" : "Project memory",
                    Profile = profile
                });

                if (profile.Type == ProfileType.Collection)
                    AddEntities(index, profile);
                else
                    AddProjects(index, profile);
            }

            // Folded now, while the index is built, rather than on the first key typed.
            foreach (SearchResult result in index)
                _ = result.Searchable;
            return index;
        }

        // Case-insensitive partial match (a note page by its text). Names that start with the query rank first, then by kind.
        public static List<SearchResult> Filter(List<SearchResult> index, string query, int limit)
        {
            string folded = TextSearch.FoldQuery((query ?? "").Trim());
            if (folded.Length == 0)
                return new List<SearchResult>();

            return index
                .Select(r => new { Result = r, At = r.Searchable.IndexOf(folded, out _) })
                .Where(x => x.At >= 0)
                .OrderBy(x => x.At == 0 ? 0 : 1)
                .ThenBy(x => x.Result.Kind)
                .Select(x => x.Result)
                .Take(limit)
                .ToList();
        }

        // A missing or unreadable file just contributes nothing — search must never crash the app.
        private static void AddProjects(List<SearchResult> index, Profile profile)
        {
            foreach (Project project in ProjectStore.LoadOrEmpty(profile))
            {
                index.Add(new SearchResult
                {
                    Kind = SearchResultKind.Project,
                    Title = project.Name,
                    Location = profile.Name + Arrow + project.Name,
                    Profile = profile,
                    ItemId = project.Id
                });

                // Its attached files by name: picking one opens the project they're in.
                foreach (Attachment attachment in project.Attachments ?? new List<Attachment>())
                {
                    index.Add(new SearchResult
                    {
                        Kind = SearchResultKind.File,
                        Title = attachment.DisplayName,
                        Location = profile.Name + Arrow + project.Name,
                        Profile = profile,
                        ItemId = project.Id
                    });
                }

                // Its note pages with writing on them (an old single note is page 1, as Project Details shows it).
                List<NotePage> pages = project.PagesOrLegacyNote();
                for (int i = 0; i < pages.Count; i++)
                {
                    string text = RichTextHelper.ToPlainText(pages[i].Body ?? "");
                    string firstLine = text.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0) ?? "";
                    if (firstLine.Length == 0) continue;

                    index.Add(new SearchResult
                    {
                        Kind = SearchResultKind.Note,
                        Title = firstLine,
                        Text = text,
                        Location = profile.Name + Arrow + project.Name + Arrow + "Page " + (i + 1),
                        Profile = profile,
                        ItemId = project.Id,
                        Page = i
                    });
                }
            }
        }

        private static void AddEntities(List<SearchResult> index, Profile profile)
        {
            foreach (Entity entity in EntityStore.LoadOrEmpty(profile))
            {
                index.Add(new SearchResult
                {
                    Kind = SearchResultKind.Entity,
                    Title = entity.Name,
                    Text = EntityTypes.SearchText(entity), // a vehicle: every field and log row too; null: by name
                    Location = profile.Name + Arrow + entity.Name,
                    Profile = profile,
                    ItemId = entity.Id
                });
            }
        }
    }
}
