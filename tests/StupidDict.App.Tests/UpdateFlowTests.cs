using System.IO.Compression;
using System.Net;
using System.Text;
using StupidDict.App.Settings;
using StupidDict.App.Update;
using Xunit;

namespace StupidDict.App.Tests;

/// <summary>
/// Release-asset naming/picking. The names here are a contract with
/// .github/workflows/dotnet-desktop.yml and scripts/release.sh: a mismatch
/// makes "download update" 404 for every client, so the shapes are pinned.
/// </summary>
public class UpdateAssetsTests
{
    [Theory]
    [InlineData("Windows", "X64", "win-x64")]
    [InlineData("Windows", "Arm64", "win-x64")] // ARM devices run the x64 build through emulation
    [InlineData("OSX", "Arm64", "osx-arm64")]
    [InlineData("OSX", "X64", "osx-x64")]
    [InlineData("Linux", "X64", "linux-x64")]
    public void PickRid_MatchesTheCiMatrix(string os, string arch, string expected) =>
        Assert.Equal(expected, UpdateAssets.PickRid(os, arch));

    [Fact]
    public void AssetNames_MatchTheCiNaming()
    {
        Assert.Equal("StupidDict-1.2.0-osx-arm64.zip",
            UpdateAssets.ZipAssetFileName("v1.2.0", "osx-arm64"));
        Assert.Equal("StupidDict-1.2.0-win-x64-setup.exe",
            UpdateAssets.InstallerAssetFileName("v1.2.0"));
        Assert.Equal("StupidDict-1.2.0-win-x64.zip.sha256",
            UpdateAssets.ChecksumAssetFileName(UpdateAssets.ZipAssetFileName("v1.2.0", "win-x64")));
    }

    [Fact]
    public void PickAssetName_UsesTheInstallerOnlyForInstalledCopies()
    {
        Assert.Equal("StupidDict-1.2.0-win-x64-setup.exe", UpdateAssets.PickAssetName("v1.2.0", true));
        Assert.Equal($"StupidDict-1.2.0-{UpdateAssets.PickRid()}.zip", UpdateAssets.PickAssetName("v1.2.0", false));
    }

    [Fact]
    public void AssetDownloadUrl_PointsAtTheTaggedRelease()
    {
        Assert.Equal(
            "https://github.com/cholf5/stupid-dict/releases/download/v1.2.3/StupidDict-1.2.3-osx-arm64.zip",
            UpdateAssets.AssetDownloadUrl("v1.2.3", "StupidDict-1.2.3-osx-arm64.zip"));
    }

    [Fact]
    public void IsInstalledCopy_ComparesTheExecutableDirectory()
    {
        var install = @"C:\Users\me\AppData\Local\Programs\Stupid Dict";

        Assert.True(UpdateAssets.IsInstalledCopy(true, install + @"\StupidDict.exe", install));
        // Separator/case variation (forward slashes, lowercase) still matches.
        Assert.True(UpdateAssets.IsInstalledCopy(true, install.Replace('\\', '/') + "/StupidDict.exe", install));
        Assert.False(UpdateAssets.IsInstalledCopy(true, @"D:\Portable\StupidDict.exe", install));
        // The criterion is Windows-only; macOS/Linux never take the installer path.
        Assert.False(UpdateAssets.IsInstalledCopy(false, install + @"\StupidDict.exe", install));
        Assert.False(UpdateAssets.IsInstalledCopy(true, null, install));
    }

    [Fact]
    public void MirrorUrls_TryGithubFirstThenAccelerators()
    {
        var urls = UpdateAssets.MirrorUrls("https://github.com/x/y.zip").ToArray();

        Assert.Equal("https://github.com/x/y.zip", urls[0]);
        Assert.All(urls.Skip(1), u => Assert.EndsWith("github.com/x/y.zip", u));
    }
}

/// <summary>The SHA-256 gate: without it a hijacked mirror could replace the program itself.</summary>
public class UpdateChecksumTests
{
    private const string Hex = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string UpperHex = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

