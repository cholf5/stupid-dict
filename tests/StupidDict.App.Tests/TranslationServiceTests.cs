using System.Net;
using System.Text;
using StupidDict.App;
using StupidDict.App.Settings;
using StupidDict.App.Translation;
using Xunit;

namespace StupidDict.App.Tests;

public class TranslationServiceTests
{
    [Fact]
    public async Task SendsOpenAiCompatibleRequestAndCachesSuccess()
    {
        var requests = new List<(Uri Uri, string? Auth, string Body)>();
        var handler = new FakeHandler(async request =>
        {
            requests.Add((request.RequestUri!, request.Headers.Authorization?.ToString(),
                await request.Content!.ReadAsStringAsync()));
            return Json(HttpStatusCode.OK,
                """{"choices":[{"message":{"content":"你好，世界。"}}]}""");
        });
        using var cache = new TranslationCache(TempPath(), maxBytes: 10_000);
        var settings = new AppSettings
        {
            TranslationApiUrl = "https://api.openai.com/v1",
            TranslationApiKey = "sk-test",
            TranslationModel = "gpt-4o-mini",
            TranslationAutoOnMiss = true,
        };
        using var service = new TranslationService(settings, cache, handler);

        var first = await service.TranslateAsync("Hello world.", bypassCache: false, TestContext.Current.CancellationToken);

        Assert.True(first.IsSuccess);
        Assert.Equal("你好，世界。", first.Text);
        Assert.False(first.FromCache);
        Assert.Single(requests);
        Assert.Equal("https://api.openai.com/v1/chat/completions", requests[0].Uri.ToString());
        Assert.Equal("Bearer sk-test", requests[0].Auth);
        Assert.Contains("gpt-4o-mini", requests[0].Body);
        Assert.Contains("<text>Hello world.</text>", requests[0].Body);

        var second = await service.TranslateAsync("Hello world.", bypassCache: false, TestContext.Current.CancellationToken);
        Assert.True(second.IsSuccess);
        Assert.True(second.FromCache);
        Assert.Single(requests);
    }

