namespace StupidDict.App.Assets;

/// <summary>Where release assets come from. Mirrors are prefixes of the GitHub URL.</summary>
internal static class ReleaseAssets
{
    internal const string Repository = "cholf5/stupid-dict";

    /// <summary>
    /// The prerelease holding the big data assets (dictionary / audio pack),
    /// decoupled from app versions: publish once, bump only if the data ever
    /// changes. Marked prerelease so it never becomes releases/latest — the
    /// in-app update check reads that page and expects an app vX.Y.Z tag.
    /// </summary>
    internal const string DataTag = "data-1";

    public const string DictionaryAsset = "dictionary.zip";
    public const string AudioPackAsset = "audio-pack.zip";

    public static string GithubUrl(string assetName) =>
        $"https://github.com/{Repository}/releases/download/{DataTag}/{assetName}";

    /// <summary>Browser-facing release page — the manual-download escape hatch.</summary>
    public static string DataReleasePageUrl =>
        $"https://github.com/{Repository}/releases/tag/{DataTag}";

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
