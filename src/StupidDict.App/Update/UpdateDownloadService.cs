using System.Net.Http;

namespace StupidDict.App.Update;

/// <summary>
/// Downloads the update asset named by <see cref="UpdateAssets.PickAssetName"/>
/// (portable zip, or the Windows installer for an installed copy): GitHub
/// direct link plus the mirror prefixes, streamed to disk (.part → rename, so
/// an interrupted download never leaves a half file looking complete), and —
/// on the path self-update actually uses — SHA-256 verified before the file is
/// handed on.
/// </summary>
internal sealed class UpdateDownloadService
{
    /// <summary>Temp subdirectory holding update packages until they are applied.</summary>
    internal const string DirectoryName = "stupiddict-updates";

    private readonly HttpMessageHandler? _handler;
    private readonly string _downloadDirectory;

    /// <summary>handler for tests to inject fake responses; downloadDirectory overrides the landing directory.</summary>
    internal UpdateDownloadService(HttpMessageHandler? handler = null, string? downloadDirectory = null)
    {
        _handler = handler;
        _downloadDirectory = downloadDirectory ?? Path.Combine(Path.GetTempPath(), DirectoryName);
    }

    internal string DownloadDirectory => _downloadDirectory;

    /// <summary>
    /// Downloads <paramref name="assetName"/> and returns the full path.
    /// The mirror list is tried in order: the next URL is only tried when the
    /// current attempt fails (connect/HTTP/timeout); any partial .part file is
    /// removed between attempts. Throws <see cref="InvalidOperationException"/>
    /// with the aggregated failures when every mirror failed.
    /// </summary>
    internal async Task<string> DownloadAsync(string latestTag, string assetName,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var targetPath = Path.Combine(_downloadDirectory, assetName);
        var partPath = targetPath + ".part";
        Directory.CreateDirectory(_downloadDirectory);

        var failures = new List<string>();
        foreach (var url in UpdateAssets.MirrorUrls(UpdateAssets.AssetDownloadUrl(latestTag, assetName)))
        {
            try
            {
                await DownloadToAsync(new Uri(url), partPath, targetPath, assetName, progress, ct);
                return targetPath;
            }
            // Only a caller-side cancellation ends the chain. HttpClient.Timeout
            // throws TaskCanceledException (an OperationCanceledException) with
            // ct untouched — that is a slow mirror, and abandoning the chain
            // there would make all three CN prefixes useless exactly when they
            // matter.
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                TryCleanup(partPath);
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException
                                          or OperationCanceledException)
            {
                TryCleanup(partPath);
                failures.Add($"{new Uri(url).Host}: {ex.Message}");
            }
        }

        throw new InvalidOperationException(string.Join("；", failures));
    }

    private async Task DownloadToAsync(Uri url, string partPath, string targetPath, string assetName,
        IProgress<double>? progress, CancellationToken ct)
    {
        using var client = CreateClient();
        // ResponseHeadersRead: stream and report progress while writing instead
        // of buffering a multi-megabyte package in memory.
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;

        long written;
        await using (var source = await response.Content.ReadAsStreamAsync(ct))
        await using (var output = File.Create(partPath))
        {
            var buffer = new byte[81920];
            written = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                written += read;
                if (total is > 0)
                {
                    progress?.Report(written * 100.0 / total.Value);
                }
            }
        }

        // Completeness: exactly as many bytes as announced (chunked truncation
        // or a proxy dropping the connection would otherwise pass).
        if (total is > 0 && written != total.Value)
        {
            throw new IOException($"incomplete body: {written}/{total.Value} bytes");
        }

        // Rate-limited mirrors often answer 200 with an HTML error page;
        // without this check that page would be renamed into place as the
        // "package" and could even be revealed in a file manager.
        EnsurePayloadLooksRight(partPath, assetName);

        File.Move(partPath, targetPath, overwrite: true);
    }

    /// <summary>
    /// Download + verify in one entry point (the only path self-update uses):
    /// fetch <c>&lt;asset&gt;.sha256</c>, download the asset, and delete the
    /// landing file when the hash does not match.
    /// </summary>
    internal async Task<string> DownloadVerifiedAsync(string latestTag, string assetName,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var expected = UpdateChecksum.TryParse(
            await DownloadTextAsync(latestTag, UpdateAssets.ChecksumAssetFileName(assetName), ct), out var hex)
            ? hex
            : throw new InvalidDataException($"no usable sha256 published for {assetName}");

        var path = await DownloadAsync(latestTag, assetName, progress, ct);
        try
        {
            UpdateChecksum.Verify(path, expected, assetName);
        }
        catch
        {
            TryDelete(path);
            throw;
        }

        progress?.Report(100);
        return path;
    }

    /// <summary>Fetches a small text asset (the checksum sidecar): mirrors in order, body capped at 4 KB.</summary>
    private async Task<string> DownloadTextAsync(string latestTag, string assetName, CancellationToken ct)
    {
        var failures = new List<string>();
        foreach (var url in UpdateAssets.MirrorUrls(UpdateAssets.AssetDownloadUrl(latestTag, assetName)))
        {
            try
            {
                using var client = CreateClient();
                using var response = await client.GetAsync(url, ct);
                response.EnsureSuccessStatusCode();
                var bytes = await response.Content.ReadAsByteArrayAsync(ct);
                if (bytes.Length is 0 or > 4096)
                {
                    throw new InvalidDataException($"unexpected checksum payload ({bytes.Length} bytes)");
                }

                return System.Text.Encoding.UTF8.GetString(bytes);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException
                                          or OperationCanceledException)
            {
                failures.Add($"{new Uri(url).Host}: {ex.Message}");
            }
        }

        throw new InvalidOperationException(string.Join("；", failures));
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[StupidDict] 删除校验失败的更新包失败（{path}）：{ex.Message}");
        }
    }

    /// <summary>
    /// Payload magic by asset type: zip → ZIP local-file header (PK\x03\x04),
    /// installer exe → PE header (MZ). Both are binary; an HTML error page from
    /// a mirror matches neither, and neither does an empty file.
    /// </summary>
    private static void EnsurePayloadLooksRight(string partPath, string assetName)
    {
        var expectsExecutable = assetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        Span<byte> magic = stackalloc byte[4];
        int read;
        using (var stream = File.OpenRead(partPath))
        {
            read = stream.ReadAtLeast(magic, magic.Length, throwOnEndOfStream: false);
        }

        var looksRight = expectsExecutable
            ? read >= 2 && magic[0] == 0x4D && magic[1] == 0x5A
            : read >= magic.Length
              && magic[0] == 0x50 && magic[1] == 0x4B && magic[2] == 0x03 && magic[3] == 0x04;

        if (!looksRight)
        {
            throw new InvalidDataException(expectsExecutable
                ? "payload is not a PE executable"
                : "payload is not a zip archive");
        }
    }

    private HttpClient CreateClient()
    {
        // release download 302 → CDN object, so redirects must be followed
        // here (the opposite of the update checker, which reads the 302).
        // The 10-minute timeout covers the whole request: the default 100 s
        // would cut a slow download mid-flight.
        var client = _handler is null
            ? new HttpClient()
            : new HttpClient(_handler, disposeHandler: false);
        client.Timeout = TimeSpan.FromMinutes(10);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"stupiddict-desktop/{UpdateChecker.CurrentVersion.TrimStart('v')}");
        return client;
    }

    private static void TryCleanup(string partPath)
    {
        try
        {
            File.Delete(partPath);
        }
        catch (IOException)
        {
            // A leftover half file is harmless (the next attempt overwrites it);
            // a failed delete must not mask the original exception.
        }
    }
}
