using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using StupidDict.App.Localization;

namespace StupidDict.App.Assets;

/// <summary>What stage a download reached, for the progress UI.</summary>
/// <param name="ResumedFromBytes">
/// Offset this attempt resumed from over an existing ".part" file, 0 for a
/// fresh transfer. The UI uses it to label a follow-up attempt as a resume
/// instead of indistinguishable "downloading" bar number two.
/// </param>
public sealed record DownloadProgress(long ReceivedBytes, long? TotalBytes, string SourceUrl, long ResumedFromBytes = 0);

/// <summary>Thrown when a downloaded asset's SHA-256 differs from the published checksum.</summary>
public sealed class ChecksumMismatchException : Exception
{
    public ChecksumMismatchException(string message) : base(message) { }
}

/// <summary>A finished download: the local file and which source served it.</summary>
public sealed record DownloadResult(string FilePath, string SourceUrl);

/// <summary>Minimal seam so tests can fake downloads.</summary>
public interface IAssetDownloader
{
    Task<DownloadResult> DownloadAsync(string assetName, string destinationFile,
        IProgress<DownloadProgress>? progress, CancellationToken cancellation);

    /// <summary>The "<c>&lt;asset&gt;.sha256</c>" companion file, or null when unavailable.</summary>
    Task<string?> FetchChecksumAsync(string assetName, CancellationToken cancellation);
}

/// <summary>How a download attempt should route through proxies.</summary>
internal enum DownloadProxyMode
{
    /// <summary>The platform's own system proxy (WinINET settings incl. PAC on
    /// Windows, env vars on unix) — the route the user's browser takes, which
    /// is the one route known to work behind VPNs.</summary>
    PlatformDefault,
    /// <summary>Bypass every proxy: a dead or stale system proxy must not take
    /// the mirror connections down with it.</summary>
    Direct,
    /// <summary>An explicitly detected proxy (env vars, system settings, probed ports).</summary>
    Explicit,
}

/// <summary>
/// Downloads GitHub release assets with the fallback chain the product needs
/// for CN networks: platform-default proxy → direct mirrors → detected
/// proxies → probed local proxy ports. Each attempt resumes an interrupted
/// ".part" file via HTTP Range. This is asset bootstrapping only — dictionary
/// lookups stay fully offline.
/// </summary>
public sealed partial class AssetDownloadService : IAssetDownloader
{
    private static readonly TimeSpan FirstByteTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ChecksumTimeout = TimeSpan.FromSeconds(10);
    // How long a body read may sit without a single byte before this attempt
    // is declared stalled. HttpClient.Timeout stops at the response headers
    // under HttpCompletionOption.ResponseHeadersRead — the body stream is
    // governed by nothing, so without this a connection that goes quiet
    // mid-transfer (mobile network switch, dead proxy process, half-dead
    // mirror) hangs the download on the old progress forever.
    private static readonly TimeSpan BodyIdleTimeout = TimeSpan.FromSeconds(60);

    private readonly Func<HttpMessageHandler>? _handlerFactory;
    private readonly TimeSpan _bodyIdleTimeout;

    public AssetDownloadService() : this(handlerFactory: null, bodyIdleTimeout: null) { }

    // Test seams (same shape as UpdateChecker's handler injection): a
    // scripted HttpMessageHandler stands in for the network — proxy routing
    // does not apply to it — and a short idle timeout keeps stall tests fast.
    internal AssetDownloadService(Func<HttpMessageHandler>? handlerFactory, TimeSpan? bodyIdleTimeout)
    {
        _handlerFactory = handlerFactory;
        _bodyIdleTimeout = bodyIdleTimeout ?? BodyIdleTimeout;
    }

    public async Task<DownloadResult> DownloadAsync(string assetName, string destinationFile,
        IProgress<DownloadProgress>? progress, CancellationToken cancellation)
    {
        var githubUrl = ReleaseAssets.GithubUrl(assetName);
        foreach (var (url, mode, proxy) in BuildAttempts(githubUrl))
        {
            try
            {
                return await DownloadFromAsync(url, mode, proxy, destinationFile, progress, cancellation)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // every source gets its chance; only total failure surfaces
            }
        }
        throw new InvalidOperationException(Translations.Instance.AllSourcesFailed);
    }