    [Theory]
    [InlineData(Hex)]
    [InlineData(UpperHex)]
    // sha256sum's default output (what CI writes): "<hex>  <file name>".
    [InlineData(Hex + "  StupidDict-1.2.0-osx-arm64.zip")]
    [InlineData("  " + Hex + "\n")]
    public void TryParse_AcceptsHexAndSha256sumOutput(string text)
    {
        Assert.True(UpdateChecksum.TryParse(text, out var hex));
        Assert.Equal(Hex, hex);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0123")]
    [InlineData("zzzz6789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    public void TryParse_RejectsAnythingElse(string? text) =>
        Assert.False(UpdateChecksum.TryParse(text, out _));

    [Fact]
    public void Verify_AcceptsTheMatchingFileAndRejectsOthers()
    {
        var path = Path.Combine(Path.GetTempPath(), $"stupiddict-sha-{Guid.NewGuid():N}");
        File.WriteAllText(path, "payload");
        try
        {
            var hex = UpdateChecksum.ComputeHex(path);
            Assert.Equal(64, hex.Length);
            UpdateChecksum.Verify(path, hex.ToUpperInvariant(), "asset.zip");

            var ex = Assert.Throws<InvalidDataException>(
                () => UpdateChecksum.Verify(path, new string('0', 64), "asset.zip"));
            Assert.Contains("asset.zip", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

/// <summary>The journal is the only thing that survives the process dying mid-update.</summary>
public class UpdateJournalTests
{
    [Fact]
    public void SaveLoadClear_RoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"stupiddict-journal-{Guid.NewGuid():N}.json");
        var journal = new UpdateJournal(path);
        try
        {
            Assert.Null(journal.Load());

            journal.Save(new UpdateJournalRecord
            {
                PendingVersion = "v1.2.0",
                BackupPath = "/tmp/backup",
                TargetPath = "/tmp/app",
                StagingPath = "/tmp/staging",
                StagedUtc = new DateTimeOffset(2026, 11, 17, 10, 0, 0, TimeSpan.Zero),
            });

            var record = journal.Load();
            Assert.NotNull(record);
            Assert.Equal("v1.2.0", record!.PendingVersion);
            Assert.Equal("/tmp/backup", record.BackupPath);
            Assert.Equal("/tmp/staging", record.StagingPath);

            journal.Clear();
            Assert.Null(journal.Load());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Load_CorruptFile_ReturnsNull()
    {
        var path = Path.Combine(Path.GetTempPath(), $"stupiddict-journal-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ not json");
        try
        {
            Assert.Null(new UpdateJournal(path).Load());
        }
        finally
        {
            File.Delete(path);
        }
    }
}

/// <summary>The --wait-for-exit hand-over flag (single-instance race after an update).</summary>
public class UpdateRelaunchHandshakeTests
{
    [Fact]
    public void TryParse_ReadsTheProcessId()
    {
        Assert.True(UpdateRelaunchHandshake.TryParse(["--wait-for-exit", "123"], out var pid));
        Assert.Equal(123, pid);

        // Extra arguments before and after are fine: the pair is searched for.
        Assert.True(UpdateRelaunchHandshake.TryParse(["--other", "--wait-for-exit", "42", "x"], out pid));
        Assert.Equal(42, pid);
    }

    [Fact]
    public void TryParse_IgnoresMalformedArguments()
    {
        Assert.False(UpdateRelaunchHandshake.TryParse([], out _));
        Assert.False(UpdateRelaunchHandshake.TryParse(["--wait-for-exit"], out _));
        Assert.False(UpdateRelaunchHandshake.TryParse(["--wait-for-exit", "0"], out _));
        Assert.False(UpdateRelaunchHandshake.TryParse(["--wait-for-exit", "abc"], out _));
        Assert.False(UpdateRelaunchHandshake.TryParse(["123"], out _));
        Assert.False(UpdateRelaunchHandshake.TryParse(null, out _));
    }

    [Fact]
    public void WaitForExit_OfAMissingProcess_ReturnsWithoutThrowing()
    {
        // A pid that cannot exist: GetProcessById throws, which the helper swallows.
        UpdateRelaunchHandshake.WaitForExit(int.MaxValue, TimeSpan.FromMilliseconds(50));
        UpdateRelaunchHandshake.WaitIfRequested(["--wait-for-exit", "999999"]);
    }
}

/// <summary>
/// The portable swap: extraction, payload validation, per-file overlay with
/// backup, and rollback. Everything runs in temp directories.
/// </summary>
public class UpdateStagerTests
{
    private static readonly byte[] PeExecutable = [(byte)'M', (byte)'Z', 1, 2, 3, 4];

    [Fact]
    public void ResolveTargetRoot_WalksUpToTheBundleOnMacOs()
    {
        Assert.Equal("/Applications/Stupid Dict.app",
            UpdateStager.ResolveTargetRoot("/Applications/Stupid Dict.app/Contents/MacOS/StupidDict"));
        Assert.Equal("/opt/stupiddict",
            UpdateStager.ResolveTargetRoot("/opt/stupiddict/StupidDict"));
    }

    [Fact]
    public void LocatePayload_PlainTarget_RequiresTheRunningExecutable()
    {
        var extracted = NewScratchDirectory();
        File.WriteAllBytes(Path.Combine(extracted, "libSkiaSharp.dylib"), [1, 2, 3]);

        Assert.Throws<InvalidDataException>(
            () => UpdateStager.LocatePayload(extracted, targetIsBundle: false, "StupidDict"));

        File.WriteAllBytes(Path.Combine(extracted, "StupidDict"), PeExecutable);
        Assert.Equal(extracted, UpdateStager.LocatePayload(extracted, targetIsBundle: false, "StupidDict"));
    }

    [Fact]
    public void LocatePayload_RejectsAFileThatIsNotThisPlatformsExecutable()
    {
        var extracted = NewScratchDirectory();
        File.WriteAllText(Path.Combine(extracted, "StupidDict"), "<html>mirror error page</html>");

        Assert.Throws<InvalidDataException>(
            () => UpdateStager.LocatePayload(extracted, targetIsBundle: false, "StupidDict"));
    }

    [Fact]
    public void LocatePayload_BundleTarget_RequiresExactlyOneApp()
    {
        var extracted = NewScratchDirectory();
        Assert.Throws<InvalidDataException>(
            () => UpdateStager.LocatePayload(extracted, targetIsBundle: true, "StupidDict"));

        Directory.CreateDirectory(Path.Combine(extracted, "Stupid Dict.app"));
        Assert.Equal(Path.Combine(extracted, "Stupid Dict.app"),
            UpdateStager.LocatePayload(extracted, targetIsBundle: true, "StupidDict"));
    }

    [Fact]
    public void Apply_ReplacesShippedFilesKeepsUserFilesAndBacksUpTheOldOnes()
    {
        var root = NewScratchDirectory();
        var install = Path.Combine(root, "install");
        Directory.CreateDirectory(install);
        var exe = Path.Combine(install, "StupidDict");
        var lib = Path.Combine(install, "libSkiaSharp.dylib");
        var userData = Path.Combine(install, "dictionary.db");
        File.WriteAllText(exe, "old exe");
        File.WriteAllText(lib, "old lib");
        File.WriteAllText(userData, "user data");

        var zip = MakeZip("StupidDict", PeExecutable, "libSkiaSharp.dylib", new byte[] { 9, 9, 9 },
            "NEW-FILE.txt", new byte[] { 7 });
        var stager = new UpdateStager(os: "Linux", processId: 1, relaunchOverride: _ => true);

        var prepared = stager.PreparePortable("v1.1.0", zip, exe);
        Assert.Equal(install, prepared.TargetRoot);
        Assert.False(prepared.TargetIsBundle);
        stager.Apply(prepared);

        Assert.Equal(PeExecutable, File.ReadAllBytes(exe));
        Assert.Equal(new byte[] { 9, 9, 9 }, File.ReadAllBytes(lib));
        Assert.Equal("user data", File.ReadAllText(userData)); // not shipped → untouched
        Assert.True(File.Exists(Path.Combine(install, "NEW-FILE.txt")));
        // The old versions live in the backup until the restarted app confirms.
        Assert.Equal("old exe", File.ReadAllText(Path.Combine(prepared.BackupPath, "StupidDict")));
        Assert.Equal("old lib", File.ReadAllText(Path.Combine(prepared.BackupPath, "libSkiaSharp.dylib")));
        Assert.True(stager.Relaunch(prepared));

        // Rollback (the "record says vX, app still runs vOld" path) restores the backup.
        Assert.True(UpdateStager.TryRollback(prepared.BackupPath, install));
        Assert.Equal("old exe", File.ReadAllText(exe));
        Assert.Equal("old lib", File.ReadAllText(lib));
    }

    [Fact]
    public void ApplyBundle_SwapsTheWholeAppAndKeepsTheBundledDictionary()
    {
        var root = NewScratchDirectory();
        var bundle = Path.Combine(root, "Applications", "Stupid Dict.app");
        var contents = Path.Combine(bundle, "Contents");
        var macOs = Path.Combine(contents, "MacOS");
        var resources = Path.Combine(contents, "Resources");
        Directory.CreateDirectory(macOs);
        Directory.CreateDirectory(resources);
        var exe = Path.Combine(macOs, "StupidDict");
        File.WriteAllText(exe, "old exe");
        File.WriteAllText(Path.Combine(contents, "Info.plist"), "old plist");
        File.WriteAllText(Path.Combine(resources, "dictionary.db"), "user data");

        // The app zip ships no dictionary.db; the with-dictionary copy lives in
        // the bundle's Resources and must survive the swap.
        var zip = MakeZip("Stupid Dict.app/Contents/MacOS/StupidDict", PeExecutable,
            "Stupid Dict.app/Contents/Info.plist", Encoding.UTF8.GetBytes("new plist"));
        var stager = new UpdateStager(os: "OSX", processId: 1, relaunchOverride: _ => true);

        var prepared = stager.PreparePortable("v1.1.0", zip, exe);
        Assert.True(prepared.TargetIsBundle);
        Assert.Equal(bundle, prepared.TargetRoot);
        stager.Apply(prepared);

        Assert.Equal(PeExecutable, File.ReadAllBytes(exe));
        Assert.Equal("new plist", File.ReadAllText(Path.Combine(contents, "Info.plist")));
        Assert.Equal("user data", File.ReadAllText(Path.Combine(resources, "dictionary.db")));
        // The old bundle is the rollback ticket.
        Assert.True(Directory.Exists(Path.Combine(prepared.BackupPath, "Stupid Dict.app")));

        Assert.True(UpdateStager.TryRollback(prepared.BackupPath, bundle));
        Assert.Equal("old exe", File.ReadAllText(exe));
    }

    [Fact]
    public void PreparePortable_RejectsAWrongPlatformArchiveAndLeavesNoStaging()
    {
        var root = NewScratchDirectory();
        var install = Path.Combine(root, "install");
        Directory.CreateDirectory(install);
        var exe = Path.Combine(install, "StupidDict");
        File.WriteAllBytes(exe, PeExecutable);

        // A macOS bundle zip on a plain target: no StupidDict at the root.
        var zip = MakeZip("Stupid Dict.app/Contents/MacOS/StupidDict", PeExecutable);
        var stager = new UpdateStager(os: "Linux", processId: 1);

        Assert.Throws<InvalidDataException>(() => stager.PreparePortable("v1.1.0", zip, exe));
        Assert.Empty(Directory.EnumerateDirectories(root, UpdateStager.StagingPrefix + "*"));
    }

    internal static string MakeZip(params object[] items)
    {
        var path = Path.Combine(Path.GetTempPath(), $"stupiddict-update-{Guid.NewGuid():N}.zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        for (var i = 0; i < items.Length; i += 2)
        {
            var entry = archive.CreateEntry((string)items[i]);
            using var stream = entry.Open();
            stream.Write((byte[])items[i + 1]);
        }

        return path;
    }

    internal static string NewScratchDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "stupiddict-update-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}

/// <summary>
/// The service state machine end to end, with a fake HTTP handler and a temp
/// install directory: check → download (checksum verified) → apply → restart
/// request, plus the startup journal interpretation.
/// </summary>
public class UpdateServiceTests
{
    private const string NewTag = "v9.9.9";

    [Fact]
    public void ShouldAutoCheck_IsThrottledByTheRecordedTimestamp()
    {
        var settings = new AppSettings();
        var service = MakeService(settings, new FakeHandler(_ => NotFound()));

        Assert.True(service.ShouldAutoCheck(TimeSpan.FromDays(7)));

        settings.LastUpdateCheckUtc = DateTimeOffset.UtcNow;
        Assert.False(service.ShouldAutoCheck(TimeSpan.FromDays(7)));

        settings.LastUpdateCheckUtc = DateTimeOffset.UtcNow.AddDays(-8);
        Assert.True(service.ShouldAutoCheck(TimeSpan.FromDays(7)));
    }

    [Fact]
    public async Task CheckNowAsync_RecordsTheTimestampEvenWhenItFails()
    {
        var settings = new AppSettings();
        var saved = 0;
        var service = MakeService(settings,
            new FakeHandler(_ => throw new HttpRequestException("offline"), redirectLatest: false),
            saveSettings: () => saved++);

        var result = await service.CheckNowAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateCheckOutcome.Failed, result.Outcome);
        Assert.Equal(UpdateState.Failed, service.State);
        Assert.NotNull(settings.LastUpdateCheckUtc);
        Assert.Equal(1, saved);
    }

    [Fact]
    public async Task DownloadAndApply_VerifiesTheChecksumAndSwapsTheFiles()
    {
        var root = UpdateStagerTests.NewScratchDirectory();
        var install = Path.Combine(root, "install");
        Directory.CreateDirectory(install);
        var exe = Path.Combine(install, "StupidDict");
        File.WriteAllText(exe, "old exe");

        var zip = UpdateStagerTests.MakeZip("StupidDict", new byte[] { (byte)'M', (byte)'Z', 7, 7 },
            "libSkiaSharp.dylib", new byte[] { 1, 2, 3 });
        var checksum = UpdateChecksum.ComputeHex(zip);
        var settings = new AppSettings();
        var restarted = 0;
        PreparedUpdate? relaunched = null;
        var journalPath = Path.Combine(root, "update-state.json");

        var service = MakeService(settings, new FakeHandler(request =>
                request.RequestUri!.AbsolutePath.EndsWith(".sha256")
                    ? Text(checksum)
                    : Bytes(File.ReadAllBytes(zip))),
            processPath: exe,
            journalPath: journalPath,
            stager: new UpdateStager(os: "Linux", processId: 1, relaunchOverride: prepared =>
            {
                relaunched = prepared;
                return true;
            }));
        service.RestartRequested += () => restarted++;

        await service.CheckNowAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateState.Available, service.State);
        Assert.Equal(NewTag, service.LastResult?.LatestVersion);

        await service.DownloadAsync(null, TestContext.Current.CancellationToken);
        Assert.Equal(UpdateState.ReadyToRestart, service.State);

        service.RestartAndApply();

        Assert.Equal(1, restarted);
        Assert.NotNull(relaunched);
        Assert.Equal(new byte[] { (byte)'M', (byte)'Z', 7, 7 }, File.ReadAllBytes(exe));
        Assert.True(File.Exists(Path.Combine(install, "libSkiaSharp.dylib")));

        var journal = new UpdateJournal(journalPath).Load();
        Assert.NotNull(journal);
        Assert.Equal(NewTag, journal!.PendingVersion);
        Assert.Equal(install, journal.TargetPath);
    }

    [Fact]
    public async Task Download_FailsWithoutAChecksumSidecarAndLeavesNoFile()
    {
        var root = UpdateStagerTests.NewScratchDirectory();
        var install = Path.Combine(root, "install");
        Directory.CreateDirectory(install);
        var exe = Path.Combine(install, "StupidDict");
        File.WriteAllBytes(exe, new byte[] { (byte)'M', (byte)'Z' });

        var zip = UpdateStagerTests.MakeZip("StupidDict", new byte[] { (byte)'M', (byte)'Z', 1 });
        var service = MakeService(new AppSettings(), new FakeHandler(request =>
                request.RequestUri!.AbsolutePath.EndsWith(".sha256") ? NotFound() : Bytes(File.ReadAllBytes(zip))),
            processPath: exe);

        await service.CheckNowAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<Exception>(
            () => service.DownloadAsync(null, TestContext.Current.CancellationToken));

        Assert.Equal(UpdateState.Available, service.State); // retryable
        Assert.NotNull(service.LastError);
        Assert.Equal(new byte[] { (byte)'M', (byte)'Z' }, File.ReadAllBytes(exe)); // untouched
    }

    [Fact]
    public void StartupJournal_MatchingVersion_ReportsUpgradedAndCleansUp()
    {
        var root = UpdateStagerTests.NewScratchDirectory();
        var staging = Path.Combine(root, ".stupiddict-update-v1");
        var backup = Path.Combine(staging, "backup");
        Directory.CreateDirectory(backup);
        var journalPath = Path.Combine(root, "update-state.json");
        new UpdateJournal(journalPath).Save(new UpdateJournalRecord
        {
            PendingVersion = "v1.0.0", // the injected comparison base, see MakeService
            BackupPath = backup,
            TargetPath = root,
            StagingPath = staging,
        });

        var service = MakeService(new AppSettings(), new FakeHandler(_ => NotFound()), journalPath: journalPath);

        Assert.Equal(UpdateStartupOutcome.Upgraded, service.StartupNotice.Outcome);
        Assert.False(File.Exists(journalPath));
        Assert.False(Directory.Exists(staging)); // the backup is only disk use; drop it
    }

    [Fact]
    public void StartupJournal_MismatchedVersion_RollsTheOldFilesBack()
    {
        var root = UpdateStagerTests.NewScratchDirectory();
        var install = Path.Combine(root, "install");
        Directory.CreateDirectory(install);
        var exe = Path.Combine(install, "StupidDict");
        File.WriteAllText(exe, "half-applied new exe");

        var staging = Path.Combine(root, ".stupiddict-update-v9.9.9");
        var backup = Path.Combine(staging, "backup");
        Directory.CreateDirectory(backup);
        File.WriteAllText(Path.Combine(backup, "StupidDict"), "old exe");

        var journalPath = Path.Combine(root, "update-state.json");
        new UpdateJournal(journalPath).Save(new UpdateJournalRecord
        {
            PendingVersion = NewTag,
            BackupPath = backup,
            TargetPath = install,
            StagingPath = staging,
        });

        var service = MakeService(new AppSettings(), new FakeHandler(_ => NotFound()),
            processPath: exe, journalPath: journalPath);

        Assert.Equal(UpdateStartupOutcome.ApplyFailed, service.StartupNotice.Outcome);
        Assert.True(service.StartupNotice.RolledBack);
        Assert.Equal("old exe", File.ReadAllText(exe));
        Assert.False(File.Exists(journalPath));
    }

    // ---- helpers ----

    private static UpdateService MakeService(AppSettings settings, HttpMessageHandler handler,
        Action? saveSettings = null, string? processPath = null, string? journalPath = null,
        UpdateStager? stager = null, string currentVersion = "v1.0.0")
    {
        var downloadDirectory = UpdateStagerTests.NewScratchDirectory();
        return new UpdateService(
            new UpdateChecker(handler, currentVersion: currentVersion),
            settings,
            saveSettings: saveSettings,
            downloader: new UpdateDownloadService(handler, downloadDirectory),
            openUrl: _ => { },
            revealFile: _ => { },
            windowsInstalledCopy: false,
            stager: stager,
            journal: new UpdateJournal(journalPath
                                       ?? Path.Combine(UpdateStagerTests.NewScratchDirectory(), "update-state.json")),
            processPath: processPath ?? Path.Combine(UpdateStagerTests.NewScratchDirectory(), "StupidDict"));
    }

    private static HttpResponseMessage RedirectResponse(string releaseUrl) => new(HttpStatusCode.Found)
    {
        Headers = { Location = new Uri(releaseUrl) },
    };

    /// <summary>Every check in these tests finds a newer release; the asset requests are answered by the same handler.</summary>
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond,
        bool redirectLatest = true) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            // The checker asks for releases/latest and wants its 302; everything
            // else (checksum, asset) is the response the test scripted.
            Task.FromResult(redirectLatest && request.RequestUri!.AbsolutePath.Contains("/releases/latest")
                ? RedirectResponse($"https://github.com/cholf5/stupid-dict/releases/tag/{NewTag}")
                : respond(request));
    }

    private static HttpResponseMessage NotFound() => new(HttpStatusCode.NotFound);

    private static HttpResponseMessage Text(string text) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(text)) };

    private static HttpResponseMessage Bytes(byte[] bytes) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
}
