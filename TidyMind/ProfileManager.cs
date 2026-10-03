using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace TidyMind
{
    public class ProfileManager
    {
        private const string ProfilesFile = "profiles.json";

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

        public static void SaveProfiles(List<Profile> profiles)
        {
            string json = JsonSerializer.Serialize(profiles);
            File.WriteAllText(ProfilesFile, json);
        }
    }
}