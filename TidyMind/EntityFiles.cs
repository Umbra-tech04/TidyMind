using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TidyMind
{
    // The files a collection item keeps in the app's folders: its pictures in EntityImages/ (EntityImages) and its
    // records' files in Attachments/ (a vehicle document's scan). Each is deleted only once no item of any collection
    // refers to it any more, since imported data can have two items on one file.
    public static class EntityFiles
    {
        public static IEnumerable<string> Pictures(Entity entity) => EntityImages.Names(entity);

        public static IEnumerable<string> Attachments(Entity entity) =>
            (entity.Records?.Values ?? Enumerable.Empty<List<EntityRecord>>())
            .Where(list => list != null).SelectMany(list => list)
            .Select(record => record?.File?.StoredFileName).Where(name => !string.IsNullOrEmpty(name))!;

        // After the items were saved without `gone` (deleted, or an edited item as it was before): the files of theirs
        // that no item uses any more (`inUse`: the asking view's own, in case its memory isn't in profiles.json).
        public static void DeleteUnreferenced(IEnumerable<Entity> gone, IEnumerable<Entity>? inUse = null)
        {
            List<Entity> items = gone.ToList();
            EntityImages.DeleteUnreferenced(items.SelectMany(Pictures), inUse);
            DeleteUnreferenced(items.SelectMany(Attachments), Attachments, inUse,
                name => AttachmentStore.Delete(new[] { new Attachment { StoredFileName = name } }));
        }

        // Deletes the `names` (file names in one folder) that no saved item's `filesOf` holds. If a collection can't be
        // read, nothing is deleted: a copy left behind is harmless, a deleted one that was in use isn't.
        internal static void DeleteUnreferenced(IEnumerable<string> names, Func<Entity, IEnumerable<string>> filesOf,
            IEnumerable<Entity>? inUse, Action<string> delete)
        {
            List<string> candidates = names.Where(n => !string.IsNullOrEmpty(n))
                .Select(n => Path.GetFileName(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (candidates.Count == 0 || !EntityStore.TryLoadAll(out List<Entity> saved))
                return;

            HashSet<string> used = saved.Concat(inUse ?? Enumerable.Empty<Entity>()).SelectMany(filesOf)
                .Select(n => Path.GetFileName(n)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (string name in candidates.Where(n => !used.Contains(n)))
                delete(name);
        }
    }
}
