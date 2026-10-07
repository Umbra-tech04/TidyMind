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
        File // a project's attached file; opens the project
    }

    // One searchable thing plus enough to navigate back to it.
    public class SearchResult
    {
        public SearchResultKind Kind { get; set; }
        public string Title { get; set; }
        public string Location { get; set; }
        public Profile Profile { get; set; }

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

            return index;
        }

        // Case-insensitive partial match. Names that start with the query rank first, then by kind.
        public static List<SearchResult> Filter(List<SearchResult> index, string query, int limit)
        {
            query = (query ?? "").Trim();
            if (query.Length == 0)
                return new List<SearchResult>();

            return index
                .Select(r => new { Result = r, At = (r.Title ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) })
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
                    Location = profile.Name + Arrow + entity.Name,
                    Profile = profile,
                    ItemId = entity.Id
                });
            }
        }
    }
}
