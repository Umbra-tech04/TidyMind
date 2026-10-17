using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TidyMind
{
    // Where updates come from, and the only addresses an update may be downloaded from. Releases are the installers
    // published in the public GitHub repository ReleaseOwner/ReleaseRepo (installer\publish-release.ps1 makes them).
    // Changing the repository or the asset names (TidyMind-Setup-<version>.exe and .exe.sha256) needs the same
    // change in publish-release.ps1, or installed copies stop finding updates.
    public sealed class UpdateEndpoints
    {
        public const string ReleaseOwner = "Umbra-tech04";
        public const string ReleaseRepo = "TidyMind-releases";

        // GitHub answers a release asset's download link with a redirect to its file storage: only these hosts.
        private static readonly string[] GitHubAssetHosts =
        {
            "objects.githubusercontent.com", "release-assets.githubusercontent.com", "github-releases.githubusercontent.com"
        };

        public static UpdateEndpoints Production { get; } = new UpdateEndpoints(
            new Uri("https://api.github.com/repos/" + ReleaseOwner + "/" + ReleaseRepo + "/releases/latest"),
            new Uri("https://github.com/" + ReleaseOwner + "/" + ReleaseRepo + "/releases/download/"),
            GitHubAssetHosts,
            allowHttp: false,
            Path.Combine(Path.GetTempPath(), "TidyMind-update"),
            checkTimeout: TimeSpan.FromSeconds(10),
            stallTimeout: TimeSpan.FromSeconds(30));

        // Only the test harness makes others (a local release server on http://127.0.0.1); see Updates.Endpoints.
        internal UpdateEndpoints(Uri latestRelease, Uri assetPrefix, IEnumerable<string> redirectHosts, bool allowHttp,
            string downloadFolder, TimeSpan checkTimeout, TimeSpan stallTimeout)
        {
            LatestRelease = latestRelease;
            AssetPrefix = assetPrefix;
            RedirectHosts = redirectHosts.ToArray();
            AllowHttp = allowHttp;
            DownloadFolder = downloadFolder;
            CheckTimeout = checkTimeout;
            StallTimeout = stallTimeout;
        }

        public Uri LatestRelease { get; }
        private Uri AssetPrefix { get; }
        private IReadOnlyList<string> RedirectHosts { get; }
        private bool AllowHttp { get; }

        // Installers are downloaded here (never into the data folder), and cleared out on the next start.
        public string DownloadFolder { get; }

        // The whole version check; a download is stopped when no data came for StallTimeout.
        public TimeSpan CheckTimeout { get; }
        public TimeSpan StallTimeout { get; }

        // A release asset's own link: in the releases repository, nowhere else.
        public bool IsAsset(Uri uri) =>
            SchemeAllowed(uri) && SameOrigin(uri, AssetPrefix)
            && uri.AbsolutePath.StartsWith(AssetPrefix.AbsolutePath, StringComparison.OrdinalIgnoreCase);

        // Where a download may be redirected: GitHub's asset storage (its standard port; the local test server has its
        // own), or another asset link.
        public bool IsRedirectTarget(Uri uri) =>
            IsAsset(uri)
            || (SchemeAllowed(uri) && uri.UserInfo.Length == 0 && (uri.IsDefaultPort || AllowHttp)
                && RedirectHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase));

        // The version check may only be redirected within the API host (GitHub does that for a renamed repository).
        public bool IsApiRedirectTarget(Uri uri) => SchemeAllowed(uri) && SameOrigin(uri, LatestRelease);

        private bool SchemeAllowed(Uri uri) =>
            uri.IsAbsoluteUri && (uri.Scheme == Uri.UriSchemeHttps || (AllowHttp && uri.Scheme == Uri.UriSchemeHttp));

        private static bool SameOrigin(Uri uri, Uri expected) =>
            uri.UserInfo.Length == 0 && uri.Scheme == expected.Scheme && uri.Port == expected.Port
            && string.Equals(uri.Host, expected.Host, StringComparison.OrdinalIgnoreCase);
    }
}
