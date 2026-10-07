using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace StupidDict.App.Assets;

/// <summary>What stage a download reached, for the progress UI.</summary>
public sealed record DownloadProgress(long ReceivedBytes, long? TotalBytes, string SourceUrl);

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

/// <summary>
/// Downloads GitHub release assets with the fallback chain the product needs
/// for CN networks: direct → mirror prefixes → detected proxies → probed
/// local proxy ports. Each attempt resumes an interrupted ".part" file via
/// HTTP Range. This is asset bootstrapping only — dictionary lookups stay
/// fully offline.
/// </summary>
public sealed class AssetDownloadService : IAssetDownloader
{
    private static readonly TimeSpan FirstByteTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ChecksumTimeout = TimeSpan.FromSeconds(10);

    public async Task<DownloadResult> DownloadAsync(string assetName, string destinationFile,
        IProgress<DownloadProgress>? progress, CancellationToken cancellation)
    {
        var githubUrl = ReleaseAssets.GithubUrl(assetName);
        foreach (var (url, proxy) in BuildAttempts(githubUrl))
        {
            try
            {
                return await DownloadFromAsync(url, proxy, destinationFile, progress, cancellation);
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
        throw new InvalidOperationException("所有下载源都失败了。请检查网络，或手动下载后导入。");
    }

    /// <summary>
    /// Attempts in escalation order. Proxy detection is cheap; probing local
    /// ports costs a few hundred milliseconds and only runs when the direct
    /// and mirror attempts are already lost.
    /// </summary>
    internal IEnumerable<(string Url, IWebProxy? Proxy)> BuildAttempts(string githubUrl)
    {
        var sources = ReleaseAssets.MirrorUrls(githubUrl).ToList();
        foreach (var url in sources)
            yield return (url, null);

        var detected = ProxyDetector.DetectFromEnvironmentAndSystem().ToList();
        foreach (var proxy in detected)
            foreach (var url in sources.Take(2))
                yield return (url, proxy);

        foreach (var proxy in ProxyDetector.ProbeCommonLocalPorts())
            foreach (var url in sources.Take(2))
                yield return (url, proxy);
    }

    private static async Task<DownloadResult> DownloadFromAsync(string url, IWebProxy? proxy, string destinationFile,
        IProgress<DownloadProgress>? progress, CancellationToken cancellation)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
        var partFile = destinationFile + ".part";
        var resumeFrom = File.Exists(partFile) ? new FileInfo(partFile).Length : 0;

        using var handler = new SocketsHttpHandler
        {
            UseProxy = proxy is not null,
            Proxy = proxy,
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };
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
        var buffer = new byte[1 << 16];
        int read;
        while ((read = await source.ReadAsync(buffer, cancellation)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellation);
            received += read;
            progress?.Report(new DownloadProgress(received, total, url));
        }
        progress?.Report(new DownloadProgress(received, total, url));

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
            throw new InvalidOperationException("下载文件校验失败，已删除损坏文件。");
    }
}
