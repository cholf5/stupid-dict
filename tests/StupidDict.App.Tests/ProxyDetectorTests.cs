using System.Net;
using StupidDict.App.Assets;
using Xunit;

namespace StupidDict.App.Tests;

public class ProxyDetectorTests
{
    // Expected addresses are canonical Uri.ToString() forms (trailing slash,
    // lowercased host).
    [Theory]
    [InlineData("127.0.0.1:7890", "http://127.0.0.1:7890/")]
    [InlineData(" localhost : 7890 ", "http://localhost:7890/")]
    [InlineData("http=10.0.0.1:8080;https=10.0.0.1:7890;ftp=10.0.0.1:21", "http://10.0.0.1:7890/")]
    [InlineData("http=127.0.0.1:8080;https=127.0.0.1:7890;socks=127.0.0.1:7891", "http://127.0.0.1:7890/")]
    [InlineData("https=http://127.0.0.1:7890", "http://127.0.0.1:7890/")]
    [InlineData("HTTPS=LOCALHOST:10809", "http://localhost:10809/")]
    [InlineData("http=127.0.0.1:8080;socks=127.0.0.1:7891", "http://127.0.0.1:8080/")]
    [InlineData("socks=127.0.0.1:7891", "socks5://127.0.0.1:7891/")]
    [InlineData("http=127.0.0.1:8080; https=127.0.0.1:7890 ;", "http://127.0.0.1:7890/")]
    public void ParsesWininetProxyServerValues(string server, string expectedAddress)
    {
        var proxy = Assert.IsType<WebProxy>(ProxyDetector.ParseWindowsProxyServer(server));
        Assert.Equal(expectedAddress, proxy.Address!.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("no-port-here")]
    [InlineData("host:notaport")]
    [InlineData("host:0")]
    [InlineData("host:70000")]
    [InlineData(":7890")]
    [InlineData("host:")]
    [InlineData("ftp=127.0.0.1:21")]
    [InlineData(";;")]
    public void RejectsUnusableWininetProxyServerValues(string server)
    {
        Assert.Null(ProxyDetector.ParseWindowsProxyServer(server));
    }

    [Fact]
    public void BuildAttemptsEscalatesPlatformDefaultThenDirectThenDetected()
    {
        // The production URL shape (DataTag-constructed, not a baked tag literal:
        // the tag bumps with data releases and this test only pins the
        // escalation order over whatever URL it is given).
        var githubUrl = ReleaseAssets.GithubUrl(ReleaseAssets.DictionaryAsset);
        var attempts = new AssetDownloadService().BuildAttempts(githubUrl).ToList();

        // Phase 1: the platform default rides every source, GitHub first —
        // the exact route the user's browser takes.
        var platform = attempts.Take(4).ToList();
        Assert.All(platform, a => Assert.Equal(DownloadProxyMode.PlatformDefault, a.Mode));
        Assert.Equal(githubUrl, platform[0].Url);
        Assert.Equal(4, platform.Select(a => a.Url).Distinct().Count());

        // Phase 2: bypass every proxy (dead/stale system proxy must not sink
        // the mirrors), and the unreachable-from-CN GitHub direct is skipped.
        var expectedMirrors = ReleaseAssets.MirrorUrls(githubUrl).Skip(1).ToList();
        var direct = attempts.Skip(4).Where(a => a.Mode == DownloadProxyMode.Direct).ToList();
        Assert.Equal(expectedMirrors, direct.Select(a => a.Url).ToList());

        // Everything after the direct phase rides explicitly detected proxies.
        Assert.All(attempts.Skip(7), a => Assert.Equal(DownloadProxyMode.Explicit, a.Mode));
        Assert.All(attempts.Skip(7), a => Assert.NotNull(a.Proxy));
    }
}
