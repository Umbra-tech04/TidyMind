using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Win32.TaskScheduler;
using Task = System.Threading.Tasks.Task;
using TidyMind;

// Harness update <data> <shotsFolder>: the in-app update against a local fake release server (never GitHub). Version
// rules, GitHub's JSON, every network failure, the host allowlist, size and SHA-256 checks, the once-a-day limit, the
// switch, Skip and Later, the dialog in three languages (progress, cancel, error), the hand-over to a stub installer,
// the report after an update, and (run as Harness.exe) the repair of a reminder task whose run failed.
static partial class Program
{
    const string ReleasesPath = "/Umbra-tech04/TidyMind-releases";

    static async Task UpdateScenarios(MainWindow main)
    {
        await Wait(1200);
        GuidedTour.Current?.Stop(TourEnd.Skipped);
        await Wait(300);
        StartBoxWatcher();

        string downloads = AppPaths.DataFolder.TrimEnd('\\') + "-downloads";
        if (Directory.Exists(downloads))
            Directory.Delete(downloads, true);

        VersionChecks();
        ParseChecks();
        AllowlistChecks();
        ChecksumChecks();

        using FakeServer server = new FakeServer();
        Updates.Endpoints = TestEndpoints(server, downloads, checkSeconds: 2, stallSeconds: 2);
        await CheckScenarios(server, downloads);
        await DownloadScenarios(server, downloads);
        await AutomaticScenarios(main, server);
        await DialogScenarios(main, server, downloads);
        await ApplyScenarios(main, server, downloads);
        await AfterUpdateScenarios(downloads);
        await TaskRepairScenario();

        Console.WriteLine("message boxes seen: " + boxes.Count);
        foreach (string box in boxes)
            Console.WriteLine("  " + box);
        Check(boxes.Count == 0, "no message box at any point (the automatic check is silent)");
        Console.WriteLine("updates.log:");
        Console.WriteLine(File.Exists(Updates.LogPath) ? File.ReadAllText(Updates.LogPath) : "(none)");
    }

    // ---- Pure rules -------------------------------------------------------------

    static void VersionChecks()
    {
        Console.WriteLine("Versions");
        Check(Updates.CurrentVersion == new Version(1, 1, 0) && DataBackup.AppVersion == "1.1.0",
            "the running version comes from the csproj's <Version> (1.1.0): " + Updates.CurrentVersion);
        Check(UpdateRelease.TryParseVersion("v1.10.0", out Version v110) && UpdateRelease.TryParseVersion("1.9.0", out Version v19)
            && v110 > v19, "1.10.0 > 1.9.0 (compared as numbers, not text)");
        Check(UpdateRelease.TryParseVersion("v1.1.0", out Version same) && same == Updates.CurrentVersion, "v1.1.0 equals the running 1.1.0");
        Check(UpdateRelease.Normalize(new Version(1, 1, 0, 0)) == new Version(1, 1, 0), "1.1.0.0 (assembly form) equals 1.1.0");
        Check(new[] { "1.2", "v1.2.0-beta", "1.2.0.1", "", null, "vv1.2.0", "1.2.x", "latest" }.All(t => !UpdateRelease.TryParseVersion(t, out _)),
            "not versions: 1.2, v1.2.0-beta, 1.2.0.1, empty, missing, vv1.2.0, 1.2.x, latest");
        Check(UpdateRelease.InstallerName(new Version(1, 2, 3)) == "TidyMind-Setup-1.2.3.exe", "asset name TidyMind-Setup-<version>.exe");

        AppSettings s = new AppSettings();
        DateTime now = DateTime.UtcNow;
        Check(Updates.ShouldCheckAutomatically(s, now), "never checked: check");
        s.LastUpdateCheck = now.AddHours(-1);
        Check(!Updates.ShouldCheckAutomatically(s, now), "checked an hour ago: not again");
        s.LastUpdateCheck = now.AddHours(-23.9);
        Check(!Updates.ShouldCheckAutomatically(s, now), "checked 23.9 hours ago: not again");
        s.LastUpdateCheck = now.AddHours(-24.1);
        Check(Updates.ShouldCheckAutomatically(s, now), "checked 24.1 hours ago: check");
        s.LastUpdateCheck = now.AddDays(3);
        Check(Updates.ShouldCheckAutomatically(s, now), "a last check in the future (clock set back): check");
        s.LastUpdateCheck = null;
        s.CheckForUpdates = false;
        Check(!Updates.ShouldCheckAutomatically(s, now), "turned off: never");
    }

    static void ParseChecks()
    {
        Console.WriteLine("GitHub's release JSON");
        UpdateEndpoints production = UpdateEndpoints.Production;
        string good = GitHubJson("v1.2.0", "https://github.com" + ReleasesPath + "/releases/download/v1.2.0/", 1000, "Line one\r\n<b>bold</b> & *stars*");
        UpdateRelease r = UpdateRelease.Parse(good, production, out string problem, out bool ignored);
        Check(r != null && r.Version == new Version(1, 2, 0) && r.Installer.Name == "TidyMind-Setup-1.2.0.exe" && r.Installer.Size == 1000
            && r.Checksum.Name == "TidyMind-Setup-1.2.0.exe.sha256" && !ignored, "a release: version, installer, checksum, size (" + problem + ")");
        Check(r?.Notes == "Line one\n<b>bold</b> & *stars*", "notes kept as plain text, nothing rendered: " + r?.Notes);

        UpdateRelease.Parse(GitHubJson("v1.2.0", null, 1000, draft: true), production, out problem, out ignored);
        Check(ignored, "a draft is ignored (" + problem + ")");
        UpdateRelease.Parse(GitHubJson("v1.2.0", null, 1000, prerelease: true), production, out problem, out ignored);
        Check(ignored, "a pre-release is ignored (" + problem + ")");
        UpdateRelease.Parse(GitHubJson("v1.2.0-beta", null, 1000), production, out problem, out ignored);
        Check(ignored, "a -beta tag is ignored (" + problem + ")");

        foreach ((string what, string json) in new[]
        {
            ("garbage", "<html>rate limited</html>"),
            ("not an object", "[1,2,3]"),
            ("truncated", good.Substring(0, good.Length / 2)),
            ("no assets", "{\"tag_name\":\"v1.2.0\",\"body\":\"x\"}"),
            ("no .sha256", GitHubJson("v1.2.0", null, 1000, checksum: false)),
            ("no installer", GitHubJson("v1.2.0", null, 1000, installer: false)),
            ("asset on another host", GitHubJson("v1.2.0", "https://evil.example.com" + ReleasesPath + "/releases/download/v1.2.0/", 1000)),
            ("asset in another repository", GitHubJson("v1.2.0", "https://github.com/someone/TidyMind-releases/releases/download/v1.2.0/", 1000)),
            ("asset over http", GitHubJson("v1.2.0", "http://github.com" + ReleasesPath + "/releases/download/v1.2.0/", 1000)),
            ("size 0", GitHubJson("v1.2.0", null, 0)),
            ("tag 1.2", GitHubJson("1.2", null, 1000))
        })
        {
            UpdateRelease bad = UpdateRelease.Parse(json, production, out problem, out ignored);
            Check(bad == null && !ignored && problem != null, what + ": rejected (" + problem + ")");
        }

        string control = "a\u0007b\u202Ec\u200Bd\te\r\nf" + new string('x', 9000);
        string clean = UpdateRelease.CleanNotes(control);
        Check(clean.StartsWith("abcd\te\nf") && clean.Length <= UpdateRelease.MaxNotesLength + 1 && clean.EndsWith("…"),
            "notes: control and direction characters removed, cut at " + UpdateRelease.MaxNotesLength + " with an ellipsis (" + clean.Length + ")");
    }

