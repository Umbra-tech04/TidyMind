using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TidyMind
{
    // Background pictures the user picked: the file is copied into a folder in the app's data folder (next to the
    // memories' .json files) under a new GUID name, so it doesn't depend on the original staying put; the owner
    // (a project card, a memory's page) stores only that file name. Decoded pictures are kept in ImageCache.
    public class ImageStore
    {
        public const string FileFilter = "Images (*.jpg;*.jpeg;*.png;*.webp)|*.jpg;*.jpeg;*.png;*.webp";

        // Card pictures: decoded at about twice the full card's width, sharp on high-DPI screens without holding
        // full-size photos for every customised card.
        public static readonly ImageStore Cards = new ImageStore("CardImages", 400);

        // Memory page backgrounds: they cover the whole view (and are washed out to a faint watermark anyway).
        public static readonly ImageStore Backgrounds = new ImageStore("BackgroundImages", 1000);

        private ImageStore(string folder, int decodeWidth)
        {
            Folder = AppPaths.Data(folder);
            DecodeWidth = decodeWidth;
        }

        public string Folder { get; }
        public int DecodeWidth { get; }

        // Whether a stored picture is there at all: cheap, so a view can decide on the spot between "show it (or
        // a placeholder while it decodes)" and "fall back to the default look".
        public bool Exists(string fileName)
        {
            return !string.IsNullOrEmpty(fileName) && File.Exists(PathOf(fileName));
        }

        // Already decoded this session: null otherwise. Views use this first, so a picture they've shown before
        // appears at once, with no placeholder.
        public ImageSource TryGetLoaded(string fileName)
        {
            return string.IsNullOrEmpty(fileName) ? null : ImageCache.TryGet(PathOf(fileName));
        }

        // Decoded on a worker thread (once per session; ImageCache); null if missing or unreadable.
        public Task<BitmapImage> LoadAsync(string fileName)
        {
            return string.IsNullOrEmpty(fileName)
                ? Task.FromResult<BitmapImage>(null)
                : ImageCache.LoadAsync(PathOf(fileName), DecodeWidth);
        }

        // A picked file, decoded at this store's size but not cached: to check it and preview it before it's copied
        // in. Null if it isn't a readable image.
        public ImageSource Decode(string path)
        {
            return ImageCache.Decode(path, DecodeWidth);
        }

        private string PathOf(string fileName) => Path.Combine(Folder, Path.GetFileName(fileName));

        // Copies the picked file in and returns the new file name, or null if it couldn't be copied. Copied to a
        // temporary name first, so a failed copy never leaves a half-written image under a real name.
        public string Import(string sourcePath)
        {
            string fileName = Guid.NewGuid().ToString("N") + Path.GetExtension(sourcePath).ToLowerInvariant();
            string target = Path.Combine(Folder, fileName);
            string temp = target + ".tmp";
            try
            {
                Directory.CreateDirectory(Folder);
                File.Copy(sourcePath, temp);
                File.Move(temp, target);
                return fileName;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                try { File.Delete(temp); } catch (Exception) { /* best effort: a stray .tmp is harmless */ }
                return null;
            }
        }

        // Removes a copy nothing uses any more. Best effort: a file that can't be deleted now is just left behind.
        public void Delete(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
                return;

            ImageCache.Forget(PathOf(fileName));
            try
            {
                File.Delete(Path.Combine(Folder, Path.GetFileName(fileName)));
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
            }
        }
    }
}
