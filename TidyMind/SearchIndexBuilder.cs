using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

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

        // Position in the memory's data file (-1 for a memory itself). Views load the file in the same order.
        public int ItemIndex { get; set; } = -1;
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

        private static void AddProjects(List<SearchResult> index, Profile profile)
        {
            // Same order ProjectView sorts into, so ItemIndex points at the same project there.
            List<Project> projects = Load<Project>(profile.Name + ".json").OrderBy(p => p.Order).ToList();

            for (int p = 0; p < projects.Count; p++)
            {
                Project project = projects[p];

                index.Add(new SearchResult
                {
                    Kind = SearchResultKind.Project,
                    Title = project.Name,
                    Location = profile.Name + Arrow + project.Name,
                    Profile = profile,
                    ItemIndex = p
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
                        ItemIndex = p
                    });
                }
            }
        }

        private static void AddEntities(List<SearchResult> index, Profile profile)
        {
            // Same order CollectionView sorts into, so ItemIndex points at the same item there.
            List<Entity> entities = Load<Entity>(profile.Name + "_entities.json").OrderBy(e => e.Order).ToList();

            for (int i = 0; i < entities.Count; i++)
            {
                index.Add(new SearchResult
                {
                    Kind = SearchResultKind.Entity,
                    Title = entities[i].Name,
                    Location = profile.Name + Arrow + entities[i].Name,
                    Profile = profile,
                    ItemIndex = i
                });
            }
        }

        // A missing or unreadable file just contributes nothing — search must never crash the app.
        private static List<T> Load<T>(string fileName)
        {
            string path = AppPaths.Data(fileName);
            if (!File.Exists(path))
                return new List<T>();

            try
            {
                return JsonSerializer.Deserialize<List<T>>(File.ReadAllText(path)) ?? new List<T>();
            }
            catch (Exception)
            {
                return new List<T>();
            }
        }
    }
}
