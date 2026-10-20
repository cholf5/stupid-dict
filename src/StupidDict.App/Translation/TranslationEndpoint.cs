using System.Net;

namespace StupidDict.App.Translation;

public enum TranslationEndpointError
{
    InvalidUrl,
    UnsupportedScheme,
    UserInfoNotAllowed,
    QueryNotAllowed,
    PublicHttpNotAllowed,
}

/// <summary>
/// Validates and normalizes a user-supplied OpenAI-compatible base URL.
/// Accepts a base ending at /v1 or a full /chat/completions endpoint; the
/// profile key is the base used in cache keys and never contains credentials.
/// </summary>
public sealed record TranslationEndpoint(Uri ChatCompletionsUri, Uri BaseUri, string ProfileKey, bool IsLoopback)
{
    private const string ChatPath = "/chat/completions";

    public static bool TryCreate(string? value, out TranslationEndpoint? endpoint, out TranslationEndpointError? error)
    {
        endpoint = null;
        error = null;

        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            error = TranslationEndpointError.InvalidUrl;
            return false;
        }

        if (uri.Scheme is not ("http" or "https"))
        {
            error = TranslationEndpointError.UnsupportedScheme;
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            error = TranslationEndpointError.UserInfoNotAllowed;
            return false;
        }

        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            error = TranslationEndpointError.QueryNotAllowed;
            return false;
        }

        var loopback = uri.IsLoopback;
        if (uri.Scheme == "http" && !loopback && !IsPrivateAddress(uri))
        {
            error = TranslationEndpointError.PublicHttpNotAllowed;
            return false;
        }

        // 设计规则 3：path 以 /chat/completions 结尾 → 原样使用（剥掉后缀只为
        // 算 base/profileKey，不能把空 path 再补成 /v1——那会让根级完整端点
        // 被改写成 /v1/chat/completions）。只有输入 path 本来就为空时才补 /v1。
        var path = uri.AbsolutePath.TrimEnd('/');
        var hadChatSuffix = path.EndsWith(ChatPath, StringComparison.OrdinalIgnoreCase);
        if (hadChatSuffix)
            path = path[..^ChatPath.Length].TrimEnd('/');
        if (path.Length == 0 && !hadChatSuffix)
            path = "/v1";

        var basePath = path.Length == 0 ? "/" : path;
        var chatPath = basePath == "/" ? ChatPath : basePath + ChatPath;

        var baseUri = new UriBuilder(uri) { Path = basePath, Query = "", Fragment = "" }.Uri;
        var chatUri = new UriBuilder(uri) { Path = chatPath, Query = "", Fragment = "" }.Uri;
        var profileKey = $"{uri.Scheme}://{uri.Authority}{basePath}";

        endpoint = new TranslationEndpoint(chatUri, baseUri, profileKey, loopback);
        return true;
    }

    /// <summary>HTTP is allowed only for loopback or private/link-local addresses.</summary>
    private static bool IsPrivateAddress(Uri uri)
    {
        if (!IPAddress.TryParse(uri.DnsSafeHost, out var address))
            return false;

        if (IPAddress.IsLoopback(address))
            return true;

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 10
                || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 169 && bytes[1] == 254);
        }

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal
                || (address.GetAddressBytes()[0] & 0xFE) == 0xFC;

        return false;
    }
}
