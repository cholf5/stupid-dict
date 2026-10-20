using StupidDict.App.Translation;
using Xunit;

namespace StupidDict.App.Tests;

public class TranslationEndpointTests
{
    [Theory]
    [InlineData("https://api.openai.com/v1", "https://api.openai.com/v1/chat/completions")]
    [InlineData("https://api.openai.com/v1/", "https://api.openai.com/v1/chat/completions")]
    [InlineData("https://api.deepseek.com", "https://api.deepseek.com/v1/chat/completions")]
    [InlineData("http://localhost:11434", "http://localhost:11434/v1/chat/completions")]
    [InlineData("https://openrouter.ai/api/v1", "https://openrouter.ai/api/v1/chat/completions")]
    [InlineData("https://api.openai.com/v1/chat/completions", "https://api.openai.com/v1/chat/completions")]
    [InlineData("https://relay.example.com/chat/completions", "https://relay.example.com/chat/completions")]
    public void NormalizesBaseAndFullEndpoints(string input, string expected)
    {
        Assert.True(TranslationEndpoint.TryCreate(input, out var endpoint, out var error), error?.ToString());
        Assert.Equal(expected, endpoint!.ChatCompletionsUri.ToString());
    }

    [Theory]
    [InlineData("ftp://example.com")]
    [InlineData("https://user:pass@example.com/v1")]
    [InlineData("https://example.com/v1?x=1")]
    [InlineData("http://example.com/v1")] // public HTTP rejected
    [InlineData("not a url")]
    public void RejectsUnsafeOrUnsupportedUrls(string input)
    {
        Assert.False(TranslationEndpoint.TryCreate(input, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void LoopbackIsLocalAndHasStableProfile()
    {
        Assert.True(TranslationEndpoint.TryCreate("http://127.0.0.1:11434/v1", out var endpoint, out _));
        Assert.True(endpoint!.IsLoopback);
        Assert.Equal("http://127.0.0.1:11434/v1", endpoint.ProfileKey);
    }
}
