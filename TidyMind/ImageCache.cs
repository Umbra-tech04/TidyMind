using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace TidyMind
{
    // Decoded background pictures (card and page), kept for the whole session and keyed by full path: each file is
    // read and decoded once, and every later view of it (another tab, reopening the memory) is instant. Safe to use
    // from any thread, so the first decode can run off the UI thread (LoadAsync) while the view shows a placeholder.
    public static class ImageCache
    {
        // Lazy: two cards asking for the same picture at once still decode it only once.
        private static readonly ConcurrentDictionary<string, Lazy<BitmapImage>> images =
            new ConcurrentDictionary<string, Lazy<BitmapImage>>(StringComparer.OrdinalIgnoreCase);

        // The picture if it has already been decoded this session (null otherwise, or if it couldn't be read).
        public static BitmapImage TryGet(string path)
        {
            return images.TryGetValue(Path.GetFullPath(path), out Lazy<BitmapImage> entry) && entry.IsValueCreated ? entry.Value : null;
        }

        // Decodes on the calling thread the first time; null if the file is missing or not a readable image.
        public static BitmapImage GetOrLoad(string path, int decodePixelWidth)
        {
            string fullPath = Path.GetFullPath(path);
            return images.GetOrAdd(fullPath,
                p => new Lazy<BitmapImage>(() => Decode(p, decodePixelWidth), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        }

        // The same, decoded on a worker thread; the result is frozen, so it can be used on the UI thread.
        public static Task<BitmapImage> LoadAsync(string path, int decodePixelWidth)
        {
            BitmapImage cached = TryGet(path);
            return cached != null ? Task.FromResult(cached) : Task.Run(() => GetOrLoad(path, decodePixelWidth));
        }

        // A deleted picture's file name is never reused (GUID names), but drop it anyway to free the memory.
        public static void Forget(string path)
        {
            images.TryRemove(Path.GetFullPath(path), out _);
        }

        // OnLoad reads the file and lets go of it right away (so it can be deleted); DecodePixelWidth decodes
        // straight to the size it's shown at instead of the full photo; Freeze makes it usable from any thread
        // and spares WPF from tracking it for changes.
        public static BitmapImage Decode(string path, int decodePixelWidth)
        {
            try
            {
                BitmapImage bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = decodePixelWidth;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bitmap.UriSource = new Uri(Path.GetFullPath(path));
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch (Exception e) when (e is IOException || e is NotSupportedException || e is UnauthorizedAccessException
                                      || e is ArgumentException || e is FileFormatException || e is UriFormatException)
            {
                return null;
            }
        }
    }
}
