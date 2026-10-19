using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TidyMind
{
    // One entry of an item's record list (Entity.Records, RecordDefinition): a vehicle's document, wheel set or part.
    // Its fields are stored text by field key (Values), like Entity.Details. What a record of that kind also has (a
    // file, pictures, specs, records under it) is left out of the file while empty.
    public class EntityRecord
    {
        public Guid Id { get; set; }

        public Dictionary<string, string> Values { get; set; } = new Dictionary<string, string>();

        // A file of its own in Attachments/ (AttachmentStore), e.g. a scanned insurance policy.
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Attachment? File { get; set; }

        // Its pictures (a part's photos), file names in EntityImages/ like an item's own; the first is the cover.
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string>? Pictures { get; set; }

        // Free name and value pairs ("Size": "M10x1.25x40", "Torque": "45 Nm"), in the user's order.
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<SpecLine>? Specs { get; set; }

        // The records under it (RecordDefinition.Nested: a part's parts, RecordTree).
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<EntityRecord>? Children { get; set; }

        // Fields this version doesn't know (written by another TidyMind version): kept so saving doesn't drop them.
        [JsonExtensionData]
        public Dictionary<string, JsonElement>? ExtensionData { get; set; }

        public string? Get(string key) => Values != null && Values.TryGetValue(key, out string? value) ? value : null;

        // Empty text removes the field, so a record holds only what was filled in.
        public void Set(string key, string? value)
        {
            Values ??= new Dictionary<string, string>();
            if (string.IsNullOrEmpty(value))
                Values.Remove(key);
            else
                Values[key] = value;
        }
    }

    // One of a record's specs: a name and its value, both as typed.
    public class SpecLine
    {
        public string Name { get; set; } = "";
        public string Value { get; set; } = "";

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? ExtensionData { get; set; }
    }
}
