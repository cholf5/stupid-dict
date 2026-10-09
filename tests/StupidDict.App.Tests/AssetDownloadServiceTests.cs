using System.Net;
using System.Net.Http.Headers;
using StupidDict.App.Assets;
using Xunit;

namespace StupidDict.App.Tests;

/// <summary>
/// The real <see cref="AssetDownloadService"/> transfer loop against a
/// scripted <see cref="HttpMessageHandler"/> (zero real network, the same
/// seam style as UpdateChecker's handler injection): body-stall idle timeout
/// semantics (B-004) and Range resume validation (B-006). The UI-level
/// download flow (purge-and-retry, kept-zip reuse) is DownloadFlowTests.
/// </summary>
public class AssetDownloadServiceTests
{
    // ---- B-004: a body that stops sending must time out into the chain ----

    /// <summary>
    /// TC-001: the server sends a prefix and then goes quiet (connection up,
    /// no bytes — a network switch or a dead proxy). The attempt must time
    /// out within the idle window and fall through to the next source, whose
    /// fresh response completes the download from the kept ".part" position.
    /// </summary>
    [Fact]
    public async Task StalledBodyTimesOutAndFallsThroughToNextSource()
    {
        var directory = NewScratchDirectory();
        var destination = Path.Combine(directory, "dictionary.zip");
        var tail = Enumerable.Range(0, 64).Select(i => (byte)(0x40 + i)).ToArray();
        var handler = new ScriptedHttpHandler((range, index) =>
        {
            if (index == 0)
            {
                Assert.Null(range);
                return Respond(HttpStatusCode.OK, new StalledStream([0x1, 0x2, 0x3, 0x4]));
            }
            // Second source: the .part (4 bytes) was kept, so the request
            // carries a Range header proving the resume story is intact.
            Assert.Equal("bytes=4-", range);
            return Respond(HttpStatusCode.OK, tail);
        });
        var service = new AssetDownloadService(() => handler, bodyIdleTimeout: TimeSpan.FromMilliseconds(200));

        var result = await service.DownloadAsync(ReleaseAssets.DictionaryAsset, destination,
            progress: null, cancellation: CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(tail, File.ReadAllBytes(result.FilePath));
        Assert.False(File.Exists(destination + ".part"));
    }

    /// <summary>
    /// TC-002: the user's cancel during a stalled body must surface as
    /// OperationCanceledException (TaskCanceledException is its subclass) —
    /// never converted into an attempt failure, and no further source tried.
    /// </summary>
    [Fact]
    public async Task UserCancellationDuringStalledBodySurfacesAsCancellation()
    {
        var directory = NewScratchDirectory();
        var destination = Path.Combine(directory, "dictionary.zip");
        var stalled = new StalledStream([0x1, 0x2, 0x3, 0x4]);
        var handler = new ScriptedHttpHandler((_, _) => Respond(HttpStatusCode.OK, stalled));
        var service = new AssetDownloadService(() => handler, bodyIdleTimeout: TimeSpan.FromHours(1));
        using var cancellation = new CancellationTokenSource();

        var download = service.DownloadAsync(ReleaseAssets.DictionaryAsset, destination,
            progress: null, cancellation.Token);
        // Wait until the body actually sits in the stall, so the cancel is
        // deterministic and lands inside the read, not on the headers.
        await stalled.ReachedStall.Task;

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
        Assert.Single(handler.Requests);
    }

    // ---- B-006: a 206 must prove it continues OUR bytes ----

    /// <summary>
    /// TC-001: a 206 whose Content-Range starts before the ".part" length
    /// (a source that lost the asset it mirrors, answering from its own byte
    /// zero) must not be appended — the stale ".part" is dropped and this
    /// source restarts from zero, so the product is one intact file.
    /// </summary>
    [Fact]
    public async Task MisalignedContentRangeDiscardsPartAndRestartsFromZero()
    {
        var directory = NewScratchDirectory();
        var destination = Path.Combine(directory, "dictionary.zip");
        await File.WriteAllBytesAsync(destination + ".part", [0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88]);
        var fresh = Enumerable.Range(0, 100).Select(i => (byte)i).ToArray();
        var handler = new ScriptedHttpHandler((range, index) =>
        {
            if (index == 0)
            {
                Assert.Equal("bytes=8-", range);
                // The weld trap: 206 status, but from the server's byte zero.
                var response = Respond(HttpStatusCode.PartialContent, fresh);
                response.Content.Headers.ContentRange =
                    new ContentRangeHeaderValue(0, fresh.Length - 1, fresh.Length);
                return response;
            }
            Assert.Null(range);
            return Respond(HttpStatusCode.OK, fresh);
        });
        var service = new AssetDownloadService(() => handler, bodyIdleTimeout: TimeSpan.FromSeconds(10));

        var result = await service.DownloadAsync(ReleaseAssets.DictionaryAsset, destination,
            progress: null, cancellation: CancellationToken.None);

        // From zero, not the welded 8 stale bytes + 100 fresh ones.
        Assert.Equal(fresh, File.ReadAllBytes(result.FilePath));
        Assert.Equal(2, handler.Requests.Count);
        Assert.False(File.Exists(destination + ".part"));
    }

    /// <summary>
    /// TC-002 / regression: a genuine 206 starting exactly at the ".part"
    /// length still appends — the resume path this validation exists to
    /// protect must not learn to distrust honest servers.
    /// </summary>
    [Fact]
    public async Task AlignedContentRangeStillAppendsPart()
    {
        var directory = NewScratchDirectory();
        var destination = Path.Combine(directory, "dictionary.zip");
        var partBytes = new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88 };
        await File.WriteAllBytesAsync(destination + ".part", partBytes);
        var tail = Enumerable.Range(0, 100).Select(i => (byte)(0x80 + i)).ToArray();
        var handler = new ScriptedHttpHandler((range, _) =>
        {
            Assert.Equal("bytes=8-", range);
            var response = Respond(HttpStatusCode.PartialContent, tail);
            response.Content.Headers.ContentRange =
                new ContentRangeHeaderValue(partBytes.Length, partBytes.Length + tail.Length - 1,
                    partBytes.Length + tail.Length);
            return response;
        });
        var service = new AssetDownloadService(() => handler, bodyIdleTimeout: TimeSpan.FromSeconds(10));

        var result = await service.DownloadAsync(ReleaseAssets.DictionaryAsset, destination,
            progress: null, cancellation: CancellationToken.None);

        Assert.Equal(partBytes.Concat(tail), File.ReadAllBytes(result.FilePath));
        Assert.Single(handler.Requests);
        Assert.False(File.Exists(destination + ".part"));
    }