    /// <summary>
    /// Attempts in escalation order. Phase 1 rides the platform default over
    /// every source (identical to the user's browser: system proxy when one
    /// exists, direct otherwise). Phase 2 bypasses proxies so a stale/dead
    /// system proxy cannot sink the mirrors. Explicitly detected proxies come
    /// last and only re-try the first two sources each.
    /// </summary>
    // The enumerator is evaluated lazily between attempts, and detecting the
    // proxies for the late phases blocks (macOS scutil subprocess, local port
    // probes) — every await below must therefore use ConfigureAwait(false) so
    // those probes never run on the UI thread that started the download.
    internal IEnumerable<(string Url, DownloadProxyMode Mode, IWebProxy? Proxy)> BuildAttempts(string githubUrl)
    {
        var sources = ReleaseAssets.MirrorUrls(githubUrl).ToList();

        foreach (var url in sources)
            yield return (url, DownloadProxyMode.PlatformDefault, null);

        foreach (var url in sources.Skip(1))
            yield return (url, DownloadProxyMode.Direct, null);

        var detected = ProxyDetector.DetectFromEnvironmentAndSystem().ToList();
        foreach (var proxy in detected)
            foreach (var url in sources.Take(2))
                yield return (url, DownloadProxyMode.Explicit, proxy);

        foreach (var proxy in ProxyDetector.ProbeCommonLocalPorts())
            foreach (var url in sources.Take(2))
                yield return (url, DownloadProxyMode.Explicit, proxy);
    }

    private async Task<DownloadResult> DownloadFromAsync(string url, DownloadProxyMode mode, IWebProxy? proxy,
        string destinationFile, IProgress<DownloadProgress>? progress, CancellationToken cancellation)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
        var partFile = destinationFile + ".part";
        var resumeFrom = File.Exists(partFile) ? new FileInfo(partFile).Length : 0;

        using HttpMessageHandler handler = _handlerFactory is { } factory
            ? factory()
            : BuildSocketsHandler(mode, proxy);
        using var client = new HttpClient(handler);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("stupiddict");

        // A ".part" continues only a download of the SAME asset. The resume
        // request itself is stateless — a source whose copy of the asset was
        // replaced (the data tag bump is a real precedent) happily answers
        // 206 with the NEW bytes from the requested offset, and appending
        // welds two different files into a corrupt zip; a broken source may
        // even answer 206 from its own byte zero. Every resume answer must
        // therefore prove where it starts (Content-Range), or the ".part" is
        // dropped and this source restarts from zero.
        var tryResume = resumeFrom > 0;
        while (true)
        {
            using var firstByte = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            firstByte.CancelAfter(FirstByteTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (tryResume)
                request.Headers.Range = new RangeHeaderValue(resumeFrom, null);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                firstByte.Token).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                File.Delete(partFile);
                throw new HttpRequestException("Range not satisfiable", null, response.StatusCode);
            }
            response.EnsureSuccessStatusCode();

            if (tryResume)
            {
                if (response.StatusCode == HttpStatusCode.PartialContent &&
                    !ContentRangeStartsAt(response, resumeFrom))
                {
                    // Not a continuation of OUR bytes. One clean restart on
                    // this source (no Range); a second misbehaving answer
                    // fails the attempt into the chain like any other.
                    File.Delete(partFile);
                    resumeFrom = 0;
                    tryResume = false;
                    continue;
                }
                if (response.StatusCode != HttpStatusCode.PartialContent)
                {
                    // A 200 (or any non-206 success) ignores the Range
                    // header; restart from zero.
                    resumeFrom = 0;
                    tryResume = false;
                }
            }

            var append = tryResume && response.StatusCode == HttpStatusCode.PartialContent;
            var total = append ? response.Content.Headers.ContentLength + resumeFrom : response.Content.Headers.ContentLength;

