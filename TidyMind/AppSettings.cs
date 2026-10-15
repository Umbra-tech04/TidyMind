using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TidyMind
{
    // Small app-wide preferences (settings.json in the data folder): read when needed, written only when one of them
    // changes. Every field is optional and left out of the file while unset.
    public class AppSettings
    {
        public const string TourShown = "shown";
        public const string TourCompleted = "completed";
        public const string TourDismissed = "dismissed";

        private static string FilePath => AppPaths.Data("settings.json");

        // The guided tour: TourShown, TourCompleted or TourDismissed; null until it was first offered or started.
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Tour { get; set; }

        // Home's "What can TidyMind do?" row, hidden for good with its Hide link.
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? HideDiscovery { get; set; }

        // Fields this version doesn't know (written by another TidyMind version): kept so saving doesn't drop them.
        [JsonExtensionData]
        public Dictionary<string, JsonElement>? ExtensionData { get; set; }

        // No file yet: the defaults. An unreadable file also reads as the defaults, with `readable` false so it's never
        // saved over.
        public static AppSettings Load(out bool readable)
        {
            readable = true;
            if (!File.Exists(FilePath))
                return new AppSettings();
            try
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
            }
            catch (Exception e) when (e is JsonException || e is IOException || e is UnauthorizedAccessException)
            {
                readable = false;
                return new AppSettings();
            }
        }

        public static AppSettings Load() => Load(out _);

        // Applies the change to the stored settings and writes them. False if the file couldn't be read or written; it
        // is then unchanged (a preference just isn't remembered, nothing to warn about).
        public static bool Update(Action<AppSettings> change)
        {
            AppSettings settings = Load(out bool readable);
            if (!readable)
                return false;
            change(settings);
            return AtomicFile.TryWriteAllText(FilePath, JsonSerializer.Serialize(settings));
        }
    }
}
