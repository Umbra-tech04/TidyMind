using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Windows;

namespace TidyMind
{
    // A backup is one ZIP of the whole data folder (AppPaths.DataFolder) plus a small manifest; restoring puts such a
    // ZIP back in its place. Logs and temporary files are left out: they aren't data.
    public static class DataBackup
    {
        public const string ManifestName = "tidymind-backup.json";
        private const int FormatVersion = 1;

        public static string AppVersion => typeof(DataBackup).Assembly.GetName().Version?.ToString(3) ?? "?";

        private static readonly string[] SkippedFiles = { "error.log", "error.old.log", "reminders.log", "reminders.old.log" };

        private class Manifest
        {
            public string App { get; set; } = "";
            public int Format { get; set; }
            public string AppVersion { get; set; } = "";
            public DateTime Created { get; set; }
            public int Files { get; set; }
        }

        // ---- Backup -------------------------------------------------------

        // Written to a temporary file next to the target and moved into place once complete, like exports: a failed
        // backup never leaves a half-written ZIP, or replaces one that was there. Throws if it couldn't be made.
        public static void Create(string zipPath)
        {
            string temp = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(zipPath)) ?? "",
                Path.GetFileNameWithoutExtension(zipPath) + ".tmp-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".zip");
            try
            {
                WriteZip(AppPaths.DataFolder, temp);
                File.Move(temp, zipPath, overwrite: true);
            }
            catch
            {
                try { File.Delete(temp); } catch (Exception) { /* best effort: nothing else to do with it */ }
                throw;
            }
        }