    /// <summary>Regression: a 200 answer ignores the Range header and restarts from zero.</summary>
    [Fact]
    public async Task OkResponseIgnoresRangeAndRestartsFromZero()
    {
        var directory = NewScratchDirectory();
        var destination = Path.Combine(directory, "dictionary.zip");
        await File.WriteAllBytesAsync(destination + ".part", [0x11, 0x22, 0x33]);
        var full = Enumerable.Range(0, 50).Select(i => (byte)i).ToArray();
        var handler = new ScriptedHttpHandler((range, _) =>
        {
            Assert.Equal("bytes=3-", range);
            return Respond(HttpStatusCode.OK, full);
        });
        var service = new AssetDownloadService(() => handler, bodyIdleTimeout: TimeSpan.FromSeconds(10));

        var result = await service.DownloadAsync(ReleaseAssets.DictionaryAsset, destination,
            progress: null, cancellation: CancellationToken.None);

        Assert.Equal(full, File.ReadAllBytes(result.FilePath));
        Assert.Single(handler.Requests);
    }

    /// <summary>
    /// Regression: 416 keeps its semantics — the ".part" is deleted (it can
    /// never be resumed) and the attempt fails into the chain, which here
    /// exhausts every source into the all-sources failure.
    /// </summary>
    [Fact]
    public async Task RangeNotSatisfiableDeletesPartAndExhaustsChain()
    {
        var directory = NewScratchDirectory();
        var destination = Path.Combine(directory, "dictionary.zip");
        await File.WriteAllBytesAsync(destination + ".part", [0x11, 0x22, 0x33]);
        var handler = new ScriptedHttpHandler((_, _) =>
            Respond(HttpStatusCode.RequestedRangeNotSatisfiable, Array.Empty<byte>()));
        var service = new AssetDownloadService(() => handler, bodyIdleTimeout: TimeSpan.FromSeconds(10));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DownloadAsync(
            ReleaseAssets.DictionaryAsset, destination, progress: null, cancellation: CancellationToken.None));

