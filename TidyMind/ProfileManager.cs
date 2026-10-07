using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace TidyMind
{
    public class ProfileManager
    {
        private static readonly string ProfilesFile = AppPaths.Data("profiles.json");

        // No file yet means no memories yet. False (with the reason) if the file exists but can't be read; the
        // caller must then not save over it.
        public static bool TryLoadProfiles(out List<Profile> profiles, out string problem)
        {
            profiles = new List<Profile>();
            problem = null;
            if (!File.Exists(ProfilesFile))
                return true;

            try
            {
                profiles = JsonSerializer.Deserialize<List<Profile>>(File.ReadAllText(ProfilesFile)) ?? new List<Profile>();
            }
            catch (Exception e) when (e is JsonException || e is IOException || e is UnauthorizedAccessException)
            {
                profiles = null;
                problem = e.Message;
                return false;
            }

            // Stable sort: files from before Order existed (all 0) keep their saved order.
            profiles = profiles.OrderBy(p => p.Order).ToList();
            return true;
        }

        // Throws IOException if profiles.json couldn't be written (the file is then unchanged).
        public static void SaveProfiles(List<Profile> profiles)
        {
            if (!TrySaveProfiles(profiles))
                throw new IOException("profiles.json couldn't be written.");
        }

        // Written via a temporary file, so a crash mid-save can't truncate the list of memories. False if it
        // couldn't be written; the file is then unchanged.
        public static bool TrySaveProfiles(List<Profile> profiles)
        {
            return AtomicFile.TryWriteAllText(ProfilesFile, JsonSerializer.Serialize(profiles));
        }
    }
}
