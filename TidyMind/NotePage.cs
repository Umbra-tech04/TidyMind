using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TidyMind
{
    // One page of a project's notebook (Project.NotePages).
    public class NotePage
    {
        // Stored like Project.Notes: formatted XAML (RichTextHelper), or plain text.
        public string? Body { get; set; }

        // Fields this version doesn't know (e.g. from a newer TidyMind): kept so saving doesn't drop them.
        [JsonExtensionData]
        public Dictionary<string, JsonElement>? ExtensionData { get; set; }
    }
}
