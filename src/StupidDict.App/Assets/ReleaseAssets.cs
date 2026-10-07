namespace StupidDict.App.Assets;

/// <summary>Where release assets come from. Mirrors are prefixes of the GitHub URL.</summary>
internal static class ReleaseAssets
{
    internal const string Repository = "cholf5/stupid-dict";

    public const string DictionaryAsset = "dictionary.zip";
    public const string AudioPackAsset = "audio-pack.zip";

    public static string GithubUrl(string assetName) =>
        $"https://github.com/{Repository}/releases/latest/download/{assetName}";

    /// <summary>GitHub first, then public accelerator mirrors that work from CN networks.</summary>
    public static IEnumerable<string> MirrorUrls(string githubUrl)
    {
        yield return githubUrl;
        // Order matters: fastest commonly-working mirror first. When one dies,
        // swap in another prefix; the list is code so anyone can update it.
        foreach (var prefix in new[] { "https://ghfast.top/", "https://gh-proxy.com/", "https://ghproxy.net/" })
            yield return prefix + githubUrl;
    }

    public static IEnumerable<string> SourceUrls(string assetName) => MirrorUrls(GithubUrl(assetName));
}