    static void AllowlistChecks()
    {
        Console.WriteLine("Allowed addresses (production)");
        UpdateEndpoints p = UpdateEndpoints.Production;
        Check(p.LatestRelease.AbsoluteUri == "https://api.github.com/repos/Umbra-tech04/TidyMind-releases/releases/latest",
            "version check: " + p.LatestRelease);
        Check(p.DownloadFolder == Path.Combine(Path.GetTempPath(), "TidyMind-update") && !p.DownloadFolder.StartsWith(AppPaths.DataFolder),
            "downloads go to %TEMP%\\TidyMind-update, not the data folder");
        foreach ((string url, bool asset, bool redirect) in new[]
        {
            ("https://github.com/Umbra-tech04/TidyMind-releases/releases/download/v1.2.0/TidyMind-Setup-1.2.0.exe", true, true),
            ("https://GitHub.com/umbra-tech04/tidymind-releases/releases/download/v1.2.0/x.exe", true, true),
            ("http://github.com/Umbra-tech04/TidyMind-releases/releases/download/v1.2.0/x.exe", false, false),
            ("https://github.com:8443/Umbra-tech04/TidyMind-releases/releases/download/v1.2.0/x.exe", false, false),
            ("https://user:pw@github.com/Umbra-tech04/TidyMind-releases/releases/download/v1.2.0/x.exe", false, false),
            ("https://github.com/Umbra-tech04/TidyMind-releases/releases/download/../../../Other/x.exe", false, false),
            ("https://github.com/Umbra-tech04/TidyMind/releases/download/v1.2.0/x.exe", false, false),
            ("https://github.com.evil.example/Umbra-tech04/TidyMind-releases/releases/download/v1/x.exe", false, false),
            ("https://objects.githubusercontent.com/github-production-release-asset/1/2?sig=x", false, true),
            ("https://release-assets.githubusercontent.com/github-production-release-asset/1/2?sig=x", false, true),
            ("http://objects.githubusercontent.com/x", false, false),
            ("https://objects.githubusercontent.com:444/x", false, false),
            ("https://evil.example/x", false, false),
            ("https://githubusercontent.com.evil.example/x", false, false),
            ("file:///C:/Windows/notepad.exe", false, false)
        })
        {
            Uri uri = new Uri(url);
            Check(p.IsAsset(uri) == asset && p.IsRedirectTarget(uri) == redirect,
                url + ": asset link " + (asset ? "yes" : "no") + ", redirect target " + (redirect ? "yes" : "no"));
        }
        Check(p.IsApiRedirectTarget(new Uri("https://api.github.com/repositories/123/releases/latest"))
            && !p.IsApiRedirectTarget(new Uri("https://evil.example/repos/x")) && !p.IsApiRedirectTarget(new Uri("http://api.github.com/x")),
            "the version check is only redirected within https://api.github.com");
    }

    static void ChecksumChecks()
    {
        Console.WriteLine(".sha256 files");
        string hash = new string('a', 32) + new string('B', 32);
        Check(UpdateClient.ParseChecksum(hash, "TidyMind-Setup-1.2.0.exe") == hash, "the hash alone");
        Check(UpdateClient.ParseChecksum(hash + "  TidyMind-Setup-1.2.0.exe\n", "TidyMind-Setup-1.2.0.exe") == hash, "sha256sum's form");
        Check(UpdateClient.ParseChecksum("\uFEFF" + hash + " *TidyMind-Setup-1.2.0.exe\r\n", "TidyMind-Setup-1.2.0.exe") == hash, "binary mark, BOM, CRLF");
        foreach ((string what, string text) in new[] { ("another file's", hash + "  TidyMind-Setup-1.1.0.exe"), ("too short", hash.Substring(1)),
                     ("not hex", hash.Replace('a', 'g')), ("empty", "") })
        {
            bool rejected = false;
            try { UpdateClient.ParseChecksum(text, "TidyMind-Setup-1.2.0.exe"); }
            catch (UpdateFailure f) { rejected = f.Problem == UpdateProblem.BadAnswer; }
            Check(rejected, ".sha256 " + what + ": rejected");
        }
    }

    // ---- The version check over HTTP -----------------------------------------------

