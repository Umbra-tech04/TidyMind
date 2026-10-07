using System;
using System.IO;
using System.Text;
using System.Threading;

namespace TidyMind
{
    // Saving without risking the existing file: the text goes to a temporary file first, which then replaces the
    // original in one step. A crash, a power cut or a full disk mid-write leaves the old file intact instead of
    // half-written JSON.
    public static class AtomicFile
    {
        private const int ReplaceAttempts = 5;
        private const int RetryDelayMs = 100;

        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        // False if the file couldn't be written (locked, no access, disk full); the original is then unchanged.
        public static bool TryWriteAllText(string path, string text)
        {
            // Unique per write: the app and the reminder process may save the same file at the same moment.
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (FileStream stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    byte[] bytes = Utf8NoBom.GetBytes(text);
                    stream.Write(bytes, 0, bytes.Length);
                    // On disk before the swap: otherwise a power cut can keep the swap but lose the contents.
                    stream.Flush(flushToDisk: true);
                }

                ReplaceWithRetries(temp, path);
                return true;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                try { File.Delete(temp); } catch (Exception) { /* best effort: a stray .tmp is harmless */ }
                return false;
            }
        }

        // Another process reading the file at that instant (the app, the reminder process, a backup or antivirus
        // scanner) makes the swap fail briefly; it's tried again a few times before giving up.
        private static void ReplaceWithRetries(string temp, string path)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    if (File.Exists(path))
                        File.Replace(temp, path, null, ignoreMetadataErrors: true);
                    else
                        File.Move(temp, path);
                    return;
                }
                catch (Exception e) when ((e is IOException || e is UnauthorizedAccessException) && attempt < ReplaceAttempts)
                {
                    Thread.Sleep(RetryDelayMs);
                }
            }
        }
    }
}