        Assert.False(File.Exists(destination + ".part"));
        Assert.True(handler.Requests.Count > 1);
    }

    // ---- B-007: published checksum payloads come in several shapes ----

    private const string Hash64 = "5d41402abc4b2a76b9719d911017c592f5f9d3b3e9c9d43a6a56e9f0e9b1d2f3";

    [Theory]
    [InlineData("5d41402abc4b2a76b9719d911017c592f5f9d3b3e9c9d43a6a56e9f0e9b1d2f3  dictionary.zip")] // sha256sum, two spaces
    [InlineData("5d41402abc4b2a76b9719d911017c592f5f9d3b3e9c9d43a6a56e9f0e9b1d2f3 dictionary.zip")] // single space
    [InlineData("5D41402ABC4B2A76B9719D911017C592F5F9D3B3E9C9D43A6A56E9F0E9B1D2F3\tdictionary.zip\r\n")] // BSD -t: tab inside the token
    [InlineData("SHA256(dictionary.zip)= 5d41402abc4b2a76b9719d911017c592f5f9d3b3e9c9d43a6a56e9f0e9b1d2f3")] // openssl dgst
    [InlineData("5d41402abc4b2a76b9719d911017c592f5f9d3b3e9c9d43a6a56e9f0e9b1d2f3 *dictionary.zip")] // binary-mode marker
    [InlineData("\r\n5d41402abc4b2a76b9719d911017c592f5f9d3b3e9c9d43a6a56e9f0e9b1d2f3  dictionary.zip")] // stray leading blank line
    [InlineData("5d41402abc4b2a76b9719d911017c592f5f9d3b3e9c9d43a6a56e9f0e9b1d2f3")] // bare digest
    public void ParseChecksumReadsCommonPayloadShapes(string text)
    {
        Assert.Equal(Hash64, AssetDownloadService.ParseChecksum(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \r\n")]
    [InlineData("no checksum here")]
    [InlineData("5d41402abc4b2a76b9719d911017c592f5f9d3b3e9c9d43a6a56e9f0e9b1d2f  dictionary.zip")] // 63 hex
    [InlineData("5d41402abc4b2a76b9719d911017c592f5f9d3b3e9c9d43a6a56e9f0e9b1d2f33  dictionary.zip")] // 65 hex
    [InlineData("5d41402abc4b2a76b9719d911017c592f5f9d3b3e9c9d43a6a56e9f0e9b1d2f35d41402abc4b2a76b9719d911017c592")] // 128 hex
    [InlineData("SHA256(dictionary.zip)= 5d41402abc4b2a76b9719d911017c592f5f9d3b3e9c9d43a6a56e9f0e9b1d2zz")] // non-hex run
    [InlineData("g5d41402abc4b2a76b9719d911017c592f5f9d3b3e9c9d43a6a56e9f0e9b1d2f3  dictionary.zip")] // hex glued to a word
    [InlineData("not a checksum line\n5d41402abc4b2a76b9719d911017c592f5f9d3b3e9c9d43a6a56e9f0e9b1d2f3  other.zip")] // only the first line counts
    public void ParseChecksumReturnsNullForUnrecognizedPayloads(string text)
    {
        Assert.Null(AssetDownloadService.ParseChecksum(text));
    }

    /// <summary>Case-insensitive hex is normalized to lowercase, as before.</summary>
    [Fact]
    public void ParseChecksumLowersHexCase()
    {
        Assert.Equal(Hash64, AssetDownloadService.ParseChecksum(Hash64.ToUpperInvariant() + "  x.zip"));
    }

    // ---- helpers ----

    private static string NewScratchDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "stupiddict-servicetests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static HttpResponseMessage Respond(HttpStatusCode statusCode, Stream content)
    {
        var response = new HttpResponseMessage(statusCode) { Content = new StreamContent(content) };
        response.Headers.AcceptRanges.Add("bytes");
        return response;
    }

    private static HttpResponseMessage Respond(HttpStatusCode statusCode, byte[] bytes) =>
        Respond(statusCode, new MemoryStream(bytes));

    private sealed class ScriptedHttpHandler(Func<string?, int, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        /// <summary>One entry per request: the Range header (or null), copied
        /// out at send time because the service disposes requests.</summary>
        public List<string?> Requests { get; } = [];
        private int _count;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.Headers.Range?.ToString());
            var response = respond(Requests[^1], _count++);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    /// <summary>
    /// Serves a fixed prefix and then never returns: the read blocks on a
    /// token that only the idle timeout (or the user's cancel) fires, which
    /// is exactly how a half-dead connection behaves under SocketsHttpHandler.
    /// </summary>
    private sealed class StalledStream(byte[] prefix) : Stream
    {
        private int _position;

        public TaskCompletionSource ReachedStall { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position < prefix.Length)
            {
                var count = Math.Min(buffer.Length, prefix.Length - _position);
                prefix.AsSpan(_position, count).CopyTo(buffer.Span);
                _position += count;
                if (_position >= prefix.Length) ReachedStall.TrySetResult();
                return count;
            }
            ReachedStall.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0; // unreachable: the delay only ends by cancellation
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException("sync reads are not part of the download path");

        public override void Flush() { }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