    static async Task CheckScenarios(FakeServer server, string downloads)
    {
        Console.WriteLine("Version check against the local server");
        async Task<UpdateCheckResult> Run(Func<string, Reply> route, Version current = null)
        {
            server.Route = route;
            return await new UpdateClient(Updates.Endpoints, current ?? Updates.CurrentVersion).CheckAsync();
        }
        Func<string, Reply> Latest(string json) => path => path == "/repos" + ReleasesPath + "/releases/latest" ? Reply.Json(json) : Reply.Status(404);

        server.Clear();
        UpdateCheckResult r = await Run(Latest(ServerJson(server, "v9.9.9", 10)));
        Check(r.Newer?.Version == new Version(9, 9, 9) && r.Answered && r.Problem == UpdateProblem.None, "9.9.9 is offered: " + r.Reason);
        string request = server.Log.FirstOrDefault() ?? "";
        Console.WriteLine("  request as the server saw it: " + request);
        Check(request.Contains("User-Agent: TidyMind/1.1.0") && !request.Contains("Cookie") && !request.Contains("Authorization"),
            "only \"TidyMind/1.1.0\" as user agent; no cookies, no sign-in");

        r = await Run(Latest(ServerJson(server, "v1.1.0", 10)));
        Check(r.Newer == null && r.Problem == UpdateProblem.None && r.Answered, "the same version: up to date (" + r.Reason + ")");
        r = await Run(Latest(ServerJson(server, "v1.0.5", 10)));
        Check(r.Newer == null && r.Problem == UpdateProblem.None, "an older version: up to date (" + r.Reason + ")");
        r = await Run(Latest(ServerJson(server, "v1.10.0", 10)), new Version(1, 9, 0));
        Check(r.Newer?.Version == new Version(1, 10, 0), "running 1.9.0: 1.10.0 is offered");
        r = await Run(Latest(ServerJson(server, "v1.9.0", 10)), new Version(1, 10, 0));
        Check(r.Newer == null, "running 1.10.0: 1.9.0 isn't");
        r = await Run(Latest(ServerJson(server, "v9.9.9", 10, prerelease: true)));
        Check(r.Newer == null && r.Problem == UpdateProblem.None, "a pre-release isn't offered (" + r.Reason + ")");
        r = await Run(Latest(ServerJson(server, "v9.9.9", 10, draft: true)));
        Check(r.Newer == null && r.Problem == UpdateProblem.None, "a draft isn't offered (" + r.Reason + ")");

        foreach ((string what, Func<string, Reply> route, UpdateProblem expected, bool answered) in new (string, Func<string, Reply>, UpdateProblem, bool)[]
        {
            ("404 (no release yet)", p => Reply.Status(404), UpdateProblem.NoRelease, true),
            ("403 (rate limit)", p => Reply.Status(403, "{\"message\":\"API rate limit exceeded\"}"), UpdateProblem.RateLimited, true),
            ("429", p => Reply.Status(429), UpdateProblem.RateLimited, true),
            ("500", p => Reply.Status(500), UpdateProblem.ServerError, true),
            ("garbage JSON", p => Reply.Json("{\"tag_name\": \"v9.9.9\", \"assets\": [ {"), UpdateProblem.BadAnswer, true),
            ("an HTML page", p => Reply.Json("<html><body>Hello</body></html>"), UpdateProblem.BadAnswer, true),
            ("missing assets", Latest("{\"tag_name\":\"v9.9.9\",\"draft\":false,\"prerelease\":false,\"assets\":[]}"), UpdateProblem.BadAnswer, true),
            ("redirect to another host", p => Reply.Redirect("http://evil.invalid/latest"), UpdateProblem.ForeignHost, true),
            ("too big an answer", p => Reply.Json("{\"body\":\"" + new string('x', 2 * 1024 * 1024) + "\"}"), UpdateProblem.BadAnswer, true),
            ("no answer in time", p => Reply.Json(ServerJson(server, "v9.9.9", 10)).Delayed(4000), UpdateProblem.Timeout, false),
            ("connection cut", p => Reply.Cut(), UpdateProblem.Offline, false)
        })
        {
            Stopwatch took = Stopwatch.StartNew();
            r = await Run(route);
            Check(r.Problem == expected && r.Newer == null && r.Answered == answered,
                what + ": " + r.Problem + (answered ? ", counts as checked" : ", not counted") + " (" + r.Reason + ", " + took.ElapsedMilliseconds + " ms)");
        }

        // Windows takes about 2 s to report a refused connection on loopback.
        UpdateEndpoints offline = TestEndpoints(null, downloads, 6, 6);
        Stopwatch t = Stopwatch.StartNew();
        r = await new UpdateClient(offline, Updates.CurrentVersion).CheckAsync();
        Check(r.Problem == UpdateProblem.Offline && !r.Answered, "nothing listening (offline): Offline, quietly (" + r.Reason + ", " + t.ElapsedMilliseconds + " ms)");
        UpdateEndpoints dns = new UpdateEndpoints(new Uri("http://no-such-host.invalid/latest"), new Uri("http://no-such-host.invalid" + ReleasesPath + "/releases/download/"),
            new string[0], true, downloads, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        r = await new UpdateClient(dns, Updates.CurrentVersion).CheckAsync();
        Check(r.Problem == UpdateProblem.Offline && !r.Answered, "a name that doesn't resolve (DNS): Offline (" + r.Reason + ")");
    }

    // ---- Downloads --------------------------------------------------------------

    static async Task DownloadScenarios(FakeServer server, string downloads)
    {
        Console.WriteLine("Downloads");
        byte[] payload = RandomBytes(3 * 1024 * 1024 + 123);
        UpdateRelease release = ServeRelease(server, "9.9.9", payload);
        List<DownloadProgress> seen = new List<DownloadProgress>();
        bool verifying = false;
        using (VerifiedInstaller v = await new UpdateClient(Updates.Endpoints, Updates.CurrentVersion).DownloadAsync(release,
                   new SyncProgress(p => { lock (seen) seen.Add(p); }), () => verifying = true, CancellationToken.None))
        {
            Check(File.Exists(v.Path) && Path.GetDirectoryName(v.Path) == downloads && Path.GetFileName(v.Path) == "TidyMind-Setup-9.9.9.exe"
                && !File.Exists(v.Path + ".part"), "downloaded through the redirect to the file storage host, renamed from .part when complete");
            Check(SHA256.HashData(File.ReadAllBytes(v.Path)).SequenceEqual(SHA256.HashData(payload)) && verifying,
                "verified (size and SHA-256), the Verifying step reported");
            Check(seen.Count > 10 && seen.Zip(seen.Skip(1)).All(x => x.First.Received <= x.Second.Received)
                && seen.Last().Received == payload.Length && seen.All(p => p.Total == payload.Length),
                "progress: " + seen.Count + " reports, never going back, ending at " + payload.Length + " bytes");
            bool locked = false;
            try { File.WriteAllBytes(v.Path, new byte[1]); } catch (IOException) { locked = true; }
            bool undeletable = false;
            try { File.Delete(v.Path); } catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { undeletable = true; }
            Check(locked && undeletable && File.Exists(v.Path), "the checked file can't be changed or deleted while TidyMind holds it");
        }
        Check(server.Log.Any(l => l.Contains("/cdn/")) && server.Log.Any(l => l.Contains(".sha256")), "the .sha256 file and the redirect were used");
        Clean(downloads);

        async Task<UpdateFailure> Fails(UpdateRelease rel, int cancelAfterMs = -1)
        {
            using CancellationTokenSource cancel = new CancellationTokenSource();
            if (cancelAfterMs >= 0)
                cancel.CancelAfter(cancelAfterMs);
            try
            {
                using (await new UpdateClient(Updates.Endpoints, Updates.CurrentVersion).DownloadAsync(rel, null, null, cancel.Token)) { }
                return null;
            }
            catch (UpdateFailure f)
            {
                return f;
            }
        }
        void Expect(UpdateFailure f, UpdateProblem problem, string what)
        {
            string[] left = Directory.Exists(downloads) ? Directory.GetFiles(downloads) : new string[0];
            Check(f?.Problem == problem && left.Length == 0, what + ": " + (f?.Problem.ToString() ?? "no failure") + ", nothing left behind ("
                + (f?.Message ?? "") + (left.Length > 0 ? "; left: " + string.Join(", ", left.Select(Path.GetFileName)) : "") + ")");
        }

        release = ServeRelease(server, "9.9.9", payload, checksumOf: RandomBytes(100));
        Expect(await Fails(release), UpdateProblem.HashMismatch, "a different SHA-256: deleted, not run");

        release = ServeRelease(server, "9.9.9", payload, declaredSize: payload.Length + 10);
        Expect(await Fails(release), UpdateProblem.SizeMismatch, "the release says 10 bytes more than the server sends");

        release = ServeRelease(server, "9.9.9", payload, declaredSize: payload.Length + 10, omitLength: true);
        Expect(await Fails(release), UpdateProblem.SizeMismatch, "no Content-Length, fewer bytes than the release says");

        release = ServeRelease(server, "9.9.9", payload, declaredSize: payload.Length - 10, omitLength: true);
        Expect(await Fails(release), UpdateProblem.SizeMismatch, "no Content-Length, more bytes than the release says");

        release = ServeRelease(server, "9.9.9", payload, cdn: "http://evil.invalid/cdn/x");
        Expect(await Fails(release), UpdateProblem.ForeignHost, "redirected to another host");
        Check(!server.Log.Any(l => l.Contains("evil")), "the other host was never asked");

        release = ServeRelease(server, "9.9.9", payload, cutAfter: payload.Length / 2);
        Expect(await Fails(release), UpdateProblem.Offline, "the connection drops half-way");

        release = ServeRelease(server, "9.9.9", payload, bytesPerSecond: 512 * 1024);
        Stopwatch took = Stopwatch.StartNew();
        Expect(await Fails(release, cancelAfterMs: 700), UpdateProblem.Cancelled, "cancelled after 0.7 s");
        Check(took.ElapsedMilliseconds < 3000, "cancel stops it at once (" + took.ElapsedMilliseconds + " ms)");

        release = ServeRelease(server, "9.9.9", payload, stallAfterHeadersMs: 4000);
        Expect(await Fails(release), UpdateProblem.Timeout, "no data for longer than the stall limit");

        release = ServeRelease(server, "9.9.9", payload, cdn: server.Base + ReleasesPath + "/releases/download/v9.9.9/TidyMind-Setup-9.9.9.exe");
        Expect(await Fails(release), UpdateProblem.BadAnswer, "redirects that never end (more than 5)");

        release = ServeRelease(server, "9.9.9", payload, installerStatus: 404);
        Expect(await Fails(release), UpdateProblem.ServerError, "the installer link answers 404");

        Directory.Delete(downloads, true);
        File.WriteAllText(downloads, "a file where the download folder should be");
        UpdateFailure cantSave = await Fails(ServeRelease(server, "9.9.9", payload));
        Check(cantSave?.Problem == UpdateProblem.CantSave, "the download folder can't be made: CantSave (" + cantSave?.Message + ")");
        File.Delete(downloads);
    }

    // ---- Automatic check, switch, once a day, Skip and Later ------------------------

    static async Task AutomaticScenarios(MainWindow main, FakeServer server)
    {
        Console.WriteLine("Automatic check");
        main.Topmost = true;
        await Foreground(main);
        await Wait(500);
        string api = "/repos" + ReleasesPath + "/releases/latest";
        string json = "";
        server.Route = p => p == api ? Reply.Json(json) : Reply.Status(404);

        json = ServerJson(server, "v1.1.0", 10);
        foreach ((string what, bool? on, DateTime? last, int expected) in new (string, bool?, DateTime?, int)[]
        {
            ("turned off", false, null, 0),
            ("on, checked an hour ago", null, DateTime.UtcNow.AddHours(-1), 0),
            ("on, checked two days ago", null, DateTime.UtcNow.AddDays(-2), 1),
            ("on, never checked", null, null, 1)
        })
        {
            AppSettings.Update(s => { s.CheckForUpdates = on; s.LastUpdateCheck = last; });
            server.Clear();
            Updates.Start(main);
            await Wait(2500);
            Check(server.Requests == expected, what + ": " + server.Requests + " request(s) to the release server");
        }
        DateTime? recorded = AppSettings.Load().LastUpdateCheck;
        Check(recorded != null && DateTime.UtcNow - recorded.Value < TimeSpan.FromMinutes(1), "an answered check is remembered (LastUpdateCheck)");

        // A failed check isn't counted, so the next start tries again.
        AppSettings.Update(s => s.LastUpdateCheck = null);
        server.Route = p => Reply.Cut();
        Updates.ResetForTests();
        await Updates.CheckAutomaticallyAsync(main);
        Check(AppSettings.Load().LastUpdateCheck == null, "a check that got no answer isn't counted");
        server.Route = p => Reply.Status(500);
        await Updates.CheckAutomaticallyAsync(main);
        await Wait(4500);
        Check(!Application.Current.Windows.OfType<Window>().Any(w => w != main && w.IsVisible), "a failed automatic check shows nothing");

        // Offered once the window is idle; Later: not again in this run.
        server.Route = p => p == api ? Reply.Json(json) : Reply.Status(404);
        json = ServerJson(server, "v9.9.9", 10);
        Updates.ResetForTests();
        GuidedTour.Run(new[] { TourSteps.Welcome }, end => { });
        await Updates.CheckAutomaticallyAsync(main);
        await Wait(5000);
        Check(Dialog() == null, "not shown while the tour is on");
        GuidedTour.Current?.Stop(TourEnd.Skipped);
        await Foreground(main);
        UpdateDialog dialog = await WaitFor(Dialog, 10000);
        Check(dialog != null && dialog.Owner == main, "shown once the tour has ended and the window is idle" + (dialog == null ? " (" + IdleState(main) + ")" : ""));
        if (dialog == null)
            return;
        Click(dialog, "LaterButton");
        await Wait(500);
        Check(Dialog() == null && AppSettings.Load().SkippedUpdate == null, "Later closes it and skips nothing");
        await Updates.CheckAutomaticallyAsync(main);
        await Wait(5000);
        Check(Dialog() == null, "after Later: not offered again in this run");

        // "Next start": offered again. A modal window open: wait for it to close.
        Updates.ResetForTests();
        Window other = new Window { Title = "Harness other window", Owner = main, Width = 300, Height = 200, ShowInTaskbar = false };
        other.Show();
        await Updates.CheckAutomaticallyAsync(main);
        await Wait(4500);
        Check(Dialog() == null, "not shown while another window is open");
        other.Close();
        await Foreground(main);
        dialog = await WaitFor(Dialog, 10000);
        Check(dialog != null, "after the next start: offered again, once the other window has closed");
        Click(dialog, "SkipButton");
        await Wait(500);
        Check(AppSettings.Load().SkippedUpdate == "9.9.9", "Skip this version remembers 9.9.9");

        Updates.ResetForTests();
        await Updates.CheckAutomaticallyAsync(main);
        await Wait(5000);
        Check(Dialog() == null, "a skipped version isn't offered again");
        json = ServerJson(server, "v9.9.10", 10);
        await Foreground(main);
        await Updates.CheckAutomaticallyAsync(main);
        dialog = await WaitFor(Dialog, 10000);
        Check(dialog != null && dialog.Title == Loc.T("Update.Title"), "a newer one than the skipped version is");
        Click(dialog, "LaterButton");
        await Wait(500);

        AppSettings.Update(s => { s.SkippedUpdate = null; s.CheckForUpdates = null; });
        main.Topmost = false;
    }


    // The dialog waits for TidyMind to be the active app. A process started in the background may not take the
    // foreground by itself, so the harness borrows the current foreground thread's input for a moment.
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint attach, uint to, bool on);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

