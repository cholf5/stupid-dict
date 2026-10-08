using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
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
public sealed class AssetDownloadService : IAssetDownloader
{
    private static readonly TimeSpan FirstByteTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ChecksumTimeout = TimeSpan.FromSeconds(10);

    public async Task<DownloadResult> DownloadAsync(string assetName, string destinationFile,
        IProgress<DownloadProgress>? progress, CancellationToken cancellation)
    {
        var githubUrl = ReleaseAssets.GithubUrl(assetName);
        foreach (var (url, mode, proxy) in BuildAttempts(githubUrl))
        {
            try
            {
                return await DownloadFromAsync(url, mode, proxy, destinationFile, progress, cancellation);
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

    private static async Task<DownloadResult> DownloadFromAsync(string url, DownloadProxyMode mode, IWebProxy? proxy,
        string destinationFile, IProgress<DownloadProgress>? progress, CancellationToken cancellation)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
        var partFile = destinationFile + ".part";
        var resumeFrom = File.Exists(partFile) ? new FileInfo(partFile).Length : 0;

        using var handler = new SocketsHttpHandler
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
        using var client = new HttpClient(handler);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("stupiddict");

        using var firstByte = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        firstByte.CancelAfter(FirstByteTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (resumeFrom > 0)
            request.Headers.Range = new RangeHeaderValue(resumeFrom, null);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, firstByte.Token);

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            File.Delete(partFile);
            throw new HttpRequestException("Range not satisfiable", null, response.StatusCode);
        }
        response.EnsureSuccessStatusCode();

        // A 200 answer ignores the Range header; restart from zero.
        var append = response.StatusCode == HttpStatusCode.PartialContent && resumeFrom > 0;
        if (!append) resumeFrom = 0;
        var total = append ? response.Content.Headers.ContentLength + resumeFrom : response.Content.Headers.ContentLength;

        await using var source = await response.Content.ReadAsStreamAsync(cancellation);
        await using var target = new FileStream(partFile, append ? FileMode.Append : FileMode.Create);
        var received = resumeFrom;
        var resumed = append ? resumeFrom : 0;
        var buffer = new byte[1 << 16];
        int read;
        while ((read = await source.ReadAsync(buffer, cancellation)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellation);
            received += read;
            progress?.Report(new DownloadProgress(received, total, url, resumed));
        }
        progress?.Report(new DownloadProgress(received, total, url, resumed));

        File.Move(partFile, destinationFile, overwrite: true);
        return new DownloadResult(destinationFile, url);
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
                var text = await client.GetStringAsync(url, firstByte.Token);
                var hex = text.Split(' ')[0].Trim();
                if (hex.Length == 64) return hex.ToLowerInvariant();
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

    /// <summary>Throws when the file's SHA-256 differs from the published checksum.</summary>
    public static void VerifyChecksum(string filePath, string expectedHex)
    {
        using var stream = File.OpenRead(filePath);
        var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (actual != expectedHex.ToLowerInvariant())
            throw new ChecksumMismatchException(Translations.Instance.ChecksumFailed);
    }
}
