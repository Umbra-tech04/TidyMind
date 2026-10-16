using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace TidyMind
{
    // One-time copy of the data older versions kept in the working folder (normally next to the exe) into
    // AppPaths.DataFolder. Copies only: the old files are never moved or deleted. The marker is written only once
    // every file has arrived and checked out, and nothing reads the new folder before that, so a copy cut short
    // (crash, full disk) is finished from the untouched originals on the next start. Files already in the data folder
    // are never copied over (CopyAndVerify).
    public static class LegacyDataMigration
    {
        private const string MarkerFile = "legacy-data-copied.txt";

        // The app-wide files: a folder holding any of them was used as TidyMind's data folder.
        private static readonly string[] KnownFiles =
            { "profiles.json", "reminders.json", "calendar.json", "quicktodos.json", "quicknotes.txt" };

        private static readonly string[] KnownFolders = { "Attachments", "CardImages", "BackgroundImages" };

        // Build output that sits next to the exe and isn't data.
        private static readonly string[] NotDataSuffixes = { ".deps.json", ".runtimeconfig.json", ".runtimeconfig.dev.json" };

        // False (with the reason) if the copy couldn't be completed; the data folder must then not be used.
        // Serialised across processes: the reminder process may start while the app is copying.
        public static bool TryRun(out string problem)
        {
            using (Mutex gate = new Mutex(false, @"Local\TidyMind_DataMigration"))
            {
                try
                {
                    gate.WaitOne();
                }
                catch (AbandonedMutexException)
                {
                    // The other process died mid-copy; we own the mutex now and redo the copy.
                }

                try
                {
                    return RunOnce(out problem);
                }
                finally
                {
                    gate.ReleaseMutex();
                }
            }
        }

        private static bool RunOnce(out string problem)
        {
            problem = null;
            string marker = AppPaths.Data(MarkerFile);
            if (File.Exists(marker))
                return true;

            try
            {
                Directory.CreateDirectory(AppPaths.DataFolder);
                string source = FindLegacyFolder();
                if (source != null)
                    CopyAndVerify(source, AppPaths.DataFolder);

                string note = source == null
                    ? "No data from an older version was found."
                    : "Copied from " + source + ". The originals there were left untouched.";
                if (!AtomicFile.TryWriteAllText(marker, note + Environment.NewLine + DateTime.Now.ToString("u") + Environment.NewLine))
                    throw new IOException(Loc.F("Migration.MarkerFailed", marker));
                return true;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                problem = e.Message;
                return false;
            }
        }

        // The exe's folder and the folder the app was started from; if both hold data, the more recently used one.
        private static string FindLegacyFolder()
        {
            string target = Normalize(AppPaths.DataFolder);
            return new[] { AppContext.BaseDirectory, Environment.CurrentDirectory }
                .Select(Normalize)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(folder => !string.Equals(folder, target, StringComparison.OrdinalIgnoreCase))
                .Select(folder => new { Folder = folder, LastUsed = LastUsed(folder) })
                .Where(x => x.LastUsed != null)
                .OrderByDescending(x => x.LastUsed)
                .Select(x => x.Folder)
                .FirstOrDefault();
        }

        private static DateTime? LastUsed(string folder)
        {
            List<DateTime> times = KnownFiles
                .Select(name => Path.Combine(folder, name))
                .Where(File.Exists)
                .Select(File.GetLastWriteTimeUtc)
                .ToList();
            return times.Count == 0 ? (DateTime?)null : times.Max();
        }

        private static string Normalize(string folder) =>
            Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // Every top-level .json (the memories' files have user-chosen names, so they can't be listed by name),
        // quicknotes.txt, and the picture and attachment folders. Leftover .tmp files are skipped.
        private static IEnumerable<string> DataFiles(string source)
        {
            IEnumerable<string> topLevel = Directory.EnumerateFiles(source, "*.json")
                .Where(path => !NotDataSuffixes.Any(suffix => path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)));
            string notes = Path.Combine(source, "quicknotes.txt");
            if (File.Exists(notes))
                topLevel = topLevel.Append(notes);

            IEnumerable<string> inFolders = KnownFolders
                .Select(name => Path.Combine(source, name))
                .Where(Directory.Exists)
                .SelectMany(folder => Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                .Where(path => !path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));

            return topLevel.Concat(inFolders).ToList();
        }

        // Never over a file that's already there. The marker going missing (deleted, or the data folder restored
        // without it) makes this run again long after the first time, when the data folder holds everything done
        // since and the old folder only what was there on the day of the upgrade. So existing files are kept, and
        // error.log notes the ones that differ. Each file is copied under a temporary name and only then given its
        // real one, so a copy cut short never leaves a partial file that would later count as already there.
        private static void CopyAndVerify(string source, string target)
        {
            List<string> kept = new List<string>();
            foreach (string from in DataFiles(source))
            {
                string relative = Path.GetRelativePath(source, from);
                string to = Path.Combine(target, relative);
                if (File.Exists(to))
                {
                    if (!SameContent(from, to))
                        kept.Add(relative);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(to));
                string temp = to + ".copying";
                File.Copy(from, temp, overwrite: true); // only ever our own leftover from a copy cut short
                if (!SameContent(from, temp))
                {
                    File.Delete(temp);
                    throw new IOException(Loc.F("Migration.CopyFailed", relative));
                }
                File.Move(temp, to);
            }

            if (kept.Count > 0)
                UnhandledErrors.LogQuietly("data migration", "Kept " + kept.Count + " file(s) already in " + target
                    + " instead of copying the older ones from " + source + " over them: "
                    + string.Join(", ", kept.Take(5)) + (kept.Count > 5 ? ", …" : "") + ".");
        }

        // Sizes for pictures and attachments (they can be large); the full contents for the small data files.
        private static bool SameContent(string a, string b)
        {
            if (new FileInfo(a).Length != new FileInfo(b).Length)
                return false;

            string extension = Path.GetExtension(a);
            bool small = extension.Equals(".json", StringComparison.OrdinalIgnoreCase)
                         || extension.Equals(".txt", StringComparison.OrdinalIgnoreCase);
            return !small || File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
        }
    }
}
