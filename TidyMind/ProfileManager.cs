using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace TidyMind
{
    public class ProfileManager
    {
        private static readonly string ProfilesFile = AppPaths.Data("profiles.json");

        public static List<Profile> LoadProfiles()
        {
            if (!File.Exists(ProfilesFile))
            {
                return new List<Profile>();
            }

            string json = File.ReadAllText(ProfilesFile);
            List<Profile> profiles = JsonSerializer.Deserialize<List<Profile>>(json) ?? new List<Profile>();

            // Stable sort: files from before Order existed (all 0) keep their saved order.
            return profiles.OrderBy(p => p.Order).ToList();
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