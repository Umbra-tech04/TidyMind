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
    // saved with its Id, which happens only after every one of its files has been copied and checked; until then it
    // keeps no Id and the next start copies again, into the same folder (its Id is derived from its type and name).
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
                    CopyLegacyFiles(memory);
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

        private static void CopyLegacyFiles(Profile memory)
        {
            string name = memory.Name ?? "";
            bool collection = memory.Type == ProfileType.Collection;

            CopyAndVerify(LegacyPath(collection ? name + "_entities.json" : name + ".json"), MemoryFiles.Items(memory));
            CopyAndVerify(LegacyPath(collection ? name + "_entities_tabs.json" : name + "_tabs.json"), MemoryFiles.Tabs(memory));
            if (!collection)
                CopyAndVerify(LegacyPath(name + "_todos.json"), MemoryFiles.Todos(memory));
        }

        // Null if the name can't have been a file name: the old version couldn't have saved anything under it.
        private static string LegacyPath(string fileName)
        {
            return fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ? null : AppPaths.Data(fileName);
        }

        // Byte for byte, corrupt files included: an unreadable file stays unreadable (and untouched) rather than
        // being dropped. A target with no source is a leftover of an earlier attempt and goes.
        private static void CopyAndVerify(string source, string target)
        {
            if (source == null || !File.Exists(source))
            {
                if (File.Exists(target))
                    File.Delete(target);
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target));
            File.Copy(source, target, overwrite: true);
            if (!File.ReadAllBytes(source).AsSpan().SequenceEqual(File.ReadAllBytes(target)))
                throw new IOException(Path.GetFileName(source) + " didn't copy correctly.");
        }
    }
}
