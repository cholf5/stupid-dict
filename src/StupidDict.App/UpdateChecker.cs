using System.Net;
using StupidDict.App.Assets;

namespace StupidDict.App;

/// <summary>The outcome of one update check.</summary>
public enum UpdateCheckOutcome
{
    /// <summary>The local version is not behind the latest release.</summary>
    UpToDate,

    /// <summary>A newer release exists (LatestVersion/ReleaseUrl are set).</summary>
    UpdateAvailable,

    /// <summary>Network or parsing failure (reason in Error: offline, blocked, or unrecognized version).</summary>
    Failed,
}

/// <summary>Failure classification for Failed results: HTTP status gets a friendly short form, the rest show raw diagnostics.</summary>
public enum UpdateCheckErrorKind
{
    None,

    /// <summary>The server answered with a non-success status (Error = "HTTP 403" style short diagnosis).</summary>
    HttpStatus,

    /// <summary>Network-level failure (offline/DNS/timeout, Error = the raw exception message).</summary>
    Network,

    /// <summary>Connected but the response carries no recognizable version (proxy interception page, endpoint change).</summary>
    InvalidResponse,
}

/// <summary>Update check result: LatestVersion/ReleaseUrl are set on UpdateAvailable, Error/ErrorKind on Failed.</summary>
public sealed record UpdateCheckResult(
    UpdateCheckOutcome Outcome,
    string? LatestVersion = null,
    string? ReleaseUrl = null,
    string? Error = null,
    UpdateCheckErrorKind ErrorKind = UpdateCheckErrorKind.None);

/// <summary>
/// Update check: requests the GitHub web-side latest-release redirect and
/// compares it against the local assembly version (v prefix stripped,
/// three numeric segments). Deliberately not api.github.com — unauthenticated
/// API calls are rate-limited to 60/hour per IP, so shared proxy/CGNAT exits
/// almost always get 403; the web endpoint releases/latest answers 302 and the
/// Location header already contains the release URL, so the tag can be read
/// without following the redirect and downloading the whole page. No custom
/// proxy pipeline: HttpClientHandler picks up https_proxy etc. from the
/// environment, matching the asset downloader's proxy behavior.
/// </summary>
public sealed class UpdateChecker
{
    /// <summary>The latest-release redirect endpoint (GitHub web page, no API rate limit).</summary>
    public const string LatestReleaseUrl = $"https://github.com/{ReleaseAssets.Repository}/releases/latest";

    /// <summary>The releases list page — the escape hatch when a check fails.</summary>
    public const string ReleasesPageUrl = $"https://github.com/{ReleaseAssets.Repository}/releases";

    /// <summary>The current app version (v prefix + three segments), same source as the About card: the assembly version.</summary>
    public static string CurrentVersion { get; } =
        "v" + (typeof(UpdateChecker).Assembly.GetName().Version is { } version
            ? version.ToString(3)
            : "0.0.0");

    /// <summary>Base for completing relative Location headers (CheckAsync always requests this address).</summary>
    private static readonly Uri LatestReleaseBaseUri = new(LatestReleaseUrl, UriKind.Absolute);

    private readonly HttpMessageHandler? _handler;
    private readonly string _currentVersion;