    static async Task Foreground(Window window)
    {
        IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        uint front = GetWindowThreadProcessId(GetForegroundWindow(), out _), self = GetCurrentThreadId();
        if (front != self)
            AttachThreadInput(self, front, true);
        SetForegroundWindow(hwnd);
        window.Activate();
        if (front != self)
            AttachThreadInput(self, front, false);
        await Wait(300);
        if (!window.IsActive)
        {
            // Windows releases its foreground lock to the process that sent the last input: one Alt press and release.
            keybd_event(0x12, 0, 0, UIntPtr.Zero);
            keybd_event(0x12, 0, 2, UIntPtr.Zero);
            SetForegroundWindow(hwnd);
            window.Activate();
            await Wait(300);
        }
        if (!window.IsActive)
            Console.WriteLine("  (the window couldn't be brought to the front: is the session locked?)");
        // That Alt press counts as input: wait out the idle time it started.
        await Wait(3200);
    }

    static string IdleState(MainWindow main) =>
        "active " + main.IsActive + ", tour " + (GuidedTour.Current != null) + ", overlay " + OverlayManager.Current + ", windows "
        + string.Join("/", Application.Current.Windows.OfType<Window>().Where(w => w.IsVisible).Select(w => w.GetType().Name))
        + ", captured " + (System.Windows.Input.Mouse.Captured != null);

    static UpdateDialog Dialog() => Application.Current.Windows.OfType<UpdateDialog>().FirstOrDefault(w => w.IsVisible);

    static void Click(Window window, string name)
    {
        if (window == null)
            return;
        Button button = (Button)window.FindName(name);
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
    }

    // ---- The dialog in three languages ---------------------------------------------

