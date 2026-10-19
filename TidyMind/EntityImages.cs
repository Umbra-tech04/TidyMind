using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TidyMind
{
    // A collection item's pictures: the cover (Entity.ImagePath) and the further ones (Entity.MoreImages), at most
    // MaxPictures. Items keep their own copies in EntityImages/ (ImageStore.Items) by file name. Items from before
    // that hold the full path of the user's own picture as their cover until the collection view copies it in; that
    // original is never deleted or changed. A copy is deleted once no item refers to it any more (DeleteUnreferenced),
    // since imported data can have two items on one file.
    public static class EntityImages
    {
        public const int MaxPictures = 20;

        public static ImageStore Store => ImageStore.Items;

        // A full path: the user's own file, not a copy of ours.
        public static bool IsLegacyPath(string imagePath) => !string.IsNullOrEmpty(imagePath) && Path.IsPathRooted(imagePath);

        // ---- The item's list of pictures ---------------------------------------

        // The item's pictures as stored, cover first. Empty names are skipped.
        public static List<string> Names(Entity entity)
        {
            List<string> names = new List<string>();
            if (!string.IsNullOrEmpty(entity.ImagePath))
                names.Add(entity.ImagePath);
            if (entity.MoreImages != null)
                names.AddRange(entity.MoreImages.Where(n => !string.IsNullOrEmpty(n)));
            return names;
        }

        public static int Count(Entity entity) => Names(entity).Count;

        // The first of the names becomes the cover; none at all leaves the item with no picture (and no MoreImages
        // field in the file).
        public static void SetNames(Entity entity, IReadOnlyList<string> names)
        {
            entity.ImagePath = names.Count > 0 ? names[0] : null;
            entity.MoreImages = names.Count > 1 ? names.Skip(1).ToList() : null;
        }

        // ---- Files -------------------------------------------------------------

        // The cover: the first stored name (ImagePath, or the first further one when a file from elsewhere has none
        // there), or null for no picture.
        public static string Cover(Entity entity) => Names(entity).FirstOrDefault();

        // The cover's file, or null if the item has none.
        public static string FullPath(Entity entity) => FullPath(Cover(entity));

        // A stored name's file (or the user's own, for a legacy path), or null for no name.
        public static string FullPath(string name)
        {
            if (string.IsNullOrEmpty(name))
                return null;
            return IsLegacyPath(name) ? name : Store.PathOf(name);
        }

        // The cover, decoded once per session; null if the item has no picture or it's missing or unreadable.
        public static BitmapImage Load(Entity entity) => Load(Cover(entity));

        public static BitmapImage Load(string name)
        {
            string path = FullPath(name);
            return path == null ? null : ImageCache.GetOrLoad(path, Store.DecodeWidth);
        }

        // Already decoded this session, or null: to draw at once, without waiting for a decode.
        public static BitmapImage Loaded(string name)
        {
            string path = FullPath(name);
            return path == null ? null : ImageCache.TryGet(path);
        }

        // Decoded on a worker thread (once per session); null if missing or unreadable.
        public static System.Threading.Tasks.Task<BitmapImage> LoadAsync(string name)
        {
            string path = FullPath(name);
            return path == null ? System.Threading.Tasks.Task.FromResult<BitmapImage>(null) : ImageCache.LoadAsync(path, Store.DecodeWidth);
        }

        // Removes a copy nothing refers to. Never touches a legacy path (the user's own file).
        public static void DeleteCopy(string imagePath)
        {
            if (!string.IsNullOrEmpty(imagePath) && !IsLegacyPath(imagePath))
                Store.Delete(imagePath);
        }

        // Removes the copies among `names` that no item in any collection refers to (the saved files say so; `inUse`
        // are items of the view that is asking, in case its memory isn't in profiles.json). If a file can't be read,
        // nothing is removed: a copy left behind is harmless, a deleted one that was in use isn't.
        public static void DeleteUnreferenced(IEnumerable<string> names, IEnumerable<Entity> inUse = null) =>
            EntityFiles.DeleteUnreferenced(names.Where(n => !IsLegacyPath(n)), EntityFiles.Pictures, inUse, Store.Delete);

        // ---- Saving the form's list --------------------------------------------

        // Gives the item the pictures of the form's list, in its order: a picked file is copied in under a new name,
        // the others keep theirs. A picked file that can't be copied (gone since it was picked, unreadable) is left
        // out and named in `failed`; the rest of the list (removals, order, cover) still applies. `imported` are the
        // new copies, for the caller to remove if it can't save the item after all.
        public static void Apply(Entity entity, IReadOnlyList<PictureSlot> slots, out List<string> imported, out List<string> failed) =>
            SetNames(entity, Import(slots, out imported, out failed));

        // The stored names of the slots, in their order, picked files copied in (as Apply; a record's pictures too).
        public static List<string> Import(IReadOnlyList<PictureSlot> slots, out List<string> imported, out List<string> failed)
        {
            imported = new List<string>();
            failed = new List<string>();
            List<string> names = new List<string>();
            foreach (PictureSlot slot in slots)
            {
                string name = slot.Stored;
                if (name == null)
                {
                    name = Store.Import(slot.PickedFile);
                    if (name == null)
                    {
                        failed.Add(slot.PickedFile);
                        continue;
                    }
                    imported.Add(name);
                }
                names.Add(name);
            }
            return names;
        }
    }

    // One picture in the item form's list: already stored under a name (Stored), or a file just picked (PickedFile,
    // the user's own, copied in when the item is saved).
    public sealed class PictureSlot
    {
        public string Stored;
        public string PickedFile;

        // What the form shows for it, decoded once.
        public ImageSource Preview;

        // Files picked to go after `slots`: each readable picture joins them (previewed), up to EntityImages.MaxPictures.
        // What is left out (unreadable, over the limit) is said in one message over `owner`. Returns the index of the first
        // one added, or -1 for none.
        public static int AddPicked(List<PictureSlot> slots, IEnumerable<string> files, System.Windows.Window owner)
        {
            int firstNew = slots.Count;
            bool overLimit = false;
            List<string> unreadable = new List<string>();
            foreach (string file in files)
            {
                if (slots.Count >= EntityImages.MaxPictures)
                {
                    overLimit = true;
                    break;
                }
                ImageSource preview = EntityImages.Store.Decode(file);
                if (preview == null)
                    unreadable.Add(Path.GetFileName(file));
                else
                    slots.Add(new PictureSlot { PickedFile = file, Preview = preview });
            }

            List<string> problems = new List<string>();
            if (unreadable.Count > 0)
                problems.Add(Loc.F("Entity.Pictures.Unreadable", string.Join(", ", unreadable.Take(5)) + (unreadable.Count > 5 ? ", …" : "")));
            if (overLimit)
                problems.Add(Loc.F("Entity.Pictures.Limit", EntityImages.MaxPictures));
            if (problems.Count > 0)
                Msg.Show(owner, string.Join("\n\n", problems), Loc.T("Entity.Pictures.NotAdded.Caption"), System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
            return slots.Count > firstNew ? firstNew : -1;
        }
    }
}