    /// <summary>The handler exists for tests to inject fake responses; currentVersion overrides the comparison base so the User-Agent and UpToDate status line stay consistent with the injected baseline.</summary>
    public UpdateChecker(HttpMessageHandler? handler = null, string? currentVersion = null)
    {
        _handler = handler;
        _currentVersion = currentVersion ?? CurrentVersion;
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var client = CreateClient();
            using var response = await client.GetAsync(LatestReleaseUrl, ct);
            var location = response.Headers.Location is { IsAbsoluteUri: false } relative
                ? new Uri(LatestReleaseBaseUri, relative) // relative Location completes against the request address
                : response.Headers.Location;
            if (location is null)
            {
                response.EnsureSuccessStatusCode(); // error statuses become HttpStatus-kind failures
                return new UpdateCheckResult(UpdateCheckOutcome.Failed,
                    Error: "no release redirect", ErrorKind: UpdateCheckErrorKind.InvalidResponse);
            }
            return Evaluate(location.ToString(), _currentVersion);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            // Everything becomes a result instead of an exception for the UI:
            // offline/timeout/HTTP errors land in Failed with a message.
            return ToFailed(e);
        }
    }

    /// <summary>HTTP status failures collapse to a short diagnosis ("HTTP 403"); network failures keep the raw exception message.</summary>
    private static UpdateCheckResult ToFailed(Exception e) =>
        e is HttpRequestException { StatusCode: { } status }
            ? new UpdateCheckResult(UpdateCheckOutcome.Failed,
                Error: $"HTTP {(int)status}", ErrorKind: UpdateCheckErrorKind.HttpStatus)
            : new UpdateCheckResult(UpdateCheckOutcome.Failed,
                Error: e.Message, ErrorKind: UpdateCheckErrorKind.Network);

    private HttpClient CreateClient()
    {
        // No auto-redirect: the 302 Location is all we need; following it would
        // download the whole page for nothing. Injected test handlers already
        // do not follow.
        var client = _handler is null
            ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            : new HttpClient(_handler, disposeHandler: false);
        client.Timeout = TimeSpan.FromSeconds(15);
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"stupiddict-desktop/{_currentVersion.TrimStart('v')}");
        return client;
    }

    /// <summary>This instance's comparison base (v prefix, three segments); the UpToDate status line shows it rather than the static assembly version so injected baselines stay self-consistent in tests.</summary>
    public string Version => _currentVersion;

    /// <summary>Extracts the tag from a release URL and compares it with the local version. Internal for tests.</summary>
    internal static UpdateCheckResult Evaluate(string? releaseUrl, string currentVersion)
    {
        var tag = ExtractTag(releaseUrl);
        if (!TryParseVersion(tag, out var latest))
            return new UpdateCheckResult(UpdateCheckOutcome.Failed,
                Error: tag ?? "no release redirect", ErrorKind: UpdateCheckErrorKind.InvalidResponse);

        TryParseVersion(currentVersion, out var current); // unparsable local version counts as 0.0.0: any release is newer
        var outcome = Compare(latest, current) > 0 ? UpdateCheckOutcome.UpdateAvailable : UpdateCheckOutcome.UpToDate;
        return new UpdateCheckResult(outcome, LatestVersion: tag, ReleaseUrl: releaseUrl);
    }

    /// <summary>The last URL path segment (…/releases/tag/&lt;tag&gt; → &lt;tag&gt;); non-URLs pass through for diagnostics.</summary>
    internal static string? ExtractTag(string? releaseUrl)
    {
        if (string.IsNullOrWhiteSpace(releaseUrl)) return null;
        if (!Uri.TryCreate(releaseUrl, UriKind.Absolute, out var uri)) return releaseUrl;
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var last = segments.LastOrDefault(s => s.Length > 0);
        return last is null ? null : Uri.UnescapeDataString(last);
    }

    /// <summary>Parses "v1.2.3" into three numeric segments: v/V prefix optional, missing segments count as 0, non-numeric suffixes (like -beta) ignored.</summary>
    internal static bool TryParseVersion(string? tag, out (int Major, int Minor, int Patch) version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(tag)) return false;
        var parts = tag.TrimStart('v', 'V').Split('.');
        if (parts.Length == 0 || !StartsWithDigits(parts[0], out var major)) return false;
        var minor = parts.Length > 1 && StartsWithDigits(parts[1], out var m) ? m : 0;
        var patch = parts.Length > 2 && StartsWithDigits(parts[2], out var p) ? p : 0;
        version = (major, minor, patch);
        return true;

        static bool StartsWithDigits(string segment, out int value)
        {
            value = 0;
            var digits = segment.TakeWhile(char.IsDigit).ToArray();
            // Overlong digit runs count as unrecognized so int.Parse cannot overflow.
            if (digits.Length == 0 || digits.Length > 9) return false;
            value = int.Parse(digits);
            return true;
        }
    }

    private static int Compare((int Major, int Minor, int Patch) a, (int Major, int Minor, int Patch) b) =>
        (a.Major, a.Minor, a.Patch).CompareTo((b.Major, b.Minor, b.Patch));
}