    static async Task DialogScenarios(MainWindow main, FakeServer server, string downloads)
    {
        Dictionary<string, Dictionary<string, string>> resx = ReadAllResx();
        HashSet<string> keys = new HashSet<string>(resx[Loc.English].Keys);
        byte[] payload = RandomBytes(2 * 1024 * 1024);
        string notes = "TidyMind 9.9.9\n\n- <b>Bold</b> stays as typed & so do *stars*\n- " + string.Join(" ", Enumerable.Repeat("A long line of notes.", 30))
            + "\n" + string.Join("\n", Enumerable.Range(1, 40).Select(i => "- item " + i));
        foreach (string code in Codes)
        {
            Console.WriteLine("Update dialog in " + code);
            Loc.Switch(code);
            string tag = code == Loc.SerbianLatin ? "sr" : code;
            HashSet<string> englishOnly = new HashSet<string>(code == Loc.English ? Enumerable.Empty<string>()
                : resx[Loc.English].Where(p => (p.Key.StartsWith("Update.") || p.Key.StartsWith("Common.")) && resx[code].TryGetValue(p.Key, out string t)
                    && t != p.Value && !resx[code].ContainsValue(p.Value)).Select(p => p.Value));

            UpdateRelease release = ServeRelease(server, "9.9.9", payload, bytesPerSecond: 700 * 1024, notes: notes);
            UpdateDialog dialog = new UpdateDialog(release) { Owner = main };
            dialog.Show();
            await Wait(400);
            Check(((TextBox)dialog.FindName("NotesBox")).Text == UpdateRelease.CleanNotes(notes) && ((TextBox)dialog.FindName("NotesBox")).IsReadOnly,
                code + ": release notes shown as plain, read-only text");
            ScrollViewer notesScroll = Descendants((TextBox)dialog.FindName("NotesBox")).OfType<ScrollViewer>().FirstOrDefault();
            Check(notesScroll != null && notesScroll.ScrollableHeight > 0 && dialog.ActualHeight < 600, code + ": long notes scroll inside a dialog "
                + dialog.ActualHeight.ToString("0") + " px tall");
            Check(((TextBlock)dialog.FindName("VersionsText")).Text == Loc.F("Update.Versions", "1.1.0", "9.9.9"), code + ": current → new version");
            InspectTexts(dialog, "Update dialog, offer (" + code + ")", keys, englishOnly);
            List<string> cut = Clipped(dialog).ToList();
            RenderWindow(dialog, tag + "-update-1-offer");

            Click(dialog, "UpdateButton");
            ProgressBar bar = (ProgressBar)dialog.FindName("DownloadBar");
            await WaitFor(() => bar.Value > 25 ? bar : null, 8000);
            Check(dialog.IsBusy && dialog.Status == Loc.T("Update.Status.Download") && bar.Value > 0 && bar.Value < 100,
                code + ": downloading, the bar at " + bar.Value.ToString("0") + "%");
            Check(bar.Foreground == Application.Current.FindResource("Success"), code + ": the bar is the palette's green");
            string progressText = ((TextBlock)dialog.FindName("ProgressText")).Text;
            Check(progressText.Contains("%") && progressText.Contains("MB") && progressText.Contains("2" + Loc.Culture.NumberFormat.NumberDecimalSeparator + "0"),
                code + ": percent and MB of the total: " + progressText);
            Check(((Button)dialog.FindName("CancelButton")).IsVisible && !((Button)dialog.FindName("UpdateButton")).IsVisible, code + ": only Cancel while downloading");
            InspectTexts(dialog, "Update dialog, downloading (" + code + ")", keys, englishOnly);
            cut.AddRange(Clipped(dialog));
            RenderWindow(dialog, tag + "-update-2-download");

            Click(dialog, "CancelButton");
            await WaitFor(() => ((FrameworkElement)dialog.FindName("OfferPanel")).IsVisible ? dialog : null, 4000);
            await Wait(300);
            Check(((FrameworkElement)dialog.FindName("OfferPanel")).IsVisible && !dialog.IsBusy
                && (!Directory.Exists(downloads) || Directory.GetFiles(downloads).Length == 0), code + ": Cancel stops it, nothing left, back to the offer");

            ServeRelease(server, "9.9.9", payload, checksumOf: RandomBytes(10));
            Click(dialog, "UpdateButton");
            await WaitFor(() => ((FrameworkElement)dialog.FindName("ErrorPanel")).IsVisible ? dialog : null, 15000);
            Check(dialog.Error == Loc.T("Update.Error.HashMismatch") && ((Button)dialog.FindName("RetryButton")).IsVisible
                && (!Directory.Exists(downloads) || Directory.GetFiles(downloads).Length == 0), code + ": a checksum mismatch says so, offers Try again, file deleted");
            InspectTexts(dialog, "Update dialog, error (" + code + ")", keys, englishOnly);
            cut.AddRange(Clipped(dialog));
            RenderWindow(dialog, tag + "-update-3-error");
            Check(cut.Count == 0, code + ": nothing cut off" + (cut.Count > 0 ? ": " + string.Join("; ", cut.Distinct()) : ""));
            dialog.Close();
            await Wait(300);
            Check(Application.Current.Windows.OfType<MainWindow>().Single().IsVisible, code + ": TidyMind still running after the error");

            SettingsWindow settings = new SettingsWindow(null) { Owner = main };
            settings.Show();
            await Wait(300);
            server.Route = p => p.EndsWith("/releases/latest") ? Reply.Json(ServerJson(server, "v9.9.9", payload.Length)) : Reply.Status(404);
            TextBlock note = (TextBlock)settings.FindName("UpdateNote");
            Check(((TextBlock)settings.FindName("UpdateVersionText")).Text == Loc.F("Settings.Updates.Version", "1.1.0")
                && ((CheckBox)settings.FindName("AutoUpdateSwitch")).IsChecked == true, code + ": Settings shows the version and the switch (on)");
            Click(settings, "CheckUpdatesButton");
            await WaitFor(() => note.Text == Loc.F("Settings.Updates.Available", "9.9.9") ? note : null, 5000);
            Check(note.Text == Loc.F("Settings.Updates.Available", "9.9.9") && (string)((Button)settings.FindName("CheckUpdatesButton")).Content == Loc.T("Settings.Updates.Install"),
                code + ": Check now: \"" + note.Text + "\", the button offers the update");
            InspectTexts(settings, "Settings, updates (" + code + ")", keys, englishOnly);
            RenderWindow(settings, tag + "-update-4-settings");
            settings.Close();
            settings = new SettingsWindow(null) { Owner = main };
            settings.Show();
            note = (TextBlock)settings.FindName("UpdateNote");
            server.Route = p => Reply.Cut();
            Click(settings, "CheckUpdatesButton");
            await WaitFor(() => note.Text == Loc.T("Settings.Updates.Failed.Offline") ? note : null, 5000);
            Check(note.Text == Loc.T("Settings.Updates.Failed.Offline"), code + ": offline, in plain words: \"" + note.Text + "\"");
            server.Route = p => Reply.Status(403);
            Click(settings, "CheckUpdatesButton");
            await WaitFor(() => note.Text == Loc.T("Settings.Updates.Failed.RateLimited") ? note : null, 5000);
            Check(note.Text == Loc.T("Settings.Updates.Failed.RateLimited"), code + ": rate limited: \"" + note.Text + "\"");
            server.Route = p => p.EndsWith("/releases/latest") ? Reply.Json(ServerJson(server, "v1.1.0", 10)) : Reply.Status(404);
            Click(settings, "CheckUpdatesButton");
            await WaitFor(() => note.Text == Loc.T("Settings.Updates.UpToDate") ? note : null, 5000);
            Check(note.Text == Loc.T("Settings.Updates.UpToDate"), code + ": up to date: \"" + note.Text + "\"");
            settings.Close();
            await Wait(200);
            Clean(downloads);
        }

        // The switch writes settings.json; a new Settings window reads it back.
        SettingsWindow again = new SettingsWindow(null) { Owner = main };
        again.Show();
        CheckBox toggle = (CheckBox)again.FindName("AutoUpdateSwitch");
        toggle.IsChecked = false;
        toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, toggle));
        Check(AppSettings.Load().CheckForUpdates == false, "the switch turned off is stored (CheckForUpdates: false)");
        again.Close();
        again = new SettingsWindow(null) { Owner = main };
        again.Show();
        toggle = (CheckBox)again.FindName("AutoUpdateSwitch");
        Check(toggle.IsChecked == false, "and shown off next time");
        toggle.IsChecked = true;
        toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, toggle));
        Check(AppSettings.Load().CheckForUpdates == null, "turned on again: back to the default (left out of settings.json)");
        again.Close();
        Loc.Switch(Loc.English);

        // Settings' "Update…" after Check now found one: Settings closes, the update dialog opens over the main window.
        server.Route = p => p.EndsWith("/releases/latest") ? Reply.Json(ServerJson(server, "v9.9.9", 10)) : Reply.Status(404);
        SettingsWindow fromSettings = new SettingsWindow(null) { Owner = main };
        fromSettings.Show();
        TextBlock found = (TextBlock)fromSettings.FindName("UpdateNote");
        Click(fromSettings, "CheckUpdatesButton");
        await WaitFor(() => found.Text == Loc.F("Settings.Updates.Available", "9.9.9") ? found : null, 5000);
        Click(fromSettings, "CheckUpdatesButton");
        UpdateDialog offered = await WaitFor(Dialog, 5000);
        Check(!fromSettings.IsVisible && offered != null && offered.Owner == main, "Settings › Update…: Settings closes, the update dialog opens over the main window");
        Click(offered, "LaterButton");
        await Wait(300);
    }

    // ---- Hand-over to the installer ------------------------------------------------------

    static async Task ApplyScenarios(MainWindow main, FakeServer server, string downloads)
    {
        Console.WriteLine("Hand-over to the installer (a stub that records its command line)");
        byte[] stub = BuildStub();
        if (stub == null)
        {
            Check(false, "the stub installer could be built");
            return;
        }
        UpdateRelease release = ServeRelease(server, "9.9.9", stub);
        VerifiedInstaller installer = await new UpdateClient(Updates.Endpoints, Updates.CurrentVersion).DownloadAsync(release, null, null, CancellationToken.None);
        string argsFile = installer.Path + ".args.txt";
        Window dialog = new Window { Title = "Harness update dialog", Owner = main, Width = 300, Height = 200, ShowInTaskbar = false };
        dialog.Show();

        bool allowClose = false;
        Window stubborn = new Window { Title = "Harness window that won't close", Owner = main, Width = 300, Height = 200, ShowInTaskbar = false };
        stubborn.Closing += (s, e) => e.Cancel = !allowClose;
        stubborn.Show();
        bool shutDown = false;
        Updates.ApplyResult result = Updates.Apply(installer, dialog, () => shutDown = true);
        await Wait(1500);
        Check(result == Updates.ApplyResult.WindowsOpen && !shutDown && !File.Exists(argsFile) && !File.Exists(Updates.PendingFile),
            "a window that refuses to close: nothing started, TidyMind keeps running (" + result + ")");
        dialog.Close();

        // The same through the real, modal dialog: it steps aside so that window can be dealt with (the update dialog
        // disables every other window), then comes back once it's closed, with the installer already downloaded.
        Updates.ResetForTests();
        _ = Application.Current.Dispatcher.BeginInvoke(new System.Action(() => Updates.Offer(main, release, installer)));
        UpdateDialog modal = await WaitFor(Dialog, 5000);
        Check(modal != null && modal.HasInstaller, "offered with the checked installer");
        Click(modal, "UpdateButton");
        await Wait(3500);
        Check(Dialog() == null && stubborn.IsVisible && stubborn.IsEnabled && !File.Exists(argsFile) && !shutDown,
            "Update now with a window that won't close: the dialog steps aside, that window can be used, nothing started");
        lock (boxes)
            Check(boxes.Count == 1 && boxes[0].Contains(Loc.T("Update.Error.WindowsOpen")), "and says so: " + string.Join(" / ", boxes));
        allowClose = true;
        stubborn.Close();
        await Foreground(main);
        modal = await WaitFor(Dialog, 10000);
        Check(modal != null && modal.HasInstaller, "once that window is closed, the update is offered again, already downloaded");
        Click(modal, "LaterButton");
        await Wait(500);
        lock (boxes)
            boxes.Clear();

        // Later released the checked file; the hand-over below gets a fresh one.
        installer = await new UpdateClient(Updates.Endpoints, Updates.CurrentVersion).DownloadAsync(release, null, null, CancellationToken.None);
        dialog = new Window { Title = "Harness update dialog", Owner = main, Width = 300, Height = 200, ShowInTaskbar = false };
        dialog.Show();

        Window plain = new Window { Title = "Harness open window", Owner = main, Width = 300, Height = 200, ShowInTaskbar = false };
        DateTime? closedAt = null;
        plain.Closed += (s, e) => closedAt = DateTime.Now;
        plain.Show();
        OverlayManager.Route(Overlay.QuickNotes);
        await Wait(500);
        DateTime? shutAt = null;
        result = Updates.Apply(installer, dialog, () => { shutDown = true; shutAt = DateTime.Now; });
        string args = null;
        for (int waited = 0; args == null && waited < 15000; waited += 250)
        {
            await Wait(250);
            try { args = File.Exists(argsFile) ? File.ReadAllText(argsFile) : null; } catch (IOException) { }
        }
        Console.WriteLine("  the stub was started as: " + args);
        Check(result == Updates.ApplyResult.Started && shutDown && closedAt != null && closedAt <= shutAt && !plain.IsVisible && !QuickNotesWindow.IsOpen,
            "the other windows closed the normal way (Quick Notes too), then the shutdown was asked for");
        Check(args != null && new[] { "/SILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/NOCANCEL", "/RELAUNCH=1", "/LOG=" + Updates.InstallerLog }
                .All(a => args.Contains(" " + a)),
            "the checked installer was started with /SILENT /SUPPRESSMSGBOXES /NORESTART /NOCANCEL /RELAUNCH=1 /LOG=...");
        Check(File.Exists(Updates.PendingFile) && File.ReadAllText(Updates.PendingFile).Contains("\"9.9.9\"") && Updates.Started == installer,
            "pending.json says 9.9.9 is being installed; the installer stays locked until TidyMind exits");
        Check(Application.Current.Windows.OfType<MainWindow>().Single().IsVisible, "(the harness's own shutdown was replaced: the main window is still up)");
        dialog.Close();
    }

    // A tiny .NET Framework console exe (csc via Windows PowerShell's Add-Type) that writes its command line to
    // "<its own path>.args.txt": the stand-in for Inno Setup.
    static byte[] BuildStub()
    {
        string folder = Path.Combine(Path.GetTempPath(), "tidymind-harness-stub-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(folder);
        string source = Path.Combine(folder, "stub.cs"), exe = Path.Combine(folder, "stub.exe");
        File.WriteAllText(source, "public static class Stub { public static int Main() { System.IO.File.WriteAllText("
            + "System.Reflection.Assembly.GetEntryAssembly().Location + \".args.txt\", System.Environment.CommandLine); return 0; } }");
        ProcessStartInfo start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };
        foreach (string a in new[] { "-NoProfile", "-NonInteractive", "-Command",
                     "Add-Type -TypeDefinition (Get-Content -Raw '" + source + "') -OutputAssembly '" + exe + "' -OutputType ConsoleApplication" })
            start.ArgumentList.Add(a);
        using (Process p = Process.Start(start))
        {
            string error = p.StandardError.ReadToEnd();
            p.WaitForExit(60000);
            if (!File.Exists(exe))
            {
                Console.WriteLine("  stub build failed: " + error);
                return null;
            }
        }
        byte[] bytes = File.ReadAllBytes(exe);
        Directory.Delete(folder, true);
        return bytes;
    }

    // ---- After an update ------------------------------------------------------------------

    static async Task AfterUpdateScenarios(string downloads)
    {
        Console.WriteLine("The start after an update");
        void Pending(string version, DateTime started) =>
            File.WriteAllText(Updates.PendingFile, JsonSerializer.Serialize(new { Version = version, From = "1.0.9", Started = started }));
        int LogLines() => File.Exists(Updates.LogPath) ? File.ReadAllLines(Updates.LogPath).Length : 0;

        Pending("1.1.0", DateTime.UtcNow);
        string message = Updates.FinishLastUpdate();
        Check(message == null && !File.Exists(Updates.PendingFile) && File.ReadAllText(Updates.LogPath).Contains("1.0.9 -> 1.1.0 installed"),
            "running the new version: logged as installed, no message");

        Pending("9.9.9", DateTime.UtcNow.AddMinutes(-20));
        File.WriteAllText(Updates.InstallerLog, "Setup version: Inno Setup\r\nError: harness says the copy failed\r\nRolling back changes.\r\nLog closed.\r\n");
        message = Updates.FinishLastUpdate();
        Check(message == Loc.F("Update.Failed.AfterRestart", "9.9.9", "1.1.0", Updates.LogPath) && !File.Exists(Updates.PendingFile)
            && !File.Exists(Updates.InstallerLog) && File.ReadAllText(Updates.LogPath).Contains("harness says the copy failed"),
            "still the old version: the user is told, updates.log has the end of the installer's log");

        Pending("9.9.9", DateTime.UtcNow);
        File.WriteAllText(Updates.InstallerLog, "Setup version: Inno Setup\r\nInstalling...\r\n");
        int before = LogLines();
        Stopwatch took = Stopwatch.StartNew();
        message = await System.Threading.Tasks.Task.Run(Updates.FinishLastUpdate);
        Check(message == null && File.Exists(Updates.PendingFile) && LogLines() == before + 1,
            "the installer still writing its log: nothing reported yet, checked again next start (" + took.ElapsedMilliseconds + " ms)");
        File.Delete(Updates.PendingFile);
        File.Delete(Updates.InstallerLog);

        File.WriteAllText(Updates.PendingFile, "{ not json");
        Check(Updates.FinishLastUpdate() == null && !File.Exists(Updates.PendingFile), "an unreadable pending.json is dropped");

        Updates.Started?.Dispose();
        foreach (string name in new[] { "TidyMind-Setup-1.0.0.exe", "TidyMind-Setup-9.9.9.exe.part", "TidyMind-Setup-9.9.9.exe.sha256", "unrelated.txt" })
            File.WriteAllText(Path.Combine(downloads, name), "x");
        Updates.CleanUpDownloads();
        string[] left = Directory.GetFiles(downloads).Select(Path.GetFileName).ToArray();
        Check(left.SequenceEqual(new[] { "unrelated.txt" }), "old installers, .part and .sha256 files are deleted at start; nothing else ("
            + string.Join(", ", left) + ")");
        Directory.Delete(downloads, true);
    }

    // ---- Reminder tasks across an update ------------------------------------------------

    // A reminder's task that ran at its time but couldn't start TidyMind (as while an update replaces the program files;
    // here Harness.exe answers "--remind" with exit code 2): the repair at the next start schedules it again.
    static async Task TaskRepairScenario()
    {
        Console.WriteLine("Reminder task whose run failed");
        if (!Path.GetFileName(Environment.ProcessPath ?? "").Equals("Harness.exe", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("  (skipped: run Harness.exe, not dotnet Harness.dll, so the task starts this exe)");
            return;
        }
        Guid id = Guid.NewGuid();
        DateTime now = DateTime.Now;
        DateTime due = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second).AddSeconds(8);
        string name = "TidyMind_Reminder_" + id;
        ReminderManager.AddReminder(new Reminder { Id = id, Title = "Harness repair", Message = "x", NextFireTime = due, RepeatType = RepeatType.Once });
        try
        {
            using TaskService service = new TaskService();
            Microsoft.Win32.TaskScheduler.Task task = null;
            for (int waited = 0; waited < 90000; waited += 1000)
            {
                await Wait(1000);
                task?.Dispose();
                task = service.GetTask(name);
                if (task != null && task.LastRunTime >= due.AddSeconds(-1) && task.State != TaskState.Running && task.LastTaskResult != 0x41301)
                    break;
            }
            Check(task != null && task.LastTaskResult == 2, "the task ran at its time and failed (result " + task?.LastTaskResult + ")");
            task?.Dispose();
            ReminderManager.RepairTasksInBackground(force: true);
            await Wait(4000);
            using (Microsoft.Win32.TaskScheduler.Task repaired = service.GetTask(name))
            {
                DateTime start = repaired?.Definition.Triggers.OfType<TimeTrigger>().First().StartBoundary ?? DateTime.MinValue;
                Check(start > DateTime.Now && start < DateTime.Now.AddMinutes(2), "scheduled again for " + start.ToString("HH:mm:ss"));
            }
            Check(File.ReadAllText(AppPaths.Data("reminders.log")).Contains("failed to start TidyMind"), "reminders.log says why");
        }
        finally
        {
            ReminderManager.DeleteReminder(id);
            using TaskService service = new TaskService();
            Check(service.GetTask(name) == null, "the test reminder and its task are gone");
        }
    }

    // ---- The fake release server --------------------------------------------------------

    static UpdateEndpoints TestEndpoints(FakeServer server, string downloads, double checkSeconds, double stallSeconds)
    {
        string root = server?.Base ?? "http://127.0.0.1:9";
        return new UpdateEndpoints(new Uri(root + "/repos" + ReleasesPath + "/releases/latest"), new Uri(root + ReleasesPath + "/releases/download/"),
            new[] { "localhost" }, allowHttp: true, downloads, TimeSpan.FromSeconds(checkSeconds), TimeSpan.FromSeconds(stallSeconds));
    }

    // Before App starts, for every mode: nothing may ever reach GitHub from the harness.
    internal static void PointUpdatesNowhere(string data) =>
        Updates.Endpoints = TestEndpoints(null, data.TrimEnd('\\') + "-downloads", 2, 2);

    static string GitHubJson(string tag, string assetBase, long size, string body = "notes", bool draft = false, bool prerelease = false,
        bool installer = true, bool checksum = true)
    {
        string name = "TidyMind-Setup-" + tag.TrimStart('v') + ".exe";
        string b = assetBase ?? "https://github.com" + ReleasesPath + "/releases/download/" + tag + "/";
        List<object> assets = new List<object>();
        if (installer)
            assets.Add(new { name, browser_download_url = b + name, size });
        if (checksum)
            assets.Add(new { name = name + ".sha256", browser_download_url = b + name + ".sha256", size = 100 });
        return JsonSerializer.Serialize(new { tag_name = tag, name = "TidyMind " + tag, draft, prerelease, body, assets });
    }

    static string ServerJson(FakeServer server, string tag, long size, bool draft = false, bool prerelease = false, string body = "notes") =>
        GitHubJson(tag, server.Base + ReleasesPath + "/releases/download/" + tag + "/", size, body, draft, prerelease);

    // Routes the latest-release answer, the .sha256 file and the installer (through a redirect to "localhost", the
    // stand-in for GitHub's file storage), as GitHub would; each option breaks one thing.
    static UpdateRelease ServeRelease(FakeServer server, string version, byte[] payload, byte[] checksumOf = null, long? declaredSize = null,
        bool omitLength = false, string cdn = null, int cutAfter = -1, int bytesPerSecond = 0, int stallAfterHeadersMs = 0, int installerStatus = 200,
        string notes = "notes")
    {
        string tag = "v" + version, name = "TidyMind-Setup-" + version + ".exe";
        string hash = Convert.ToHexString(SHA256.HashData(checksumOf ?? payload)).ToLowerInvariant();
        string json = GitHubJson(tag, server.Base + ReleasesPath + "/releases/download/" + tag + "/", declaredSize ?? payload.Length, notes);
        string cdnUrl = cdn ?? "http://localhost:" + server.Port + "/cdn/" + name + "?sig=harness";
        server.Route = path =>
        {
            if (path == "/repos" + ReleasesPath + "/releases/latest")
                return Reply.Json(json);
            if (path == ReleasesPath + "/releases/download/" + tag + "/" + name + ".sha256")
                return Reply.Text(hash + "  " + name + "\n");
            if (path == ReleasesPath + "/releases/download/" + tag + "/" + name)
                return installerStatus != 200 ? Reply.Status(installerStatus) : Reply.Redirect(cdnUrl);
            if (path.StartsWith("/cdn/" + name))
                return new Reply { Body = payload, OmitLength = omitLength, CutAfter = cutAfter, BytesPerSecond = bytesPerSecond, PauseAfterHeadersMs = stallAfterHeadersMs };
            return Reply.Status(404);
        };
        server.Clear();
        return UpdateRelease.Parse(json, Updates.Endpoints, out _, out _);
    }

    static byte[] RandomBytes(int count)
    {
        byte[] bytes = new byte[count];
        new Random(count).NextBytes(bytes);
        return bytes;
    }

    static void Clean(string folder)
    {
        if (Directory.Exists(folder))
            foreach (string file in Directory.GetFiles(folder))
                File.Delete(file);
    }

    // Progress<T> posts to the UI thread; the download test wants every report as it happens.
    sealed class SyncProgress : IProgress<DownloadProgress>
    {
        private readonly Action<DownloadProgress> report;
        public SyncProgress(Action<DownloadProgress> report) => this.report = report;
        public void Report(DownloadProgress value) => report(value);
    }

    sealed class Reply
    {
        public int Code = 200;
        public byte[] Body = new byte[0];
        public string ContentType = "application/octet-stream";
        public string Location;
        public bool OmitLength;
        public int CutAfter = -1;
        public int DelayMs;
        public int PauseAfterHeadersMs;
        public int BytesPerSecond;
        public bool CutAtOnce;

        public static Reply Json(string json) => new Reply { Body = Encoding.UTF8.GetBytes(json), ContentType = "application/json" };
        public static Reply Text(string text) => new Reply { Body = Encoding.ASCII.GetBytes(text), ContentType = "text/plain" };
        public static Reply Status(int status, string body = "") => new Reply { Code = status, Body = Encoding.UTF8.GetBytes(body) };
        public static Reply Redirect(string location) => new Reply { Code = 302, Location = location };
        public static Reply Cut() => new Reply { CutAtOnce = true };
        public Reply Delayed(int ms) { DelayMs = ms; return this; }
    }

    // HTTP/1.1 on 127.0.0.1 (and ::1, so "localhost" works too), one request per connection. Only loopback: no
    // firewall prompt, nothing reachable from outside.
    sealed class FakeServer : IDisposable
    {
        private readonly TcpListener v4;
        private readonly TcpListener v6;
        private readonly List<string> log = new List<string>();
        public volatile Func<string, Reply> Route = p => Reply.Status(404);
        public int Requests => log.Count;

        public FakeServer()
        {
            v4 = new TcpListener(IPAddress.Loopback, 0);
            v4.Start();
            Port = ((IPEndPoint)v4.LocalEndpoint).Port;
            try
            {
                v6 = new TcpListener(IPAddress.IPv6Loopback, Port);
                v6.Start();
            }
            catch (SocketException)
            {
                v6 = null;
            }
            Listen(v4);
            if (v6 != null)
                Listen(v6);
        }

        public int Port { get; }
        public string Base => "http://127.0.0.1:" + Port;
        public List<string> Log { get { lock (log) return log.ToList(); } }
        public void Clear() { lock (log) log.Clear(); }

        private void Listen(TcpListener listener) => new Thread(() =>
        {
            while (true)
            {
                TcpClient client;
                try { client = listener.AcceptTcpClient(); }
                catch (Exception) { return; }
                ThreadPool.QueueUserWorkItem(_ => Serve(client));
            }
        }) { IsBackground = true }.Start();

        private void Serve(TcpClient client)
        {
            using (client)
            {
                try
                {
                    NetworkStream stream = client.GetStream();
                    StringBuilder head = new StringBuilder();
                    while (!head.ToString().EndsWith("\r\n\r\n") && head.Length < 16384)
                    {
                        int b = stream.ReadByte();
                        if (b < 0) return;
                        head.Append((char)b);
                    }
                    string[] lines = head.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                    string path = lines[0].Split(' ')[1];
                    lock (log) log.Add(string.Join(" | ", lines));
                    Reply reply = Route(path);
                    if (reply.CutAtOnce)
                    {
                        client.Client.LingerState = new LingerOption(true, 0);
                        return;
                    }
                    if (reply.DelayMs > 0) Thread.Sleep(reply.DelayMs);
                    StringBuilder h = new StringBuilder("HTTP/1.1 " + reply.Code + " Harness\r\nConnection: close\r\nContent-Type: " + reply.ContentType + "\r\n");
                    if (!reply.OmitLength) h.Append("Content-Length: " + reply.Body.Length + "\r\n");
                    if (reply.Location != null) h.Append("Location: " + reply.Location + "\r\n");
                    h.Append("\r\n");
                    byte[] headBytes = Encoding.ASCII.GetBytes(h.ToString());
                    stream.Write(headBytes, 0, headBytes.Length);
                    stream.Flush();
                    if (reply.PauseAfterHeadersMs > 0) Thread.Sleep(reply.PauseAfterHeadersMs);
                    int end = reply.CutAfter >= 0 ? reply.CutAfter : reply.Body.Length;
                    int chunk = 16384;
                    for (int at = 0; at < end; at += chunk)
                    {
                        stream.Write(reply.Body, at, Math.Min(chunk, end - at));
                        if (reply.BytesPerSecond > 0) Thread.Sleep(chunk * 1000 / reply.BytesPerSecond);
                    }
                    if (reply.CutAfter >= 0)
                        client.Client.LingerState = new LingerOption(true, 0); // a reset, not a clean end
                }
                catch (Exception e) when (e is IOException || e is SocketException || e is ObjectDisposedException)
                {
                    // The client went away (cancelled): fine.
                }
            }
        }

        public void Dispose()
        {
            v4.Stop();
            v6?.Stop();
        }
    }
}