    [Fact]
    public async Task AuthFailurePausesAutomaticTranslation()
    {
        using var cache = new TranslationCache(TempPath());
        var settings = new AppSettings
        {
            TranslationApiUrl = "https://api.openai.com/v1",
            TranslationApiKey = "bad",
            TranslationModel = "m",
        };
        using var service = new TranslationService(settings, cache,
            new FakeHandler(_ => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.Unauthorized))));

        var result = await service.TranslateAsync("Hello world.", false, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(TranslationFailureKind.Authentication, result.Failure);
        Assert.True(service.AutoPaused);
        service.ResetCircuit();
        Assert.False(service.AutoPaused);
    }

    [Fact]
    public async Task RedirectResponsesAreRejectedWithoutFollowing()
    {
        // 红线：302 绝不跟随（Authorization 不能被带到别的主机）——AllowAutoRedirect=false
        // 之外，代码层还要把 3xx 判为配置类失败。key 不得出现在错误诊断里。
        using var cache = new TranslationCache(TempPath());
        var settings = new AppSettings
        {
            TranslationApiUrl = "https://api.openai.com/v1",
            TranslationApiKey = "sk-secret",
            TranslationModel = "m",
        };
        using var service = new TranslationService(settings, cache, new FakeHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri("https://evil.example.com/collect");
            return Task.FromResult(response);
        }));

        var result = await service.TranslateAsync("Hello world.", false, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(TranslationFailureKind.InvalidConfig, result.Failure);
        Assert.False(result.CanRetry);
        Assert.DoesNotContain("sk-secret", result.Detail);
    }

    [Fact]
    public async Task EmptyKeyOmitsAuthorizationHeader()
    {
        string? auth = "sent";
        var handler = new FakeHandler(request =>
        {
            auth = request.Headers.Authorization?.ToString();
            return Task.FromResult(Json(HttpStatusCode.OK,
                """{"choices":[{"message":{"content":"好"}}]}"""));
        });
        using var cache = new TranslationCache(TempPath());
        var settings = new AppSettings
        {
            TranslationApiUrl = "http://localhost:11434/v1",
            TranslationApiKey = "",
            TranslationModel = "qwen2.5:7b",
        };
        using var service = new TranslationService(settings, cache, handler);

        await service.TranslateAsync("Hello", false, TestContext.Current.CancellationToken);

        Assert.Null(auth); // Ollama 等本地端点允许空 key：不发 Authorization 头
    }

    [Fact]
    public async Task UserAgentIdentifiesTheApp()
    {
        string? userAgent = null;
        var handler = new FakeHandler(request =>
        {
            userAgent = request.Headers.UserAgent.ToString();
            return Task.FromResult(Json(HttpStatusCode.OK,
                """{"choices":[{"message":{"content":"好"}}]}"""));
        });
        using var cache = new TranslationCache(TempPath());
        var settings = new AppSettings
        {
            TranslationApiUrl = "http://localhost:11434/v1",
            TranslationModel = "m",
        };
        using var service = new TranslationService(settings, cache, handler);

        await service.TranslateAsync("Hello", false, TestContext.Current.CancellationToken);

        Assert.StartsWith("stupiddict-desktop/", userAgent);
    }

    [Fact]
    public async Task ArrayFormContentIsParsed()
    {
        using var cache = new TranslationCache(TempPath());
        var settings = new AppSettings
        {
            TranslationApiUrl = "http://localhost:11434/v1",
            TranslationModel = "m",
        };
        using var service = new TranslationService(settings, cache, new FakeHandler(_ => Task.FromResult(Json(
            HttpStatusCode.OK,
            """{"choices":[{"message":{"content":[{"type":"text","text":"你"},{"type":"text","text":"好，世"},{"type":"text","text":"界。"}]}}]}"""))));

        var result = await service.TranslateAsync("Hello world.", false, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal("你好，世界。", result.Text);
    }

    [Fact]
    public async Task SuccessResponseWithErrorObjectFails()
    {
        using var cache = new TranslationCache(TempPath());
        var settings = new AppSettings
        {
            TranslationApiUrl = "http://localhost:11434/v1",
            TranslationModel = "m",
        };
        using var service = new TranslationService(settings, cache, new FakeHandler(_ => Task.FromResult(Json(
            HttpStatusCode.OK, """{"error":{"message":"insufficient quota"}}"""))));

        var result = await service.TranslateAsync("Hello world.", false, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(TranslationFailureKind.InvalidResponse, result.Failure);
    }

    [Fact]
    public async Task RequestTimeoutMapsToTimeoutFailure()
    {
        using var cache = new TranslationCache(TempPath());
        var settings = new AppSettings
        {
            TranslationApiUrl = "http://localhost:11434/v1",
            TranslationModel = "m",
        };
        using var service = new TranslationService(settings, cache, new FakeHandler(_ =>
            Task.FromCanceled<HttpResponseMessage>(new CancellationToken(canceled: true))));

        var result = await service.TranslateAsync("Hello world.", false, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(TranslationFailureKind.Timeout, result.Failure);
        Assert.True(result.CanRetry);
    }

    [Fact]
    public async Task CacheClearedDuringFlightDoesNotWriteBack()
    {
        // 清除缓存递增 generation：在飞请求完成后不得把旧结果写回。
        using var cache = new TranslationCache(TempPath());
        var settings = new AppSettings
        {
            TranslationApiUrl = "http://localhost:11434/v1",
            TranslationApiKey = "sk-test",
            TranslationModel = "gpt-4o-mini",
        };
        TranslationService? service = null;
        using var handlerOwner = new FakeHandler(_ =>
        {
            service!.ClearCache(); // 响应返回前用户清了缓存
            return Task.FromResult(Json(HttpStatusCode.OK,
                """{"choices":[{"message":{"content":"你好，世界。"}}]}"""));
        });
        service = new TranslationService(settings, cache, handlerOwner);

        var first = await service.TranslateAsync("Hello world.", bypassCache: true, TestContext.Current.CancellationToken);
        Assert.True(first.IsSuccess);

        var second = await service.TranslateAsync("Hello world.", bypassCache: false, TestContext.Current.CancellationToken);
        Assert.False(second.FromCache); // 写回被 generation 挡下 → 又发一次
    }

    [Fact]
    public async Task ModelChangeInvalidatesCache()
    {
        var requests = 0;
        var handler = new FakeHandler(_ =>
        {
            requests++;
            return Task.FromResult(Json(HttpStatusCode.OK,
                """{"choices":[{"message":{"content":"你好，世界。"}}]}"""));
        });
        using var cache = new TranslationCache(TempPath(), maxBytes: 10_000);
        var settings = new AppSettings
        {
            TranslationApiUrl = "https://api.openai.com/v1",
            TranslationApiKey = "sk-test",
            TranslationModel = "gpt-4o-mini",
        };
        using var service = new TranslationService(settings, cache, handler);

        await service.TranslateAsync("Hello world.", false, TestContext.Current.CancellationToken);
        Assert.Equal(1, requests);
        settings.TranslationModel = "gpt-4o"; // 换模型 → profile key 变 → miss
        await service.TranslateAsync("Hello world.", false, TestContext.Current.CancellationToken);

        Assert.Equal(2, requests);
    }

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), "stupiddict-service-tests",
            Guid.NewGuid().ToString("N"), "cache.db");

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }
}
