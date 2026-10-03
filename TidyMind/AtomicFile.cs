using System;
using System.IO;

namespace TidyMind
{
    // Saving without risking the existing file: the text goes to a temporary file first, which then replaces the
    // original in one step. A crash or a full disk mid-write leaves the old file intact instead of half-written JSON.
    public static class AtomicFile
    {
        // False if the file couldn't be written (locked, no access, disk full); the original is then unchanged.
        public static bool TryWriteAllText(string path, string text)
        {
            string temp = path + ".tmp";
            try
            {
                File.WriteAllText(temp, text);
                File.Move(temp, path, overwrite: true);
                return true;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                try { File.Delete(temp); } catch (Exception) { /* best effort: a stray .tmp is harmless */ }
                return false;
            }
        }
    }
}
