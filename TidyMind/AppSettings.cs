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
        public const string ChecklistHidden = "hidden";
        public const string ChecklistShown = "shown";

        private static string FilePath => AppPaths.Data("settings.json");

        // The guided tour: TourShown, TourCompleted or TourDismissed; null until it was first offered or started.
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Tour { get; set; }

        // The version that last ran ("1.4.0"), so "What's new" (WhatsNew) shows once after an update. Null until then.
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? LastVersion { get; set; }

        // Home's "Getting started" list (GettingStarted): ChecklistHidden after Hide, ChecklistShown once brought back from
        // ?; null shows it while something on it is left to do.
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Checklist { get; set; }

        // Home's "What can TidyMind do?" tiles, folded away with Hide (true) until Show brings them back.
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? HideDiscovery { get; set; }

        // The language the user picked (Loc: "en", "hu" or "sr-Latn"). Null until one is picked: the app then follows
        // the Windows display language. A local preference: a restored backup keeps the current one (DataBackup).
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Language { get; set; }

        // Settings > Background pictures: how much of a memory's background picture shows, in percent; null means
        // PageBackground.DefaultImageVisibility.
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? BackgroundImageVisibility { get; set; }

        // Settings > Appearance > Adaptive card tint (CardTint): false turns it off; null (the default) means on.
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? AdaptiveCardTint { get; set; }

        // Settings > Updates: false turns the automatic check off; null (the default) means on.
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? CheckForUpdates { get; set; }

        // When GitHub last answered a check (UTC), automatic or "Check now": shown in Settings > Updates, never a limit.
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DateTime? LastUpdateCheck { get; set; }

        // A version the user chose to skip ("1.2.0"): not offered automatically; a newer one is.
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? SkippedUpdate { get; set; }

        // Settings > General: what amounts are shown in (Currency), as typed; null: the Windows region's symbol.
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Currency { get; set; }

        // The item window's size when it was last closed.
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? ItemWindowWidth { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? ItemWindowHeight { get; set; }

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
