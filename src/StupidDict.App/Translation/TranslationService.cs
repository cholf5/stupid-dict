using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using StupidDict.App.Settings;

namespace StupidDict.App.Translation;

/// <summary>
/// OpenAI-compatible chat-completions client plus cache orchestration. It is
/// intentionally UI-free: failures are returned as values (except caller
/// cancellation), and the UI maps kinds to localized text.
/// </summary>
public sealed class TranslationService : ITranslationService
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    private static readonly string UserAgent =
        "stupiddict-desktop/" + (typeof(TranslationService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");

    // Default escaping would \u003C-encode the <text> wrapper; this body goes
    // out as application/json, never into an HTML context, so keep it literal
    // and readable.
    private static readonly JsonSerializerOptions RequestJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly AppSettings _settings;
    private readonly TranslationCache _cache;
    private readonly HttpMessageHandler? _testHandler;
    private readonly object _gate = new();
    private HttpClient? _proxied;
    private HttpClient? _direct;
    private HttpClient? _testClient;
    private long _cacheGeneration;
    private bool _autoPaused;

    public TranslationService(AppSettings settings, TranslationCache cache, HttpMessageHandler? testHandler = null)
    {
        _settings = settings;
        _cache = cache;
        _testHandler = testHandler;
    }

    public TranslationConfig GetConfig() => new(
        _settings.TranslationApiUrl.Trim(),
        _settings.TranslationApiKey,
        _settings.TranslationModel.Trim(),
        _settings.TranslationAutoOnMiss);

    public bool AutoPaused { get { lock (_gate) return _autoPaused; } }

    public void ResetCircuit() { lock (_gate) _autoPaused = false; }

    public TranslationResult? TryGetCached(string text)
    {
        if (!TryPrepare(text, out var config, out var endpoint, out var direction, out var key)) return null;
        if (!_cache.TryGet(key, out var cached)) return null;
        return TranslationResult.Success(cached, config.Model, fromCache: true, isLocal: endpoint!.IsLoopback);
    }

    public async Task<TranslationResult> TranslateAsync(
        string text, bool bypassCache, CancellationToken cancellationToken)
    {
        if (!TryPrepare(text, out var config, out var endpoint, out var direction, out var key))
            return TranslationResult.Fail(TranslationFailureKind.NotConfigured, canRetry: false);

        if (!bypassCache && _cache.TryGet(key, out var cached))
            return TranslationResult.Success(cached, config.Model, fromCache: true, isLocal: endpoint!.IsLoopback);

        var generation = Interlocked.Read(ref _cacheGeneration);
        var call = await SendCoreAsync(config, endpoint!, text, cancellationToken);
        if (!call.Success)
            return TranslationResult.Fail(call.Failure!.Value, canRetry: call.CanRetry, detail: call.Detail);

        var cleaned = TranslationPrompt.Format(TranslationPrompt.Clean(call.Text!));
        var problem = TranslationQualityGate.FindProblem(text, cleaned, direction);
        if (problem is { } kind)
            return TranslationResult.Fail(kind, canRetry: true, detail: "quality gate");

        if (generation == Interlocked.Read(ref _cacheGeneration))
            _cache.Put(key, cleaned);

        ResetCircuit();
        return TranslationResult.Success(cleaned, config.Model, fromCache: false, isLocal: endpoint!.IsLoopback);
    }

    public async Task<TranslationTestResult> TestAsync(
        TranslationConfig config, CancellationToken cancellationToken)
    {
        if (!config.IsConfigured)
            return new TranslationTestResult(false, Failure: TranslationFailureKind.NotConfigured);
        if (!TranslationEndpoint.TryCreate(config.ApiUrl, out var endpoint, out var endpointError))
            return new TranslationTestResult(false, Failure: TranslationFailureKind.InvalidConfig,
                Detail: endpointError?.ToString());

        var call = await SendCoreAsync(config, endpoint!, "Hello", cancellationToken);
        return call.Success
            ? new TranslationTestResult(true, config.Model)
            : new TranslationTestResult(false, Failure: call.Failure, Detail: call.Detail);
    }

    public void ClearCache()
    {
        Interlocked.Increment(ref _cacheGeneration);
        _cache.Clear();
    }

    public long GetCacheSizeBytes() => _cache.SizeBytes();

    private bool TryPrepare(
        string text, out TranslationConfig config, out TranslationEndpoint? endpoint,
        out TranslationDirection direction, out string key)
    {
        config = GetConfig();
        endpoint = null;
        direction = TranslationText.DetectDirection(text);
        key = "";
        if (!config.IsConfigured) return false;
        if (!TranslationEndpoint.TryCreate(config.ApiUrl, out endpoint, out _)) return false;

        var profile = string.Join('\n',
            endpoint!.ProfileKey, config.Model,
            TranslationPrompt.PromptVersion.ToString(),
            TranslationPrompt.FormatVersion.ToString(),
            direction.ToString());
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(profile + "\n" + TranslationText.NormalizeForCache(text)));
        key = Convert.ToHexString(hash).ToLowerInvariant();
        return true;
    }

    private async Task<ApiCall> SendCoreAsync(
        TranslationConfig config, TranslationEndpoint endpoint, string text, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(new
        {
            model = config.Model,
            messages = new object[]
            {
                new { role = "system", content = TranslationPrompt.System },
                new { role = "user", content = TranslationPrompt.User(text) },
            },
            temperature = 0.2,
            stream = false,
        }, RequestJsonOptions);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint.ChatCompletionsUri);
            request.Headers.UserAgent.ParseAdd(UserAgent);
            if (!string.IsNullOrWhiteSpace(config.ApiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey.Trim());
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

            using var response = await ClientFor(endpoint)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if ((int)response.StatusCode is >= 300 and < 400)
                return ApiCall.Fail(TranslationFailureKind.InvalidConfig, canRetry: false, "redirect");

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                lock (_gate) _autoPaused = true;
                return ApiCall.Fail(TranslationFailureKind.Authentication, canRetry: false,
                    $"HTTP {(int)response.StatusCode}");
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                lock (_gate) _autoPaused = true;
                return ApiCall.Fail(TranslationFailureKind.EndpointOrModel, canRetry: false, "HTTP 404");
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                return ApiCall.Fail(TranslationFailureKind.RateLimited, canRetry: true, "HTTP 429");

            if ((int)response.StatusCode >= 500)
                return ApiCall.Fail(TranslationFailureKind.Server, canRetry: true,
                    $"HTTP {(int)response.StatusCode}");

            if (!response.IsSuccessStatusCode)
                return ApiCall.Fail(TranslationFailureKind.InvalidResponse, canRetry: true,
                    $"HTTP {(int)response.StatusCode}");

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var content = ExtractContent(json);
            return content is null
                ? ApiCall.Fail(TranslationFailureKind.InvalidResponse, canRetry: true, "invalid response")
                : ApiCall.Ok(content);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            return ApiCall.Fail(TranslationFailureKind.Timeout, canRetry: true, "timeout");
        }
        catch (HttpRequestException ex)
        {
            return ApiCall.Fail(TranslationFailureKind.Network, canRetry: true, ex.Message);
        }
        catch (JsonException ex)
        {
            return ApiCall.Fail(TranslationFailureKind.InvalidResponse, canRetry: true, ex.Message);
        }
    }

    private static string? ExtractContent(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.TryGetProperty("error", out _)) return null;
        if (!root.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
            return null;

        var message = choices.EnumerateArray().First().GetProperty("message");
        if (!message.TryGetProperty("content", out var content)) return null;

        if (content.ValueKind == JsonValueKind.String)
            return content.GetString()?.Trim();

        if (content.ValueKind == JsonValueKind.Array)
        {
            var builder = new StringBuilder();
            foreach (var part in content.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                    builder.Append(text.GetString());
                else if (part.TryGetProperty("content", out var nested) && nested.ValueKind == JsonValueKind.String)
                    builder.Append(nested.GetString());
            }
            return builder.ToString().Trim();
        }

        return null;
    }

    private HttpClient ClientFor(TranslationEndpoint endpoint)
    {
        if (_testHandler is not null)
            return _testClient ??= new HttpClient(_testHandler, disposeHandler: false) { Timeout = RequestTimeout };

        if (endpoint.IsLoopback)
            return _direct ??= CreateClient(useProxy: false);
        return _proxied ??= CreateClient(useProxy: true);
    }

    private static HttpClient CreateClient(bool useProxy) =>
        new(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = ConnectTimeout,
            UseProxy = useProxy,
        })
        {
            Timeout = RequestTimeout,
        };

    private sealed record ApiCall(
        bool Success, string? Text, TranslationFailureKind? Failure, bool CanRetry, string? Detail)
    {
        public static ApiCall Ok(string text) => new(true, text, null, true, null);
        public static ApiCall Fail(TranslationFailureKind kind, bool canRetry, string? detail) =>
            new(false, null, kind, canRetry, detail);
    }

    public void Dispose()
    {
        _proxied?.Dispose();
        _direct?.Dispose();
        _testClient?.Dispose();
        _cache.Dispose();
    }
}
