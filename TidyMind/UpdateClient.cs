using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace TidyMind
{
    public sealed class UpdateCheckResult
    {
        public UpdateCheckResult(UpdateProblem problem, UpdateRelease? newer, bool answered, string reason)
        {
            Problem = problem;
            Newer = newer;
            Answered = answered;
            Reason = reason;
        }

        public UpdateProblem Problem { get; }

        // Set only for a version newer than the running one.
        public UpdateRelease? Newer { get; }

        // GitHub answered (with anything, an error included): shown as the last check in Settings. A connection that
        // failed isn't.
        public bool Answered { get; }

        // English, for updates.log.
        public string Reason { get; }
    }

    public readonly struct DownloadProgress
    {
        public DownloadProgress(long received, long total)
        {
            Received = received;
            Total = total;
        }

        public long Received { get; }
        public long Total { get; }
    }

    // An installer that was downloaded and matched its published size and SHA-256 in this session. The file has been
    // open since it was checked (others may read it, nobody may change or delete it) and stays so until this process
    // ends, so what's started is what was checked. Only UpdateClient.DownloadAsync makes one.
    public sealed class VerifiedInstaller : IDisposable
    {
        private readonly FileStream locked;

        internal VerifiedInstaller(string path, FileStream locked, Version version)
        {
            Path = path;
            this.locked = locked;
            Version = version;
        }

        public string Path { get; }
        public Version Version { get; }

        public void Dispose() => locked.Dispose();
    }

    // The update's network side: asks GitHub for the latest release, and downloads and checks its installer. Plain
    // HTTPS GETs with "TidyMind/<version>" as user agent; no cookies, no sign-in, nothing about the user or the PC. Every
    // redirect is checked against UpdateEndpoints before it's followed.
    public sealed class UpdateClient
    {
        private const int MaxRedirects = 5;
        private const int MaxReleaseJsonBytes = 1024 * 1024;
        private const int MaxChecksumBytes = 1024;
        private static readonly Regex Sha256Line = new Regex(@"^\s*([0-9a-fA-F]{64})(?:\s+\*?(\S.*?))?\s*$");

        // The last release JSON GitHub sent in this run, with its ETag: the next check asks "changed since?"
        // (If-None-Match) and a 304 (no body) reuses it. Memory only, so each start asks plainly.
        private static readonly object answerLock = new object();
        private static (Uri Url, EntityTagHeaderValue ETag, string Json)? lastAnswer;

        private readonly UpdateEndpoints endpoints;
        private readonly Version current;

        public UpdateClient(UpdateEndpoints endpoints, Version current)
        {
            this.endpoints = endpoints;
            this.current = UpdateRelease.Normalize(current);
        }

        // Never throws: every failure comes back as a problem with its reason.
        public async Task<UpdateCheckResult> CheckAsync()
        {
            using CancellationTokenSource timeout = new CancellationTokenSource();
            timeout.CancelAfter(endpoints.CheckTimeout);
            try
            {
                Uri url = endpoints.LatestRelease;
                (EntityTagHeaderValue ETag, string Json)? earlier;
                lock (answerLock)
                    earlier = lastAnswer != null && lastAnswer.Value.Url == url ? (lastAnswer.Value.ETag, lastAnswer.Value.Json) : null;

                using HttpClient client = NewClient();
                using HttpResponseMessage response = await GetAsync(client, url, "application/vnd.github+json",
                    endpoints.IsApiRedirectTarget, timeout.Token, earlier?.ETag).ConfigureAwait(false);

                int status = (int)response.StatusCode;
                string json;
                string unchanged = "";
                if (response.StatusCode == HttpStatusCode.NotModified && earlier != null)
                {
                    json = earlier.Value.Json;
                    unchanged = " (unchanged since the last check)";
                }
                else
                {
                    if (response.StatusCode == HttpStatusCode.NotFound)
                        return Answer(UpdateProblem.NoRelease, "GitHub has no published release (404)");
                    if (response.StatusCode == HttpStatusCode.Forbidden || response.StatusCode == HttpStatusCode.TooManyRequests)
                        return Answer(UpdateProblem.RateLimited, "GitHub refused the request (" + status + ", rate limit)");
                    if (!response.IsSuccessStatusCode)
                        return Answer(UpdateProblem.ServerError, "GitHub answered " + status);
                    json = await ReadTextAsync(response, MaxReleaseJsonBytes, timeout.Token).ConfigureAwait(false);
                    lock (answerLock)
                        lastAnswer = response.Headers.ETag != null ? (url, response.Headers.ETag, json) : null;
                }

                UpdateRelease? release = UpdateRelease.Parse(json, endpoints, out string? problem, out bool ignored);
                if (release == null)
                    return ignored ? Answer(UpdateProblem.None, problem ?? "nothing to offer") : Answer(UpdateProblem.BadAnswer, problem ?? "unreadable");
                if (release.Version <= current)
                    return Answer(UpdateProblem.None, "latest is " + release.Version.ToString(3) + ", running " + current.ToString(3) + ": up to date" + unchanged);
                return new UpdateCheckResult(UpdateProblem.None, release, true,
                    "latest is " + release.Version.ToString(3) + ", running " + current.ToString(3) + ": update available" + unchanged);
            }
            catch (UpdateFailure e)
            {
                return new UpdateCheckResult(e.Problem, null, e.Problem != UpdateProblem.Offline, e.Message);
            }
            catch (OperationCanceledException)
            {
                return new UpdateCheckResult(UpdateProblem.Timeout, null, false,
                    "no answer within " + endpoints.CheckTimeout.TotalSeconds + " s");
            }
            catch (Exception e) when (e is HttpRequestException || e is IOException)
            {
                return new UpdateCheckResult(UpdateProblem.Offline, null, false, "couldn't connect: " + Describe(e));
            }
        }

        private static UpdateCheckResult Answer(UpdateProblem problem, string reason) =>
            new UpdateCheckResult(problem, null, true, reason);

        // The .sha256 file first, then the installer as <name>.part, renamed once complete, then its size and SHA-256
        // are checked on the file as it lies on disk. Throws UpdateFailure; on any failure the files are deleted.
        public async Task<VerifiedInstaller> DownloadAsync(UpdateRelease release, IProgress<DownloadProgress>? progress,
            Action? verifying, CancellationToken cancel)
        {
            string folder = endpoints.DownloadFolder;
            string target = Path.Combine(folder, release.Installer.Name);
            string part = target + ".part";
            try
            {
                Directory.CreateDirectory(folder);
                DeleteIfThere(part);
                DeleteIfThere(target);

                using HttpClient client = NewClient();
                string expected = ParseChecksum(await DownloadTextAsync(client, release.Checksum, cancel).ConfigureAwait(false),
                    release.Installer.Name);
                await DownloadFileAsync(client, release.Installer, part, progress, cancel).ConfigureAwait(false);
                File.Move(part, target);

                verifying?.Invoke();
                FileStream locked = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
                try
                {
                    if (locked.Length != release.Installer.Size)
                        throw new UpdateFailure(UpdateProblem.SizeMismatch,
                            "the installer is " + locked.Length + " bytes, the release says " + release.Installer.Size);
                    string actual = Convert.ToHexString(await SHA256.HashDataAsync(locked, cancel).ConfigureAwait(false));
                    if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                        throw new UpdateFailure(UpdateProblem.HashMismatch,
                            "the installer's SHA-256 is " + actual + ", the published one " + expected.ToUpperInvariant());
                    return new VerifiedInstaller(target, locked, release.Version);
                }
                catch
                {
                    locked.Dispose();
                    throw;
                }
            }
            catch (Exception e)
            {
                DeleteIfThere(part);
                DeleteIfThere(target);
                if (e is UpdateFailure)
                    throw;
                if (e is OperationCanceledException)
                    throw new UpdateFailure(cancel.IsCancellationRequested ? UpdateProblem.Cancelled : UpdateProblem.Timeout,
                        cancel.IsCancellationRequested ? "download cancelled" : "no data for " + endpoints.StallTimeout.TotalSeconds + " s");
                if (e is HttpRequestException)
                    throw new UpdateFailure(UpdateProblem.Offline, "download stopped: " + Describe(e));
                if (e is IOException || e is UnauthorizedAccessException)
                    throw new UpdateFailure(UpdateProblem.CantSave, "couldn't write " + folder + ": " + e.Message);
                throw;
            }
        }

        // "<64 hex digits>" alone, or followed by the file name (sha256sum's form): then it must be this installer's.
        internal static string ParseChecksum(string text, string installerName)
        {
            string line = text.Replace("\r", "").Split('\n')[0].TrimStart('﻿');
            Match m = Sha256Line.Match(line);
            if (!m.Success)
                throw new UpdateFailure(UpdateProblem.BadAnswer, "the .sha256 file has no SHA-256");
            if (m.Groups[2].Success && !string.Equals(m.Groups[2].Value, installerName, StringComparison.OrdinalIgnoreCase))
                throw new UpdateFailure(UpdateProblem.BadAnswer, "the .sha256 file names another file (" + m.Groups[2].Value + ")");
            return m.Groups[1].Value;
        }

        private async Task<string> DownloadTextAsync(HttpClient client, UpdateAsset asset, CancellationToken cancel)
        {
            if (asset.Size > MaxChecksumBytes)
                throw new UpdateFailure(UpdateProblem.BadAnswer, asset.Name + " is " + asset.Size + " bytes, too big for a checksum");
            using CancellationTokenSource stall = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            stall.CancelAfter(endpoints.StallTimeout);
            using HttpResponseMessage response = await GetAsync(client, asset.Url, "application/octet-stream",
                endpoints.IsRedirectTarget, stall.Token).ConfigureAwait(false);
            EnsureSuccess(response, asset);
            return await ReadTextAsync(response, MaxChecksumBytes, stall.Token).ConfigureAwait(false);
        }

        private async Task DownloadFileAsync(HttpClient client, UpdateAsset asset, string path, IProgress<DownloadProgress>? progress,
            CancellationToken cancel)
        {
            using CancellationTokenSource stall = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            stall.CancelAfter(endpoints.StallTimeout);
            using HttpResponseMessage response = await GetAsync(client, asset.Url, "application/octet-stream",
                endpoints.IsRedirectTarget, stall.Token).ConfigureAwait(false);
            EnsureSuccess(response, asset);
            long? length = response.Content.Headers.ContentLength;
            if (length != null && length != asset.Size)
                throw new UpdateFailure(UpdateProblem.SizeMismatch,
                    "the server sends " + length + " bytes, the release says " + asset.Size);

            using Stream source = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
            using FileStream file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            byte[] buffer = new byte[81920];
            long received = 0;
            long reportedAt = -1;
            progress?.Report(new DownloadProgress(0, asset.Size));
            while (true)
            {
                int read;
                try
                {
                    read = await source.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                }
                catch (IOException e)
                {
                    // The connection, not the disk (a failed write below is CantSave).
                    throw new UpdateFailure(UpdateProblem.Offline, "download stopped: " + Describe(e));
                }
                if (read == 0)
                    break;
                received += read;
                if (received > asset.Size)
                    throw new UpdateFailure(UpdateProblem.SizeMismatch, "the server sent more than the release's " + asset.Size + " bytes");
                await file.WriteAsync(buffer.AsMemory(0, read), stall.Token).ConfigureAwait(false);
                stall.CancelAfter(endpoints.StallTimeout);
                // About every half percent: enough for a smooth bar without flooding the UI thread.
                if (received - reportedAt >= asset.Size / 200 || received == asset.Size)
                {
                    reportedAt = received;
                    progress?.Report(new DownloadProgress(received, asset.Size));
                }
            }
            if (received != asset.Size)
                throw new UpdateFailure(UpdateProblem.SizeMismatch, "got " + received + " of the release's " + asset.Size + " bytes");
        }

        private static void EnsureSuccess(HttpResponseMessage response, UpdateAsset asset)
        {
            if (!response.IsSuccessStatusCode)
                throw new UpdateFailure(response.StatusCode == HttpStatusCode.Forbidden || response.StatusCode == HttpStatusCode.TooManyRequests
                    ? UpdateProblem.RateLimited : UpdateProblem.ServerError,
                    asset.Name + ": the server answered " + (int)response.StatusCode);
        }

        // Follows redirects by hand, each one only if `allowed` says so. The response is the caller's to dispose.
        private static async Task<HttpResponseMessage> GetAsync(HttpClient client, Uri uri, string accept, Func<Uri, bool> allowed,
            CancellationToken cancel, EntityTagHeaderValue? ifNoneMatch = null)
        {
            for (int hop = 0; ; hop++)
            {
                using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
                if (ifNoneMatch != null)
                    request.Headers.IfNoneMatch.Add(ifNoneMatch);
                HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel)
                    .ConfigureAwait(false);
                int status = (int)response.StatusCode;
                if (status < 300 || status > 399 || status == 304)
                    return response;

                Uri? next = response.Headers.Location;
                response.Dispose();
                if (next == null)
                    throw new UpdateFailure(UpdateProblem.BadAnswer, "a redirect (" + status + ") without a location");
                if (!next.IsAbsoluteUri)
                    next = new Uri(uri, next);
                // Signed storage links carry their signature in the query: the log gets the address without it.
                if (!allowed(next))
                    throw new UpdateFailure(UpdateProblem.ForeignHost, "redirected to " + next.GetLeftPart(UriPartial.Path) + ", which isn't allowed");
                if (hop >= MaxRedirects)
                    throw new UpdateFailure(UpdateProblem.BadAnswer, "more than " + MaxRedirects + " redirects");
                uri = next;
            }
        }

        private static async Task<string> ReadTextAsync(HttpResponseMessage response, int maxBytes, CancellationToken cancel)
        {
            if (response.Content.Headers.ContentLength > maxBytes)
                throw new UpdateFailure(UpdateProblem.BadAnswer, "the answer is larger than " + maxBytes + " bytes");
            using Stream stream = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
            using MemoryStream copy = new MemoryStream();
            byte[] buffer = new byte[16384];
            while (true)
            {
                int read;
                try
                {
                    read = await stream.ReadAsync(buffer, cancel).ConfigureAwait(false);
                }
                catch (IOException e)
                {
                    throw new UpdateFailure(UpdateProblem.Offline, "the answer stopped: " + Describe(e));
                }
                if (read == 0)
                    break;
                if (copy.Length + read > maxBytes)
                    throw new UpdateFailure(UpdateProblem.BadAnswer, "the answer is larger than " + maxBytes + " bytes");
                copy.Write(buffer, 0, read);
            }
            return Encoding.UTF8.GetString(copy.GetBuffer(), 0, (int)copy.Length);
        }

        private HttpClient NewClient()
        {
            SocketsHttpHandler handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                PreAuthenticate = false,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                ConnectTimeout = endpoints.CheckTimeout
            };
            HttpClient client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("TidyMind", current.ToString(3)));
            return client;
        }

        private static string Describe(Exception e)
        {
            SocketException? socket = e.InnerException as SocketException ?? e.InnerException?.InnerException as SocketException;
            return socket != null ? socket.SocketErrorCode + " (" + e.Message + ")" : e.Message;
        }

        private static void DeleteIfThere(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                // Still open somewhere: the next start's clean-up takes it.
            }
        }
    }
}