            await using var source = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
            await using var target = new FileStream(partFile, append ? FileMode.Append : FileMode.Create);
            var received = resumeFrom;
            var resumed = append ? resumeFrom : 0;
            var buffer = new byte[1 << 16];
            while (true)
            {
                int read;
                // One idle window per read: no byte inside it declares this
                // attempt stalled. The failure is an ordinary attempt failure —
                // the chain moves to the next source and the kept ".part"
                // resumes there — while the user's own cancel must keep
                // surfacing as OperationCanceledException.
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                idle.CancelAfter(_bodyIdleTimeout);
                try
                {
                    read = await source.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                {
                    throw new HttpRequestException(
                        $"The response body stalled: no bytes within {_bodyIdleTimeout.TotalSeconds:0}s.", default);
                }
                if (read == 0) break;
                await target.WriteAsync(buffer.AsMemory(0, read), cancellation).ConfigureAwait(false);
                received += read;
                progress?.Report(new DownloadProgress(received, total, url, resumed));
            }
            progress?.Report(new DownloadProgress(received, total, url, resumed));

            File.Move(partFile, destinationFile, overwrite: true);
            return new DownloadResult(destinationFile, url);
        }
    }

    /// <summary>
    /// True when the 206 really continues at <paramref name="offset"/>:
    /// "bytes 123-456/789" with 123 == offset. A missing header, another
    /// unit, or the unsatisfied "*/*" shape is unverifiable and must not be
    /// trusted for an append.
    /// </summary>
    private static bool ContentRangeStartsAt(HttpResponseMessage response, long offset) =>
        response.Content.Headers.ContentRange is { Unit: "bytes", From: long from } && from == offset;

    private static SocketsHttpHandler BuildSocketsHandler(DownloadProxyMode mode, IWebProxy? proxy)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };
        switch (mode)
        {
            case DownloadProxyMode.PlatformDefault:
                handler.UseProxy = true;
                handler.Proxy = HttpClient.DefaultProxy;
                break;
            case DownloadProxyMode.Explicit:
                handler.UseProxy = true;
                handler.Proxy = proxy;
                break;
            case DownloadProxyMode.Direct:
                handler.UseProxy = false;
                break;
        }
        return handler;
    }

    public async Task<string?> FetchChecksumAsync(string assetName, CancellationToken cancellation)
    {
        foreach (var url in ReleaseAssets.SourceUrls(assetName + ".sha256"))
        {
            try
            {
                using var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(5) };
                using var client = new HttpClient(handler);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("stupiddict");
                using var firstByte = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                firstByte.CancelAfter(ChecksumTimeout);
                var text = await client.GetStringAsync(url, firstByte.Token).ConfigureAwait(false);
                var hex = ParseChecksum(text);
                if (hex is not null) return hex;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // no checksum available here; the next source may have one
            }
        }
        return null;
    }

    /// <summary>
    /// Parses a published "&lt;asset&gt;.sha256" payload into its 64-digit
    /// hex digest, lowercased. The payload has no single standard, so the
    /// common shapes must all read: "&lt;hex&gt;  &lt;name&gt;" (sha256sum,
    /// one or two spaces), "&lt;hex&gt;\t*&lt;name&gt;" (BSD sha256sum -t,
    /// where the tab is inside the first token and a Trim cannot reach it),
    /// "SHA256(&lt;file&gt;)= &lt;hex&gt;" (openssl dgst), any letter case,
    /// CRLF or LF, stray leading blank lines. The scan takes the first
    /// standalone 64-hex-digit run on the first non-empty line — word
    /// boundaries keep it from cutting a longer hex run — and only the first
    /// line, because a multi-line sha256sums.txt lists OTHER files whose
    /// digests must never stand in for this asset's. No match means the
    /// format is not recognized: null, which the caller treats exactly like
    /// "no checksum published" (extraction's entry CRC stands in). Pure
    /// function; see AssetDownloadServiceTests for the format table.
    /// </summary>
    internal static string? ParseChecksum(string text)
    {
        var firstContentLine = text.Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .FirstOrDefault(line => line.Trim().Length > 0);
        if (firstContentLine is null) return null;
        var match = Hex64().Match(firstContentLine);
        return match.Success ? match.Value.ToLowerInvariant() : null;
    }

    [GeneratedRegex(@"\b[0-9a-fA-F]{64}\b")]
    private static partial Regex Hex64();

    /// <summary>Throws when the file's SHA-256 differs from the published checksum.</summary>
    public static void VerifyChecksum(string filePath, string expectedHex)
    {
        using var stream = File.OpenRead(filePath);
        var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (actual != expectedHex.ToLowerInvariant())
            throw new ChecksumMismatchException(Translations.Instance.ChecksumFailed);
    }
}
