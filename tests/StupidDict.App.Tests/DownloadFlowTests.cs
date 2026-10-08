using System.IO.Compression;
using System.Security.Cryptography;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using StupidDict.App;
using StupidDict.App.Assets;
using StupidDict.App.Localization;
using StupidDict.App.Settings;
using StupidDict.Core.Application;
using StupidDict.Core.Dictionary;
using Xunit;

namespace StupidDict.App.Tests;

/// <summary>
/// Bootstrap download semantics: the checksum failure recovery (purge +
/// automatic clean retry), the resume label on a second progress pass, and
/// all-or-nothing extraction. Scoped to the dictionary flow; the auto-started
/// audio pack download is out of scope and the stubs reject other assets.
/// </summary>
public class DownloadFlowTests
{
    public DownloadFlowTests()
    {
        // Same pin as HeadlessWindowTests: string assertions need a fixed language.
        Translations.Instance.SetLanguage(AppLanguage.SimplifiedChinese);
    }

    [AvaloniaFact]
    public void ChecksumMismatchPurgesArtifactsAndRedownloadsOnce()
    {
        var locations = NewLocations(out var dictionaryPath, out _);
        var (zipPath, goodHash) = MakeDictionaryZipWithHash();
        // First attempt serves corrupt bytes; the checksum then forces the
        // purge-and-retry path, whose second attempt serves the real zip.
        var downloader = new ScriptedDownloader(
            callIndex => callIndex == 0 ? [0x1, 0x2, 0x3] : File.ReadAllBytes(zipPath),
            _ => goodHash);
        using var service = new DictionaryService(dictionaryPath, locations.HistoryDatabasePath);
        var window = new MainWindow(service, downloader: downloader, locations: locations, autoDownload: true);
        window.Show();

        WaitUntil(() => !window.FindControl<StackPanel>("DictionaryDownloadPanel")!.IsVisible);

        Assert.Equal(2, downloader.RequestCount);
        // The retry must start from a clean slate: no corrupt zip, no .part.
        Assert.False(downloader.DestinationExisted[1]);
        Assert.True(File.Exists(locations.DictionaryDatabasePath));
    }

    [AvaloniaFact]
    public void ChecksumMismatchTwiceReportsChecksumFailureNotDownloadFailure()
    {
        var locations = NewLocations(out var dictionaryPath, out _);
        var destination = Path.Combine(Path.GetTempPath(), "stupiddict-downloads", ReleaseAssets.DictionaryAsset);
        var downloader = new ScriptedDownloader(
            _ => new byte[] { 0x1, 0x2, 0x3 },
            _ => new string('0', 64));
        using var service = new DictionaryService(dictionaryPath, locations.HistoryDatabasePath);
        var window = new MainWindow(service, downloader: downloader, locations: locations, autoDownload: true);
        window.Show();

        WaitUntil(() =>
            window.FindControl<TextBlock>("DictionaryDownloadStatus")!.Text == Translations.Instance.ChecksumFailed);

        Assert.Equal(2, downloader.RequestCount);
        Assert.True(window.FindControl<Button>("DownloadDictionaryButton")!.IsVisible);
        Assert.False(File.Exists(destination));
        Assert.False(File.Exists(destination + ".part"));
    }

    [AvaloniaFact]
    public void ResumedDownloadAttemptIsLabeledAsResume()
    {
        var locations = NewLocations(out var dictionaryPath, out _);
        var (zipPath, _) = MakeDictionaryZipWithHash();
        // Holds the download open after reporting a resumed pass, so the test
        // can observe the status line while the second bar is "running".
        var downloader = new PausingResumeDownloader(zipPath);
        using var service = new DictionaryService(dictionaryPath, locations.HistoryDatabasePath);
        var window = new MainWindow(service, downloader: downloader, locations: locations, autoDownload: true);
        window.Show();

        var observed = new List<string>();
        WaitUntil(() =>
        {
            var text = window.FindControl<TextBlock>("DictionaryDownloadStatus")!.Text ?? "";
            observed.Add(text);
            return text.Contains("断点续传");
        });
        Assert.Contains(observed, text => text.Contains("断点续传"));
        downloader.Release();
        WaitUntil(() => !window.FindControl<StackPanel>("DictionaryDownloadPanel")!.IsVisible);
        Assert.True(File.Exists(locations.DictionaryDatabasePath));
    }

