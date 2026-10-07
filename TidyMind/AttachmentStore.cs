using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TidyMind
{
    // Attached files: copied into a flat folder in the app's data folder (next to the memories' .json files, like
    // CardImages/) under a new GUID name, so they don't depend on the original staying put or on any name in the app.
    public static class AttachmentStore
    {
        public const string Folder = "Attachments";

        // Above this, adding a file asks first: copying it takes a while and fills the disk.
        public const long LargeFileBytes = 200L * 1024 * 1024;

        public static string PathOf(string storedFileName) => Path.GetFullPath(Path.Combine(Folder, Path.GetFileName(storedFileName)));

        public static bool Exists(Attachment attachment) =>
            !string.IsNullOrEmpty(attachment?.StoredFileName) && File.Exists(PathOf(attachment.StoredFileName));

        // Copies the file in on a worker thread, reporting 0..1 as it goes, and returns its attachment entry; null if
        // it couldn't be read or written. Copied to a temporary name first, so a failed or stopped copy never leaves
        // a half-written file under a real name. Cancelling throws OperationCanceledException (after cleaning up).
        public static async Task<Attachment> ImportAsync(string sourcePath, IProgress<double> progress, CancellationToken cancel)
        {
            string storedFileName = Guid.NewGuid().ToString("N") + Path.GetExtension(sourcePath).ToLowerInvariant();
            string target = PathOf(storedFileName);
            string temp = target + ".tmp";
            try
            {
                long size = await Task.Run(() => Copy(sourcePath, temp, target, progress, cancel), cancel);
                return new Attachment
                {
                    Id = Guid.NewGuid(),
                    DisplayName = Path.GetFileName(sourcePath),
                    StoredFileName = storedFileName,
                    SizeBytes = size,
                    AddedDate = DateTime.Now
                };
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is OperationCanceledException)
            {
                try { File.Delete(temp); } catch (Exception) { /* best effort: a stray .tmp is harmless */ }
                if (e is OperationCanceledException)
                    throw;
                return null;
            }
        }

        private static async Task<long> Copy(string sourcePath, string temp, string target, IProgress<double> progress,
            CancellationToken cancel)
        {
            const int BufferSize = 1 << 20;
            Directory.CreateDirectory(Path.GetDirectoryName(target));

            // Progress at most every 50 ms: from a fast disk, one report per chunk would flood the UI thread with
            // updates that outrank input, and the dialog would feel stuck while it copies.
            Stopwatch sinceReport = Stopwatch.StartNew();
            long copied = 0;
            using (FileStream from = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                       BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (FileStream to = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       BufferSize, FileOptions.Asynchronous))
            {
                long length = from.Length;
                byte[] buffer = new byte[BufferSize];
                int read;
                while ((read = await from.ReadAsync(buffer.AsMemory(), cancel)) > 0)
                {
                    await to.WriteAsync(buffer.AsMemory(0, read), cancel);
                    copied += read;
                    if (sinceReport.ElapsedMilliseconds >= 50)
                    {
                        progress?.Report((double)copied / length);
                        sinceReport.Restart();
                    }
                }
            }

            File.Move(temp, target);
            return copied;
        }

        // Removes copies nothing uses any more. Best effort: a file that can't be deleted now is just left behind.
        public static void Delete(IEnumerable<Attachment> attachments)
        {
            foreach (Attachment attachment in attachments ?? Enumerable.Empty<Attachment>())
            {
                if (string.IsNullOrEmpty(attachment?.StoredFileName))
                    continue;

                string path = PathOf(attachment.StoredFileName);
                ImageCache.Forget(path); // an image's thumbnail
                try
                {
                    File.Delete(path);
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                }
            }
        }
    }
}