        private static void WriteZip(string folder, string zipPath)
        {
            List<string> files = Directory.Exists(folder)
                ? Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Where(f => IsData(folder, f)).ToList()
                : new List<string>();

            using (ZipArchive zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                foreach (string file in files)
                {
                    ZipArchiveEntry entry = zip.CreateEntry(Path.GetRelativePath(folder, file).Replace('\\', '/'));
                    entry.LastWriteTime = File.GetLastWriteTime(file);
                    // Read with sharing, so a file another part of TidyMind (or a reminder) is saving right now
                    // doesn't stop the backup; saves replace whole files, so what's read is one complete version.
                    using (FileStream source = new FileStream(file, FileMode.Open, FileAccess.Read,
                               FileShare.ReadWrite | FileShare.Delete))
                    using (Stream target = entry.Open())
                        source.CopyTo(target);
                }

                Manifest manifest = new Manifest
                {
                    App = "TidyMind",
                    Format = FormatVersion,
                    AppVersion = AppVersion,
                    Created = DateTime.Now,
                    Files = files.Count
                };
                using (StreamWriter writer = new StreamWriter(zip.CreateEntry(ManifestName).Open()))
                    writer.Write(JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            }
        }

        // Not logs, not temporary or half-copied files (AtomicFile's *.tmp, a migration's *.copying).
        private static bool IsData(string folder, string file)
        {
            string relative = Path.GetRelativePath(folder, file);
            string name = Path.GetFileName(file);
            return !SkippedFiles.Contains(relative, StringComparer.OrdinalIgnoreCase)
                && !name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                && !name.EndsWith(".copying", StringComparison.OrdinalIgnoreCase)
                && !relative.Split(Path.DirectorySeparatorChar).Any(part => part.EndsWith(".copying", StringComparison.OrdinalIgnoreCase));
        }

        // ---- Restore ------------------------------------------------------

        // Null if the file is a TidyMind backup this version can restore; otherwise why not.
        public static string? CheckBackup(string zipPath)
        {
            try
            {
                using (ZipArchive zip = ZipFile.OpenRead(zipPath))
                {
                    ZipArchiveEntry? entry = zip.GetEntry(ManifestName);
                    if (entry == null)
                        return "This file isn't a TidyMind backup (it has no " + ManifestName + ").";

                    Manifest? manifest;
                    using (Stream stream = entry.Open())
                        manifest = JsonSerializer.Deserialize<Manifest>(stream);
                    if (manifest == null || manifest.App != "TidyMind")
                        return "This file isn't a TidyMind backup.";
                    if (manifest.Format > FormatVersion)
                        return "This backup was made by a newer TidyMind (" + manifest.AppVersion + ") and can't be restored here.";
                    return null;
                }
            }
            catch (Exception e) when (e is InvalidDataException || e is IOException || e is UnauthorizedAccessException
                                      || e is JsonException)
            {
                return "The file can't be read as a backup: " + e.Message;
            }
        }

        // Replaces the data folder with the backup's contents. In order, so that any failure leaves the current data
        // exactly as it was:
        //   1. the backup is unpacked into a new folder beside the data folder (nothing outside it is ever written);
        //   2. that folder is checked: profiles.json and every memory's files must be readable, and every memory must
        //      have its Id, so no migration runs over the restored data on the next start (the "data was copied"
        //      marker is added if the backup lacks it). A backup that fails here leaves nothing behind;
        //   3. before the data folder is touched, a safety copy of it (a backup ZIP, so it can itself be restored);
        //   4. only then the folders are swapped, by renaming (and renamed back if the second step fails).
        // Throws (with a message for the user) if the restore couldn't be done; safetyCopy is set as soon as it exists.
        public static void Restore(string zipPath, out string? safetyCopy)
        {
            safetyCopy = null;
            string data = AppPaths.DataFolder;
            string parent = Path.GetDirectoryName(data) ?? throw new IOException("The data folder has no parent folder.");

            string unpacked = Path.Combine(parent, "TidyMind.restore-" + Guid.NewGuid().ToString("N"));
            try
            {
                Unpack(zipPath, unpacked);
                Validate(unpacked);
                KeepLogs(data, unpacked);

                safetyCopy = UniquePath(Path.Combine(parent, "TidyMind-before-restore-" + DateTime.Now.ToString("yyyy-MM-dd-HHmm")), ".zip");
                WriteZip(data, safetyCopy);
                using (ZipArchive check = ZipFile.OpenRead(safetyCopy))
                    if (check.GetEntry(ManifestName) == null)
                        throw new IOException("The safety copy of the current data couldn't be checked, so nothing was changed.");

                string replaced = Path.Combine(parent, "TidyMind.replaced-" + Guid.NewGuid().ToString("N"));
                if (Directory.Exists(data))
                    Directory.Move(data, replaced);
                try
                {
                    Directory.Move(unpacked, data);
                }
                catch
                {
                    if (Directory.Exists(replaced))
                        Directory.Move(replaced, data);
                    throw;
                }

                // The safety copy holds the previous data; the folder it came from isn't needed any more.
                try { Directory.Delete(replaced, recursive: true); } catch (Exception) { /* best effort: it's only a leftover */ }
            }
            finally
            {
                if (Directory.Exists(unpacked))
                    try { Directory.Delete(unpacked, recursive: true); } catch (Exception) { /* best effort */ }
            }
        }

        // Every entry must land inside `target` ("zip slip"): a name with ".." or a drive or a colon is refused.
        private static void Unpack(string zipPath, string target)
        {
            string root = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(root);

            using (ZipArchive zip = ZipFile.OpenRead(zipPath))
            {
                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    if (entry.FullName == ManifestName || entry.FullName.EndsWith("/") || entry.FullName.EndsWith("\\"))
                        continue;

                    string destination = Path.GetFullPath(Path.Combine(root, entry.FullName));
                    if (entry.FullName.Contains(':') || Path.IsPathRooted(entry.FullName)
                        || !destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("The backup contains a file that would land outside the data folder ("
                            + entry.FullName + "), so it wasn't restored.");

                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    entry.ExtractToFile(destination, overwrite: false);
                }
            }
        }

        private static void Validate(string folder)
        {
            string profilesFile = Path.Combine(folder, "profiles.json");
            if (!File.Exists(profilesFile))
                throw new InvalidDataException("The backup has no profiles.json, so it wasn't restored.");

            List<Profile> profiles = Read<List<Profile>>(folder, "profiles.json") ?? new List<Profile>();
            if (profiles.Any(p => p.Id == Guid.Empty))
                throw new InvalidDataException("The backup's profiles.json has memories without an Id (it's from an older "
                    + "TidyMind), so it wasn't restored.");

            foreach (Profile memory in profiles)
            {
                string memoryFolder = Path.Combine("Memories", memory.Id.ToString("N"));
                ReadIfThere<List<Project>>(folder, Path.Combine(memoryFolder, "projects.json"));
                ReadIfThere<List<Entity>>(folder, Path.Combine(memoryFolder, "entities.json"));
                ReadIfThere<List<MemoryTab>>(folder, Path.Combine(memoryFolder, "tabs.json"));
                ReadIfThere<List<TodoEntry>>(folder, Path.Combine(memoryFolder, "todos.json"));
            }

            // The one-time copy from the old version's folder is done for this data; never again over it.
            string marker = Path.Combine(folder, "legacy-data-copied.txt");
            if (!File.Exists(marker))
                File.WriteAllText(marker, "Restored from a backup on " + DateTime.Now.ToString("u") + Environment.NewLine);
        }

        // relative: the file's path inside the backup, which is also what the message names.
        private static T? Read<T>(string folder, string relative)
        {
            try
            {
                return JsonSerializer.Deserialize<T>(File.ReadAllText(Path.Combine(folder, relative)));
            }
            catch (JsonException e)
            {
                throw new InvalidDataException("The backup's " + relative + " can't be read (" + e.Message
                    + "), so it wasn't restored.");
            }
        }

        private static void ReadIfThere<T>(string folder, string relative)
        {
            if (File.Exists(Path.Combine(folder, relative)))
                Read<T>(folder, relative);
        }

        // The logs aren't in backups, but they explain what happened before: they come along.
        private static void KeepLogs(string from, string to)
        {
            foreach (string name in SkippedFiles)
            {
                string source = Path.Combine(from, name);
                string target = Path.Combine(to, name);
                if (File.Exists(source) && !File.Exists(target))
                    File.Copy(source, target);
            }
        }

        private static string UniquePath(string pathWithoutExtension, string extension)
        {
            string path = pathWithoutExtension + extension;
            for (int i = 2; File.Exists(path) || Directory.Exists(path); i++)
                path = pathWithoutExtension + "-" + i + extension;
            return path;
        }

        // ---- Restart ------------------------------------------------------

        // A new copy of TidyMind on the restored data, then this one closes without saving anything else. The new one
        // waits for this one to finish first (App: "--after-restore"), since only one copy may run.
        public static void Restart()
        {
            string? exe = Environment.ProcessPath;
            try
            {
                if (exe == null)
                    throw new IOException("TidyMind's own location couldn't be found.");
                using (Process.Start(new ProcessStartInfo(exe) { ArgumentList = { "--after-restore", Environment.ProcessId.ToString() } })) { }
            }
            catch (Exception e) when (e is IOException || e is System.ComponentModel.Win32Exception)
            {
                MessageBox.Show("TidyMind couldn't start again by itself: " + e.Message + "\n\nIt will close now; please "
                    + "open it again.", "Restore", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            Application.Current.Shutdown();
        }

        public static void OpenFolder()
        {
            Directory.CreateDirectory(AppPaths.DataFolder);
            using (Process.Start(new ProcessStartInfo(AppPaths.DataFolder) { UseShellExecute = true })) { }
        }
    }
}
