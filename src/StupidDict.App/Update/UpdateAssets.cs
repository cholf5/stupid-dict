using System.Runtime.InteropServices;
using StupidDict.App.Assets;

namespace StupidDict.App.Update;

/// <summary>
/// The single source of truth for release assets: repository, asset naming
/// (must match the CI artifacts exactly), platform picking and the mirror list.
/// </summary>
internal static class UpdateAssets
{
    /// <summary>
    /// GitHub repository slug. The tag is v{csproj Version} — release.sh /
    /// CI / the in-app updater all read that same version.
    /// </summary>
    internal const string Repository = ReleaseAssets.Repository;

    /// <summary>Repository home page (the About card's repo row).</summary>
    internal const string RepositoryUrl = $"https://github.com/{Repository}";

    /// <summary>The releases list page — the escape hatch when a download or check fails.</summary>
    internal static string ReleasesPageUrl => $"https://github.com/{Repository}/releases";

    /// <summary>A specific tag's release page (opened in the browser, for manual downloads).</summary>
    internal static string ReleasePageUrl(string tag) => $"https://github.com/{Repository}/releases/tag/{tag}";

    /// <summary>
    /// Direct asset URL. The CI artifact names live in
    /// .github/workflows/dotnet-desktop.yml and scripts/package.sh — changing
    /// one of those three places without the others breaks "download update"
    /// for every installed client.
    /// </summary>
    internal static string AssetDownloadUrl(string tag, string assetName) =>
        $"https://github.com/{Repository}/releases/download/{tag}/{assetName}";

    /// <summary>GitHub direct link first, then the same public accelerator prefixes the data downloads use.</summary>
    internal static IEnumerable<string> MirrorUrls(string githubUrl) => ReleaseAssets.MirrorUrls(githubUrl);

    /// <summary>Platform RID (one of the four CI actually builds). Internal so tests can inject OS/arch.</summary>
    internal static string PickRid() =>
        PickRid(
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Windows"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "OSX"
            : "Linux",
            RuntimeInformation.ProcessArchitecture.ToString());

    internal static string PickRid(string os, string arch)
    {
        // One asset per Windows/Linux architecture: win-x64 runs on ARM devices
        // through system x64 emulation, and Linux only ever ships x64. macOS
        // splits arm64/x64 — running x64 there needs Rosetta, so picking the
        // wrong asset is a real usability loss; match the process architecture.
        return os switch
        {
            "Windows" => "win-x64",
            "OSX" => arch == "Arm64" ? "osx-arm64" : "osx-x64",
            _ => "linux-x64",
        };
    }

    /// <summary>Release asset file name (portable/extracted distribution): StupidDict-{version}-{rid}.zip.</summary>
    internal static string AssetFileName(string latestTag) => ZipAssetFileName(latestTag);

    /// <summary>Same, with an explicit RID (tests inject a platform instead of following the build machine).</summary>
    internal static string ZipAssetFileName(string latestTag, string rid) =>
        $"StupidDict-{latestTag.TrimStart('v')}-{rid}.zip";

    internal static string ZipAssetFileName(string latestTag) => ZipAssetFileName(latestTag, PickRid());

    /// <summary>
    /// Windows installer asset name (Inno Setup output, built in the same CI
    /// job as the zip). Installed copies upgrade through it silently. Note the
    /// installer is a PE (MZ header), not a zip, so download integrity checks
    /// branch on the extension.
    /// </summary>
    internal static string InstallerAssetFileName(string latestTag) =>
        $"StupidDict-{latestTag.TrimStart('v')}-win-x64-setup.exe";

    /// <summary>Where the Windows installer puts the app (same source as the Inno DefaultDirName).</summary>
    internal static string InstallDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Stupid Dict");

    /// <summary>
    /// Whether this process runs out of the install directory (an installed
    /// copy) rather than an extracted portable folder. Decides the upgrade
    /// path: installed → download the installer and upgrade silently;
    /// portable → download the zip and swap the files in place.
    /// The criterion is the executable's directory, not a registry key or a
    /// marker file — a portable user may unzip anywhere.
    /// </summary>
    internal static bool IsInstalledCopy() => IsInstalledCopy(
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows), Environment.ProcessPath, InstallDirectory);

    internal static bool IsInstalledCopy(bool isWindows, string? processPath, string installDirectory)
    {
        if (!isWindows || string.IsNullOrEmpty(processPath))
        {
            return false;
        }

        // Not Path.GetDirectoryName: on Unix it does not know backslashes, and
        // that test case (or any cross-platform construction) would treat a
        // Windows path as a bare file name. Split on both separators instead.
        var directory = DirectoryOf(processPath);
        return directory is not null
            && string.Equals(NormalizeSeparators(directory), NormalizeSeparators(installDirectory),
                StringComparison.OrdinalIgnoreCase);
    }

    private static string? DirectoryOf(string path)
    {
        var index = path.LastIndexOfAny(['\\', '/']);
        return index > 0 ? path[..index] : null;
    }

    /// <summary>Trailing separators ignored, both separators treated alike: Windows accepts either and a
    /// process path is not guaranteed to use the backslash form.</summary>
    private static string NormalizeSeparators(string path) =>
        path.TrimEnd('\\', '/').Replace('/', '\\');

    /// <summary>Which asset this upgrade downloads: Windows installed copy → installer; everything else → zip.</summary>
    internal static string PickAssetName(string latestTag, bool windowsInstalledCopy) =>
        windowsInstalledCopy ? InstallerAssetFileName(latestTag) : ZipAssetFileName(latestTag);

    /// <summary>
    /// The asset's SHA-256 sidecar (same name + <c>.sha256</c>, produced with
    /// <c>sha256sum</c> in CI). With self-update the checksum is a
    /// precondition, not a nicety: the mirror prefixes are third parties, and
    /// whoever can swap the download can swap the program itself.
    /// </summary>
    internal static string ChecksumAssetFileName(string assetName) => assetName + ".sha256";
}
