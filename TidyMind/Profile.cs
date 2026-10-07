using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TidyMind
{
    public enum ProfileType
    {
        Project,
        Collection
    }

    public class Profile
    {
        // Names the memory's own folder (MemoryFiles), so the display name below never touches the file system.
        // Guid.Empty only in files from before memory folders existed, until MemoryLayoutMigration gives it one.
        public Guid Id { get; set; }

        public string Name { get; set; }
        public string Color { get; set; }
        public ProfileType Type { get; set; } = ProfileType.Project;
        public int Order { get; set; }

        // The memory page's own background (Edit Background, project and collection memories), the same on every
        // tab. Only the field matching the type is set. Memories saved before this existed load as None.
        public BackgroundFill BackgroundType { get; set; }
        public string BackgroundColor { get; set; }     // "#RRGGBB", when the type is Color
        public string BackgroundImagePath { get; set; } // a file name inside BackgroundImages/, when the type is Image

        public BackgroundSetting GetBackground() => new BackgroundSetting(BackgroundType, BackgroundColor, BackgroundImagePath);

        public void SetBackground(BackgroundSetting background)
        {
            BackgroundType = background.Type;
            BackgroundColor = background.Color;
            BackgroundImagePath = background.Image;
        }

        // Fields this version doesn't know (written by another TidyMind version): kept so saving doesn't drop them.
        [JsonExtensionData]
        public Dictionary<string, JsonElement> ExtensionData { get; set; }
    }
}
