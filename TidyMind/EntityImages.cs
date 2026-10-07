using System.IO;
using System.Windows.Media.Imaging;

namespace TidyMind
{
    // A collection item's picture (Entity.ImagePath). Items keep their own copy in EntityImages/ (ImageStore.Items)
    // by file name, deleted with the item. Items from before that hold the full path of the user's own picture until
    // the collection view copies it in; that original is never deleted or changed.
    public static class EntityImages
    {
        public static ImageStore Store => ImageStore.Items;

        // A full path: the user's own file, not a copy of ours.
        public static bool IsLegacyPath(string imagePath) => !string.IsNullOrEmpty(imagePath) && Path.IsPathRooted(imagePath);

        // The picture's file, or null if the item has none.
        public static string FullPath(Entity entity)
        {
            if (string.IsNullOrEmpty(entity.ImagePath))
                return null;
            return IsLegacyPath(entity.ImagePath) ? entity.ImagePath : Store.PathOf(entity.ImagePath);
        }

        // Decoded once per session; null if the item has no picture or it's missing or unreadable.
        public static BitmapImage Load(Entity entity)
        {
            string path = FullPath(entity);
            return path == null ? null : ImageCache.GetOrLoad(path, Store.DecodeWidth);
        }

        // Removes the item's copy once nothing refers to it. Never touches a legacy path (the user's own file).
        public static void DeleteCopy(string imagePath)
        {
            if (!string.IsNullOrEmpty(imagePath) && !IsLegacyPath(imagePath))
                Store.Delete(imagePath);
        }
    }
}
