using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace TidyMind
{
    // Moves memories from the old layout (files named after the memory, next to profiles.json) into their own folders
    // (MemoryFiles). Copies only: the old files stay where they are. A memory switches over when profiles.json is
    // saved with its Id; until then it keeps no Id, and the next start tries again (its Id is derived from its type
    // and name, so it always lands on the same folder).
    //
    // Never over existing data. A memory without an Id usually means "not moved yet", but it can also come from a
    // profiles.json restored from an older backup or edited by hand, while the memory's folder already holds all the
    // work done since; the old files next to profiles.json are only a snapshot from the day of the upgrade. So a
    // memory is copied into a staging folder that is renamed into place in one step: a folder that exists is always
    // complete, and is then kept as it is. The memory just gets its Id back, and error.log notes it.
    public static class MemoryLayoutMigration
    {
        // False (with the reason) if the copy couldn't be completed; nothing was switched over then.
        public static bool TryRun(List<Profile> profiles, out string problem)
        {
            problem = null;
            List<Profile> pending = profiles.Where(p => p.Id == Guid.Empty).ToList();
            if (pending.Count == 0)
                return true;

            HashSet<Guid> taken = new HashSet<Guid>(profiles.Where(p => p.Id != Guid.Empty).Select(p => p.Id));
            try
            {
                foreach (Profile memory in pending)
                {
                    Guid id = StableId(memory);
                    memory.Id = taken.Add(id) ? id : NewId(taken);

                    string folder = MemoryFiles.Folder(memory);
                    if (Directory.Exists(folder))
                        UnhandledErrors.LogQuietly("memory migration", "'" + memory.Name + "' had no Id in profiles.json, "
                            + "but its folder " + folder + " already exists: kept as it is, nothing was copied over it.");
                    else
                        CopyLegacyFiles(memory, folder);
                }

                if (!ProfileManager.TrySaveProfiles(profiles))
                    throw new IOException("profiles.json couldn't be written.");
                return true;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                foreach (Profile memory in pending)
                    memory.Id = Guid.Empty;
                problem = e.Message;
                return false;
            }
        }

        // The same on every attempt, so a copy cut short is redone into the same folder instead of leaving another.
        // Memories differing only in case (which used to share their files) get separate folders, each with a copy.
        private static Guid StableId(Profile memory)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes("TidyMind memory|" + memory.Type + "|" + memory.Name));
            return new Guid(hash.AsSpan(0, 16));
        }

        // Only for two memories with exactly the same type and name (possible only in a hand-edited profiles.json).
        private static Guid NewId(HashSet<Guid> taken)
        {
            Guid id;
            do
                id = Guid.NewGuid();
            while (!taken.Add(id));
            return id;
        }

        // Filled in a staging folder, then renamed to the memory's folder in one step (the rename fails rather than
        // replace a folder that appeared meanwhile). A staging folder left by a copy cut short is cleared and refilled.
        private static void CopyLegacyFiles(Profile memory, string folder)
        {
            string staging = folder + ".copying";
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
            Directory.CreateDirectory(staging);

            string name = memory.Name ?? "";
            bool collection = memory.Type == ProfileType.Collection;

            CopyAndVerify(LegacyPath(collection ? name + "_entities.json" : name + ".json"), staging, MemoryFiles.Items(memory));
            CopyAndVerify(LegacyPath(collection ? name + "_entities_tabs.json" : name + "_tabs.json"), staging, MemoryFiles.Tabs(memory));
            if (!collection)
                CopyAndVerify(LegacyPath(name + "_todos.json"), staging, MemoryFiles.Todos(memory));

            Directory.Move(staging, folder);
        }

        // Null if the name can't have been a file name: the old version couldn't have saved anything under it.
        private static string LegacyPath(string fileName)
        {
            return fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ? null : AppPaths.Data(fileName);
        }

        // Byte for byte, corrupt files included: an unreadable file stays unreadable (and untouched) rather than
        // being dropped. target: the file's final path; it's copied under that name into the staging folder.
        private static void CopyAndVerify(string source, string staging, string target)
        {
            if (source == null || !File.Exists(source))
                return;

            string copy = Path.Combine(staging, Path.GetFileName(target));
            File.Copy(source, copy);
            if (!File.ReadAllBytes(source).AsSpan().SequenceEqual(File.ReadAllBytes(copy)))
                throw new IOException(Path.GetFileName(source) + " didn't copy correctly.");
        }
    }
}