    [AvaloniaFact]
    public void ExtractZipFailureLeavesDestinationUntouched()
    {
        var scratch = NewScratchDirectory();
        var destination = Path.Combine(scratch, "dest");
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, "existing.txt"), "keep");
        var zipPath = MakeCorruptZip(scratch);

        Assert.ThrowsAny<Exception>(() => MainWindow.ExtractZip(zipPath, destination));

        // All-or-nothing: no entry, no half-written staging leftovers.
        Assert.Equal(["existing.txt"],
            Directory.EnumerateFileSystemEntries(destination).Select(e => Path.GetFileName(e)).ToArray());
    }

    [AvaloniaFact]
    public void ExtractZipReplacesExistingEntriesAndSweepsStaleStaging()
    {
        var scratch = NewScratchDirectory();
        var destination = Path.Combine(scratch, "dest");
        var oldEntry = Path.Combine(destination, "uk");
        Directory.CreateDirectory(oldEntry);
        File.WriteAllText(Path.Combine(oldEntry, "junk.mp3"), "old");
        Directory.CreateDirectory(Path.Combine(destination, ".stupiddict-extracting-stale"));
        File.WriteAllText(Path.Combine(destination, ".stupiddict-extracting-stale", "half.db"), "crashed run");

        // Two top-level directories — the audio pack's real shape (uk/ + us/),
        // which is the case where staging must enumerate before moving.
        var source = Path.Combine(scratch, "src");
        Directory.CreateDirectory(Path.Combine(source, "uk"));
        Directory.CreateDirectory(Path.Combine(source, "us"));
        File.WriteAllText(Path.Combine(source, "uk", "cat.mp3"), "new");
        File.WriteAllText(Path.Combine(source, "us", "cat.mp3"), "new");
        var zipPath = Path.Combine(scratch, "pack.zip");
        ZipFile.CreateFromDirectory(source, zipPath);

        MainWindow.ExtractZip(zipPath, destination);

        Assert.True(File.Exists(Path.Combine(destination, "uk", "cat.mp3")));
        Assert.True(File.Exists(Path.Combine(destination, "us", "cat.mp3")));
        Assert.False(File.Exists(Path.Combine(destination, "uk", "junk.mp3")));
        Assert.False(Directory.Exists(Path.Combine(destination, ".stupiddict-extracting-stale")));
    }

    // ---- helpers ----

    private static AppLocations NewLocations(out string dictionaryPath, out string historyPath)
    {
        var directory = NewScratchDirectory();
        dictionaryPath = Path.Combine(directory, "dictionary.db");
        historyPath = Path.Combine(directory, "history.db");
        return new AppLocations(directory, dictionaryPath, historyPath, Path.Combine(directory, "audio"));
    }

    private static string NewScratchDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "stupiddict-uitests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static (string ZipPath, string Hash) MakeDictionaryZipWithHash()
    {
        var releaseDirectory = Path.Combine(NewScratchDirectory(), "release");
        Directory.CreateDirectory(releaseDirectory);
        var dbPath = Path.Combine(releaseDirectory, "dictionary.db");
        using (var db = DictionaryDatabase.Create(dbPath))
            db.InsertWord("cat", "kæt", "kæt", "n:100", "n. 猫", "", 1775, 0, "");
        var zipPath = Path.Combine(NewScratchDirectory(), "dictionary.zip");
        ZipFile.CreateFromDirectory(releaseDirectory, zipPath);
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(zipPath))).ToLowerInvariant();
        return (zipPath, hash);
    }

    /// <summary>
    /// A zip whose entry's deflate stream carries the reserved block type
    /// (BTYPE=11): decoding fails the moment extraction reaches the entry,
    /// after the staging directory is already populated.
    /// </summary>
    private static string MakeCorruptZip(string directory)
    {
        var source = Path.Combine(directory, "src");
        Directory.CreateDirectory(source);
        File.WriteAllBytes(Path.Combine(source, "data.bin"), new byte[8192]);
        var zipPath = Path.Combine(directory, "corrupt.zip");
        ZipFile.CreateFromDirectory(source, zipPath);

        // Single-entry zip: local header at offset 0, entry name at 30.
        var raw = File.ReadAllBytes(zipPath);
        Assert.Equal(0x50, raw[0]);
        Assert.Equal(0x4B, raw[1]);
        var nameLength = raw[26] | (raw[27] << 8);
        var extraLength = raw[28] | (raw[29] << 8);
        raw[30 + nameLength + extraLength] |= 0x06;
        File.WriteAllBytes(zipPath, raw);
        return zipPath;
    }

    private static void WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 300; i++)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition()) return;
            Thread.Sleep(10);
        }
        throw new TimeoutException("Condition not reached within 3s.");
    }

    /// <summary>
    /// Serves scripted bytes per dictionary download call and records what the
    /// destination looked like before each. Other assets are a test bug.
    /// </summary>
    private sealed class ScriptedDownloader(
        Func<int, byte[]> serveDictionaryBytes,
        Func<string, string?> checksum) : IAssetDownloader
    {
        public int RequestCount;
        public List<bool> DestinationExisted = [];

        public Task<DownloadResult> DownloadAsync(string assetName, string destinationFile,
            IProgress<DownloadProgress>? progress, CancellationToken cancellation)
        {
            Assert.Equal(ReleaseAssets.DictionaryAsset, assetName);
            DestinationExisted.Add(File.Exists(destinationFile));
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
            File.WriteAllBytes(destinationFile, serveDictionaryBytes(RequestCount++));
            return Task.FromResult(new DownloadResult(destinationFile, "stub://scripted"));
        }

        public Task<string?> FetchChecksumAsync(string assetName, CancellationToken cancellation) =>
            Task.FromResult(checksum(assetName));
    }

    /// <summary>
    /// One dictionary download that reports a resumed pass (the second bar)
    /// and then holds until <see cref="Release"/> lets the flow finish with a
    /// real zip.
    /// </summary>
    private sealed class PausingResumeDownloader(string zipPath) : IAssetDownloader
    {
        private TaskCompletionSource<DownloadResult>? _pending;
        private string? _destinationFile;

        public void Release()
        {
            // Serve the real zip only now: the flow resumes and extracts it.
            Directory.CreateDirectory(Path.GetDirectoryName(_destinationFile)!);
            File.Copy(zipPath, _destinationFile!, overwrite: true);
            _pending!.SetResult(new DownloadResult(_destinationFile!, "stub://resume"));
        }

        public Task<DownloadResult> DownloadAsync(string assetName, string destinationFile,
            IProgress<DownloadProgress>? progress, CancellationToken cancellation)
        {
            Assert.Equal(ReleaseAssets.DictionaryAsset, assetName);
            _destinationFile = destinationFile;
            const long resumeAt = 50L << 20;
            progress?.Report(new DownloadProgress(resumeAt, 173L << 20, "stub://resume", resumeAt));
            _pending = new TaskCompletionSource<DownloadResult>();
            return _pending.Task;
        }

        public Task<string?> FetchChecksumAsync(string assetName, CancellationToken cancellation) =>
            Task.FromResult<string?>(null);
    }
}
