using System.Collections.Generic;
using System.IO;
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
            return JsonSerializer.Deserialize<List<Profile>>(json);
        }

        public static void SaveProfiles(List<Profile> profiles)
        {
            string json = JsonSerializer.Serialize(profiles);
            File.WriteAllText(ProfilesFile, json);
        }
    }
}