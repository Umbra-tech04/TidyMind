using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TidyMind
{
    // Why an update check or download didn't work out. Each has its own plain-words text (UpdateTexts).
    public enum UpdateProblem
    {
        None,
        Offline,      // no connection, DNS failure, connection dropped
        Timeout,      // no answer in time
        NoRelease,    // the repository has no published release (404)
        RateLimited,  // GitHub refuses for now (403, 429)
        ServerError,  // any other HTTP error
        BadAnswer,    // not JSON, or not a release with both assets
        ForeignHost,  // a link or redirect to anywhere but the releases repository and GitHub's file storage
        SizeMismatch, // the file isn't the published size
        HashMismatch, // the file doesn't match the published SHA-256
        CantSave,     // the download folder couldn't be written
        Cancelled
    }

    // A failed step, with the English reason for updates.log.
    public sealed class UpdateFailure : Exception
    {
        public UpdateFailure(UpdateProblem problem, string reason) : base(reason) => Problem = problem;

        public UpdateProblem Problem { get; }
    }

    public sealed class UpdateAsset
    {
        public UpdateAsset(string name, Uri url, long size)
        {
            Name = name;
            Url = url;
            Size = size;
        }

        public string Name { get; }
        public Uri Url { get; }
        public long Size { get; }
    }

    // One published version, from GitHub's "latest release" answer: its installer and the installer's .sha256 file.
    public sealed class UpdateRelease
    {
        public const int MaxNotesLength = 4000;
        private static readonly Regex VersionTag = new Regex(@"^v?(\d{1,5})\.(\d{1,5})\.(\d{1,5})$");

        public UpdateRelease(Version version, string notes, UpdateAsset installer, UpdateAsset checksum)
        {
            Version = version;
            Notes = notes;
            Installer = installer;
            Checksum = checksum;
        }

        public Version Version { get; }

        // Plain text, never rendered: control characters removed, cut at MaxNotesLength.
        public string Notes { get; }
        public UpdateAsset Installer { get; }
        public UpdateAsset Checksum { get; }

        public static string InstallerName(Version version) => "TidyMind-Setup-" + version.ToString(3) + ".exe";

        // "v1.10.0" or "1.10.0". Anything else (1.2, 1.2.0-beta, 1.2.0.1) isn't a release version.
        public static bool TryParseVersion(string? tag, out Version version)
        {
            version = new Version(0, 0, 0);
            Match m = VersionTag.Match(tag?.Trim() ?? "");
            if (!m.Success)
                return false;
            version = new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
            return true;
        }

        // Major.Minor.Build, so 1.1.0.0 (the assembly's form) equals 1.1.0 (a tag's).
        public static Version Normalize(Version version) =>
            new Version(version.Major, version.Minor, Math.Max(version.Build, 0));

        // GitHub's release JSON. Null when there is no usable release: `ignored` true for a draft or pre-release (not a
        // problem, just nothing to offer), otherwise `problem` says what's wrong (English, for the log).
        public static UpdateRelease? Parse(string json, UpdateEndpoints endpoints, out string? problem, out bool ignored)
        {
            problem = null;
            ignored = false;
            try
            {
                using JsonDocument document = JsonDocument.Parse(json);
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    return Fail("the answer isn't a JSON object", out problem);

                string? tag = ReadString(root, "tag_name");
                if (ReadBool(root, "draft") || ReadBool(root, "prerelease"))
                {
                    ignored = true;
                    problem = "release " + Shorten(tag) + " is a draft or pre-release; ignored";
                    return null;
                }
                if (!TryParseVersion(tag, out Version version))
                {
                    // A tag such as v1.2.0-beta is a pre-release by its name.
                    ignored = tag != null && Regex.IsMatch(tag, @"^v?\d+\.\d+\.\d+-");
                    problem = "tag_name '" + Shorten(tag) + "' isn't a version (v1.2.3)" + (ignored ? "; ignored" : "");
                    return null;
                }

                string installerName = InstallerName(version);
                UpdateAsset? installer = null, checksum = null;
                if (root.TryGetProperty("assets", out JsonElement assets) && assets.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement asset in assets.EnumerateArray())
                    {
                        string? name = ReadString(asset, "name");
                        if (name != installerName && name != installerName + ".sha256")
                            continue;
                        string? url = ReadString(asset, "browser_download_url");
                        if (url == null || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || !endpoints.IsAsset(uri))
                            return Fail("asset " + name + " links outside the releases repository (" + Shorten(url) + ")", out problem);
                        if (!asset.TryGetProperty("size", out JsonElement sizeElement) || !sizeElement.TryGetInt64(out long size) || size <= 0)
                            return Fail("asset " + name + " has no size", out problem);
                        if (name == installerName)
                            installer = new UpdateAsset(name, uri, size);
                        else
                            checksum = new UpdateAsset(name, uri, size);
                    }
                }
                if (installer == null || checksum == null)
                    return Fail("release " + Shorten(tag) + " lacks " + (installer == null ? installerName : installerName + ".sha256"), out problem);

                return new UpdateRelease(version, CleanNotes(ReadString(root, "body")), installer, checksum);
            }
            catch (JsonException e)
            {
                return Fail("the answer isn't valid JSON (" + e.Message + ")", out problem);
            }
        }

        // Line breaks kept, every other control character (and anything that could steer the text's direction) dropped.
        public static string CleanNotes(string? body)
        {
            if (string.IsNullOrWhiteSpace(body))
                return "";
            string text = body.Replace("\r\n", "\n").Replace('\r', '\n');
            StringBuilder clean = new StringBuilder(Math.Min(text.Length, MaxNotesLength + 1));
            foreach (char c in text)
            {
                if (c == '\n' || c == '\t' || (!char.IsControl(c) && !IsFormatting(c)))
                    clean.Append(c);
                if (clean.Length > MaxNotesLength)
                    break;
            }
            string result = clean.ToString().Trim();
            return result.Length > MaxNotesLength ? result.Substring(0, MaxNotesLength).TrimEnd() + "…" : result;
        }

        private static bool IsFormatting(char c) =>
            char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format;

        private static UpdateRelease? Fail(string reason, out string? problem)
        {
            problem = reason;
            return null;
        }

        private static string? ReadString(JsonElement element, string name) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        private static bool ReadBool(JsonElement element, string name) =>
            element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;

        private static string Shorten(string? text) =>
            text == null ? "(none)" : new string(text.Take(80).Where(c => !char.IsControl(c)).ToArray());
    }
}
