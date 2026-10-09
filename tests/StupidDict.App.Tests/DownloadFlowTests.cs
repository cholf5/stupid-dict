using System.IO.Compression;
using System.Security.Cryptography;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
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
/// automatic clean retry), the resume label on a second progress pass,
/// all-or-nothing extraction, and kept-zip reuse (an extraction failure
/// retries from the downloaded zip instead of re-downloading it). Flow tests
/// run the dictionary and audio pack downloads against scripted stubs with a
/// scratch download directory; the pure extraction shapes are the static
/// ExtractZip / ImportAudioPack cases below.
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
        var downloader = new ScriptedDownloader(ReleaseAssets.DictionaryAsset,
            callIndex => callIndex == 0 ? [0x1, 0x2, 0x3] : File.ReadAllBytes(zipPath),
            _ => goodHash);
        using var service = new DictionaryService(dictionaryPath, locations.HistoryDatabasePath);
        var window = new MainWindow(service, downloader: downloader, locations: locations, autoDownload: true,
            downloadDirectory: NewScratchDirectory());
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
        var downloadDirectory = NewScratchDirectory();
        var destination = Path.Combine(downloadDirectory, ReleaseAssets.DictionaryAsset);
        var downloader = new ScriptedDownloader(ReleaseAssets.DictionaryAsset,
            _ => new byte[] { 0x1, 0x2, 0x3 },
            _ => new string('0', 64));
        using var service = new DictionaryService(dictionaryPath, locations.HistoryDatabasePath);
        var window = new MainWindow(service, downloader: downloader, locations: locations, autoDownload: true,
            downloadDirectory: downloadDirectory);
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
        var window = new MainWindow(service, downloader: downloader, locations: locations, autoDownload: true,
            downloadDirectory: NewScratchDirectory());
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

    /// <summary>
    /// The kept-zip reuse the Windows audio-pack loop motivated: attempt 1
    /// downloads, verifies and fails at extraction; the retry must pick up
    /// from the kept zip — no second download, no second checksum fetch — and
    /// once the file is made well-formed, install without any network.
    /// </summary>
    [AvaloniaFact]
    public void ExtractionFailureRetryReusesKeptZipWithoutRedownloading()
    {
        var locations = NewLocations(out var dictionaryPath, out _);
        using (var db = DictionaryDatabase.Create(dictionaryPath))
            db.InsertWord("cat", "kæt", "kæt", "n:100", "n. 猫", "", 1775, 0, "");
        var downloadDirectory = NewScratchDirectory();
        var destination = Path.Combine(downloadDirectory, ReleaseAssets.AudioPackAsset);
        var corrupt = MakeCorruptZip(NewScratchDirectory());
        var good = MakeAudioPackZip(NewScratchDirectory(), "pack.zip", "cat.mp3");
        var corruptBytes = File.ReadAllBytes(corrupt);
        var downloader = new ScriptedDownloader(ReleaseAssets.AudioPackAsset,
            _ => corruptBytes, _ => HashFile(corrupt));
        var window = new MainWindow(new DictionaryService(dictionaryPath, locations.HistoryDatabasePath),
            downloader: downloader, locations: locations, autoDownload: true,
            downloadDirectory: downloadDirectory);
        window.Show();

        // Attempt 1: download and checksum pass, extraction dies on the
        // corrupt entry — the zip stays behind for the retry.
        WaitUntil(() => window.FindControl<Button>("AudioPackActionButton")!.Content as string
            == Translations.Instance.Retry);
        Assert.Equal(1, downloader.RequestCount);
        Assert.True(File.Exists(destination));

        // The kept zip is ours to fix in place — the same file, now
        // well-formed, like an app update fixing what broke extraction.
        File.WriteAllBytes(destination, File.ReadAllBytes(good));
        window.FindControl<Button>("AudioPackActionButton")!
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        WaitUntil(() => File.Exists(Path.Combine(locations.AudioDirectory, "uk", "cat.mp3")));
        Assert.Equal(1, downloader.RequestCount);
        Assert.Equal(1, downloader.ChecksumCalls);
        Assert.True(File.Exists(Path.Combine(locations.AudioDirectory, "us", "cat.mp3")));
    }

    /// <summary>
    /// A zip kept by a previous session (app closed mid-extract) installs on
    /// the next launch without a download when its checksum still matches.
    /// </summary>
    [AvaloniaFact]
    public void CrossSessionKeptZipReusedWithoutDownloadWhenChecksumMatches()
    {
        var locations = NewLocations(out var dictionaryPath, out _);
        using (var db = DictionaryDatabase.Create(dictionaryPath))
            db.InsertWord("cat", "kæt", "kæt", "n:100", "n. 猫", "", 1775, 0, "");
        var downloadDirectory = NewScratchDirectory();
        Directory.CreateDirectory(downloadDirectory);
        var good = MakeAudioPackZip(NewScratchDirectory(), "pack.zip", "cat.mp3");
        var destination = Path.Combine(downloadDirectory, ReleaseAssets.AudioPackAsset);
        File.Copy(good, destination);
        var downloader = new ScriptedDownloader(ReleaseAssets.AudioPackAsset,
            _ => File.ReadAllBytes(good), _ => HashFile(good));
        var window = new MainWindow(new DictionaryService(dictionaryPath, locations.HistoryDatabasePath),
            downloader: downloader, locations: locations, autoDownload: true,
            downloadDirectory: downloadDirectory);
        window.Show();

        WaitUntil(() => File.Exists(Path.Combine(locations.AudioDirectory, "uk", "cat.mp3")));

        Assert.Equal(0, downloader.RequestCount);
        Assert.Equal(1, downloader.ChecksumCalls);
    }

    /// <summary>
    /// A kept zip whose checksum no longer matches (the remote asset was
    /// replaced) is purged and the replacement downloaded — reuse must never
    /// extract stale data past a published checksum.
    /// </summary>
    [AvaloniaFact]
    public void CrossSessionStaleZipFailsChecksumAndIsPurgedAndReplaced()
    {
        var locations = NewLocations(out var dictionaryPath, out _);
        using (var db = DictionaryDatabase.Create(dictionaryPath))
            db.InsertWord("cat", "kæt", "kæt", "n:100", "n. 猫", "", 1775, 0, "");
        var downloadDirectory = NewScratchDirectory();
        Directory.CreateDirectory(downloadDirectory);
        var stale = MakeAudioPackZip(NewScratchDirectory(), "stale.zip", "old.mp3");
        var fresh = MakeAudioPackZip(NewScratchDirectory(), "fresh.zip", "cat.mp3");
        var destination = Path.Combine(downloadDirectory, ReleaseAssets.AudioPackAsset);
        File.Copy(stale, destination);
        var downloader = new ScriptedDownloader(ReleaseAssets.AudioPackAsset,
            _ => File.ReadAllBytes(fresh), _ => HashFile(fresh));
        var window = new MainWindow(new DictionaryService(dictionaryPath, locations.HistoryDatabasePath),
            downloader: downloader, locations: locations, autoDownload: true,
            downloadDirectory: downloadDirectory);
        window.Show();

        WaitUntil(() => File.Exists(Path.Combine(locations.AudioDirectory, "uk", "cat.mp3")));

        Assert.Equal(1, downloader.RequestCount);
        Assert.False(downloader.DestinationExisted[0]);
        Assert.False(File.Exists(Path.Combine(locations.AudioDirectory, "uk", "old.mp3")));
    }

    /// <summary>
    /// Cancelling the audio pack download must hand the (single) action
    /// button back usable: the cancel click disables it ("取消中…") and the
    /// cancelled flow is the last thing that runs — without a re-enable the
    /// button shows 下载 but never responds again, blocking every later
    /// attempt until the app restarts.
    /// </summary>
    [AvaloniaFact]
    public void AudioPackDownloadCancelledRestoresEnabledActionButton()
    {
        var locations = NewLocations(out var dictionaryPath, out _);
        using (DictionaryDatabase.Create(dictionaryPath)) { }
        var window = new MainWindow(new DictionaryService(dictionaryPath, locations.HistoryDatabasePath),
            downloader: new NeverFinishingDownloader(), locations: locations, autoDownload: true,
            downloadDirectory: NewScratchDirectory());
        window.Show();

        var button = window.FindControl<Button>("AudioPackActionButton")!;
        WaitUntil(() => button.Content as string == Translations.Instance.Cancel);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        WaitUntil(() => button.Content as string == Translations.Instance.AudioPackDownloadButton);
        Assert.True(button.IsEnabled);
        Assert.Equal(Translations.Instance.AudioPackCancelled,
            window.FindControl<TextBlock>("AudioPackStatus")!.Text);
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

    [AvaloniaFact]
    public void AudioPackImportRejectsZipWithoutPackEntries()
    {
        var scratch = NewScratchDirectory();
        var source = Path.Combine(scratch, "src");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "dictionary.db"), "not a pack");
        var zipPath = Path.Combine(scratch, "wrong.zip");
        ZipFile.CreateFromDirectory(source, zipPath);
        var audio = Path.Combine(scratch, "audio");

        var ex = Assert.Throws<InvalidOperationException>(
            () => MainWindow.ImportAudioPack(zipPath, audio));

        Assert.Equal(Translations.Instance.ImportMissingPack, ex.Message);
        // A wrong zip must not scatter its contents into the audio directory.
        Assert.False(Directory.Exists(audio));
    }

    [AvaloniaFact]
    public void AudioPackImportExtractsPack()
    {
        var scratch = NewScratchDirectory();
        var source = Path.Combine(scratch, "src");
        Directory.CreateDirectory(Path.Combine(source, "uk"));
        Directory.CreateDirectory(Path.Combine(source, "us"));
        File.WriteAllText(Path.Combine(source, "uk", "cat.mp3"), "x");
        File.WriteAllText(Path.Combine(source, "us", "cat.mp3"), "x");
        var zipPath = Path.Combine(scratch, "pack.zip");
        ZipFile.CreateFromDirectory(source, zipPath);

        var audio = Path.Combine(scratch, "audio");
        MainWindow.ImportAudioPack(zipPath, audio);

        Assert.True(File.Exists(Path.Combine(audio, "uk", "cat.mp3")));
        Assert.True(File.Exists(Path.Combine(audio, "us", "cat.mp3")));
    }

    [AvaloniaFact]
    public void AudioPackImportAcceptsReservedDeviceNameEntry()
    {
        var scratch = NewScratchDirectory();
        var zipPath = Path.Combine(scratch, "pack.zip");
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            AddEntry(archive, "us/con.mp3", "us-con");
            AddEntry(archive, "uk/con.mp3", "uk-con");
            AddEntry(archive, "us/cat.mp3", "us-cat");
        }

        var audio = Path.Combine(scratch, "audio");
        MainWindow.ImportAudioPack(zipPath, audio);

        // "con" is a real headword (and so are aux/nul/com1 lookalikes in
        // principle): a legitimate pack carries device-name files, and the
        // zip-slip gate must not mistake them for path attacks.
        Assert.Equal("us-con", ReadExtracted(Path.Combine(audio, "us", "con.mp3")));
        Assert.Equal("uk-con", ReadExtracted(Path.Combine(audio, "uk", "con.mp3")));
        Assert.Equal("us-cat", ReadExtracted(Path.Combine(audio, "us", "cat.mp3")));
    }

    [AvaloniaFact]
    public void ExtractZipRejectsParentTraversalEntry()
    {
        var scratch = NewScratchDirectory();
        var destination = Path.Combine(scratch, "dest");
        Directory.CreateDirectory(destination);
        var zipPath = Path.Combine(scratch, "evil.zip");
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            AddEntry(archive, "us/ok.mp3", "ok");
            AddEntry(archive, "../evil.mp3", "evil");
        }

        var ex = Assert.Throws<InvalidOperationException>(() => MainWindow.ExtractZip(zipPath, destination));

        Assert.Contains("../evil.mp3", ex.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(destination));
    }

    [AvaloniaFact]
    public void ExtractZipRejectsRootedEntry()
    {
        var scratch = NewScratchDirectory();
        var destination = Path.Combine(scratch, "dest");
        Directory.CreateDirectory(destination);
        var zipPath = Path.Combine(scratch, "evil.zip");
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            AddEntry(archive, "us/ok.mp3", "ok");
            AddEntry(archive, "/abs/evil.mp3", "evil");
        }

        var ex = Assert.Throws<InvalidOperationException>(() => MainWindow.ExtractZip(zipPath, destination));

        Assert.Contains("/abs/evil.mp3", ex.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(destination));
    }

    // ---- helpers ----

    private static AppLocations NewLocations(out string dictionaryPath, out string historyPath)
    {
        var directory = NewScratchDirectory();
        dictionaryPath = Path.Combine(directory, "dictionary.db");
        historyPath = Path.Combine(directory, "history.db");
        return new AppLocations(directory, dictionaryPath, historyPath, Path.Combine(directory, "audio"));
    }

    private static void AddEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream);
        writer.Write(content);
    }

    private static string NewScratchDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "stupiddict-uitests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>
    /// Reads an extracted file back. Reserved DOS device names must go through
    /// the \\?\ prefix: an ordinary Win32 path redirects us/con.mp3 to the CON
    /// device, where File.Exists says false and File.ReadAllText blocks on the
    /// console forever (which hung the whole suite on Windows 10).
    /// </summary>
    private static string ReadExtracted(string path) =>
        File.ReadAllText(OperatingSystem.IsWindows() ? MainWindow.ToExtendedPath(path) : path);

    private static (string ZipPath, string Hash) MakeDictionaryZipWithHash()
    {
        var releaseDirectory = Path.Combine(NewScratchDirectory(), "release");
        Directory.CreateDirectory(releaseDirectory);
        var dbPath = Path.Combine(releaseDirectory, "dictionary.db");
        using (var db = DictionaryDatabase.Create(dbPath))
            db.InsertWord("cat", "kæt", "kæt", "n:100", "n. 猫", "", 1775, 0, "");
        var zipPath = Path.Combine(NewScratchDirectory(), "dictionary.zip");
        ZipFile.CreateFromDirectory(releaseDirectory, zipPath);
        return (zipPath, HashFile(zipPath));
    }

    private static string HashFile(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    /// <summary>
    /// An audio pack zip with the real shape (uk/ + us/ at the root), the
    /// entry name choosing the content so tests can tell packs apart.
    /// </summary>
    private static string MakeAudioPackZip(string directory, string zipFileName, string entryName)
    {
        var source = Path.Combine(directory, "src-" + Path.GetFileNameWithoutExtension(zipFileName));
        Directory.CreateDirectory(Path.Combine(source, "uk"));
        Directory.CreateDirectory(Path.Combine(source, "us"));
        File.WriteAllText(Path.Combine(source, "uk", entryName), "x");
        File.WriteAllText(Path.Combine(source, "us", entryName), "x");
        var zipPath = Path.Combine(directory, zipFileName);
        ZipFile.CreateFromDirectory(source, zipPath);
        return zipPath;
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
    /// Serves scripted bytes per download call and records what the
    /// destination looked like before each, plus how often the checksum
    /// source was consulted (the kept-zip reuse must not consult it again
    /// within a session). Other assets are a test bug.
    /// </summary>
    private sealed class ScriptedDownloader(
        string expectedAsset,
        Func<int, byte[]> serveBytes,
        Func<string, string?> checksum) : IAssetDownloader
    {
        public int RequestCount;
        public int ChecksumCalls;
        public List<bool> DestinationExisted = [];

        public Task<DownloadResult> DownloadAsync(string assetName, string destinationFile,
            IProgress<DownloadProgress>? progress, CancellationToken cancellation)
        {
            Assert.Equal(expectedAsset, assetName);
            DestinationExisted.Add(File.Exists(destinationFile));
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
            File.WriteAllBytes(destinationFile, serveBytes(RequestCount++));
            return Task.FromResult(new DownloadResult(destinationFile, "stub://scripted"));
        }

        public Task<string?> FetchChecksumAsync(string assetName, CancellationToken cancellation)
        {
            ChecksumCalls++;
            return Task.FromResult(checksum(assetName));
        }
    }

    /// <summary>
    /// A download that only ends when the flow cancels it: exercises the
    /// cancellation path (status text, button state) without serving bytes.
    /// </summary>
    private sealed class NeverFinishingDownloader : IAssetDownloader
    {
        public async Task<DownloadResult> DownloadAsync(string assetName, string destinationFile,
            IProgress<DownloadProgress>? progress, CancellationToken cancellation)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
            throw new InvalidOperationException("unreachable: the delay only ends by cancellation");
        }

        public Task<string?> FetchChecksumAsync(string assetName, CancellationToken cancellation) =>
            Task.FromResult<string?>(null);
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
