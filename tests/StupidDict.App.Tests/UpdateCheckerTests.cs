using System.Net;
using StupidDict.App;
using Xunit;

namespace StupidDict.App.Tests;

/// <summary>
/// Version comparison, release-redirect parsing and network-failure handling
/// of the update checker (fake handler, never touches the real network).
/// </summary>
public class UpdateCheckerTests
{
    /// <summary>The release page URL a releases/latest 302 points at (the Location header; the tag is the last path segment).</summary>
    internal const string LatestReleaseUrl = "https://github.com/cholf5/stupid-dict/releases/tag/v1.2.3";

    // ---- Evaluate (tag extraction + comparison) ----

    [Fact]
    public void Evaluate_HigherLatest_FindsUpdate()
    {
        var result = UpdateChecker.Evaluate(LatestReleaseUrl, "v1.0.0");

        Assert.Equal(UpdateCheckOutcome.UpdateAvailable, result.Outcome);
        Assert.Equal("v1.2.3", result.LatestVersion);
        Assert.Equal(LatestReleaseUrl, result.ReleaseUrl);
    }

    [Fact]
    public void Evaluate_LocalNotBehind_UpToDate()
    {
        Assert.Equal(UpdateCheckOutcome.UpToDate, UpdateChecker.Evaluate(LatestReleaseUrl, "v1.2.3").Outcome);
        Assert.Equal(UpdateCheckOutcome.UpToDate, UpdateChecker.Evaluate(LatestReleaseUrl, "v9.9.9").Outcome);
    }

    [Theory]
    [InlineData("v1.0.10", "v1.0.9")] // numeric comparison, not lexicographic
    [InlineData("1.2", "v1.1.9")] // v prefix optional, missing segments count as 0
    [InlineData("v2.0.0-beta", "v1.9.9")] // trailing non-numeric suffixes are ignored
    public void Evaluate_VariedTagShapes_StillDetectUpdate(string tag, string current)
    {
        var result = UpdateChecker.Evaluate($"https://github.com/cholf5/stupid-dict/releases/tag/{tag}", current);

        Assert.Equal(UpdateCheckOutcome.UpdateAvailable, result.Outcome);
        Assert.Equal(tag, result.LatestVersion);
    }

    [Fact]
    public void Evaluate_UnrecognizedTag_FailsWithRawText()
    {
        var result = UpdateChecker.Evaluate("https://github.com/cholf5/stupid-dict/releases/tag/nightly-2024", "v1.0.0");

        Assert.Equal(UpdateCheckOutcome.Failed, result.Outcome);
        Assert.Equal("nightly-2024", result.Error);
        Assert.Equal(UpdateCheckErrorKind.InvalidResponse, result.ErrorKind);
    }

    [Fact]
    public void Evaluate_NoAddress_Fails()
    {
        var result = UpdateChecker.Evaluate(null, "v1.0.0");

        Assert.Equal(UpdateCheckOutcome.Failed, result.Outcome);
        Assert.Equal(UpdateCheckErrorKind.InvalidResponse, result.ErrorKind);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("v", false)]
    [InlineData("abc", false)]
    [InlineData("v1.2.3", true)]
    [InlineData("1.2", true)]
    public void TryParseVersion_ShapeRecognition(string? tag, bool expected)
    {
        Assert.Equal(expected, UpdateChecker.TryParseVersion(tag, out _));
    }

    // ---- CheckAsync (HTTP path with a fake handler) ----

    [Fact]
    public async Task CheckAsync_SendsUserAgentAndCorrectUrl()
    {
        Uri? requested = null;
        string? userAgent = null;
        var checker = new UpdateChecker(new FakeHandler(request =>
        {
            requested = request.RequestUri;
            userAgent = request.Headers.UserAgent.ToString();
            return RedirectResponse(LatestReleaseUrl);
        }), currentVersion: "v1.0.0");

        var result = await checker.CheckAsync();

        Assert.Equal(new Uri(UpdateChecker.LatestReleaseUrl), requested);
        // The User-Agent lowers the chance of GitHub's anti-abuse interception.
        Assert.Equal("stupiddict-desktop/1.0.0", userAgent);
        Assert.Equal(UpdateCheckOutcome.UpdateAvailable, result.Outcome);
    }

    [Fact]
    public async Task CheckAsync_RelativeLocation_CompletesAgainstRequestAddress()
    {
        var checker = new UpdateChecker(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Found)
        {
            Headers = { Location = new Uri("/cholf5/stupid-dict/releases/tag/v1.2.3", UriKind.Relative) },
        }), currentVersion: "v1.0.0");

        var result = await checker.CheckAsync();

        Assert.Equal(UpdateCheckOutcome.UpdateAvailable, result.Outcome);
        Assert.Equal(LatestReleaseUrl, result.ReleaseUrl);
    }

    [Fact]
    public async Task CheckAsync_NetworkFailure_BecomesFailedWithoutThrowing()
    {
        var checker = new UpdateChecker(
            new FakeHandler(_ => throw new HttpRequestException("offline")), currentVersion: "v1.0.0");

        var result = await checker.CheckAsync();

        Assert.Equal(UpdateCheckOutcome.Failed, result.Outcome);
        Assert.Equal("offline", result.Error);
        Assert.Equal(UpdateCheckErrorKind.Network, result.ErrorKind);
    }

    [Fact]
    public async Task CheckAsync_HttpError_CollapsesToShortDiagnosis()
    {
        var checker = new UpdateChecker(
            new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)), currentVersion: "v1.0.0");

        var result = await checker.CheckAsync();

        Assert.Equal(UpdateCheckOutcome.Failed, result.Outcome);
        Assert.Equal("HTTP 403", result.Error);
        Assert.Equal(UpdateCheckErrorKind.HttpStatus, result.ErrorKind);
    }

    [Fact]
    public async Task CheckAsync_SuccessWithoutRedirect_FailsAsInvalidResponse()
    {
        var checker = new UpdateChecker(
            new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)), currentVersion: "v1.0.0");

        var result = await checker.CheckAsync();

        Assert.Equal(UpdateCheckOutcome.Failed, result.Outcome);
        Assert.Equal(UpdateCheckErrorKind.InvalidResponse, result.ErrorKind);
    }

    internal static HttpResponseMessage RedirectResponse(string location) => new(HttpStatusCode.Found)
    {
        Headers = { Location = new Uri(location) },
    };
}

/// <summary>Synchronous fake HTTP handler returning a canned response per request; internal so headless UI tests can reuse it.</summary>
internal sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(respond(request));
}
