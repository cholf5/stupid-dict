using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
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
/// all-or-nothing extraction, kept-zip reuse (an extraction failure
/// retries from the downloaded zip instead of re-downloading it), and the
/// terminal-state cleanups (the bar hides on cancel/failure; the success
/// path's zip delete is best-effort after the install, B-009). Flow tests
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
        // B-009: the failure terminal state hides the bar too — it used to
        // stay visible, frozen at whatever the last progress report drew.
        Assert.False(window.FindControl<ProgressBar>("DictionaryDownloadBar")!.IsVisible);
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
    /// A pack database kept by a previous session (download finished, app
    /// closed before it landed) installs on the next launch without a
    /// download when its checksum still matches. Since data-3 the audio
    /// asset IS the database — the download flow validates and places it,
    /// no conversion pass.
    /// </summary>
    [AvaloniaFact]
    public void CrossSessionKeptDatabaseReusedWithoutDownloadWhenChecksumMatches()
    {
        var locations = NewLocations(out var dictionaryPath, out _);
        using (var db = DictionaryDatabase.Create(dictionaryPath))
            db.InsertWord("cat", "kæt", "kæt", "n:100", "n. 猫", "", 1775, 0, "");
        var downloadDirectory = NewScratchDirectory();
        Directory.CreateDirectory(downloadDirectory);
        var good = MakeAudioPackDb(NewScratchDirectory());
        var destination = Path.Combine(downloadDirectory, ReleaseAssets.AudioPackAsset);
        File.Copy(good, destination);
        var downloader = new ScriptedDownloader(ReleaseAssets.AudioPackAsset,
            _ => File.ReadAllBytes(good), _ => HashFile(good));
        var window = new MainWindow(new DictionaryService(dictionaryPath, locations.HistoryDatabasePath),
            downloader: downloader, locations: locations, autoDownload: true,
            downloadDirectory: downloadDirectory);
        window.Show();

        WaitUntil(() => File.Exists(locations.AudioPackDatabasePath));

        Assert.Equal(0, downloader.RequestCount);
        Assert.Equal(1, downloader.ChecksumCalls);
    }

    /// <summary>
    /// A kept pack database whose checksum no longer matches (the remote
    /// asset was replaced) is purged and the replacement downloaded — reuse
    /// must never install stale data past a published checksum.
    /// </summary>
    [AvaloniaFact]
    public void CrossSessionStaleDatabaseFailsChecksumAndIsPurgedAndReplaced()
    {
        var locations = NewLocations(out var dictionaryPath, out _);
        using (var db = DictionaryDatabase.Create(dictionaryPath))
            db.InsertWord("cat", "kæt", "kæt", "n:100", "n. 猫", "", 1775, 0, "");
        var downloadDirectory = NewScratchDirectory();
        Directory.CreateDirectory(downloadDirectory);
        var stale = MakeAudioPackDb(NewScratchDirectory(), "old.mp3");
        var fresh = MakeAudioPackDb(NewScratchDirectory());
        var destination = Path.Combine(downloadDirectory, ReleaseAssets.AudioPackAsset);
        File.Copy(stale, destination);
        var downloader = new ScriptedDownloader(ReleaseAssets.AudioPackAsset,
            _ => File.ReadAllBytes(fresh), _ => HashFile(fresh));
        var window = new MainWindow(new DictionaryService(dictionaryPath, locations.HistoryDatabasePath),
            downloader: downloader, locations: locations, autoDownload: true,
            downloadDirectory: downloadDirectory);
        window.Show();

        WaitUntil(() => File.Exists(locations.AudioPackDatabasePath));

        Assert.Equal(1, downloader.RequestCount);
        Assert.False(downloader.DestinationExisted[0]);
        Assert.False(PackRowExists(locations.AudioPackDatabasePath, "old"));
    }

    /// <summary>
    /// A zip kept by a previous session (app closed mid-extract) installs on
    /// the next launch without a download when its checksum still matches.
    /// </summary>
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

    /// <summary>
    /// B-009 TC-002: cancelling mid-download hides the progress bar. The bar
    /// used to survive the cancel frozen at the last percentage — the
    /// finally only restored the three buttons. The bar is drawn determinate
    /// first (one progress tick), so the hide cannot pass vacuously on a bar
    /// that never showed.
    /// </summary>
    [AvaloniaFact]
    public void DictionaryDownloadCancelledHidesProgressBar()
    {
        var locations = NewLocations(out var dictionaryPath, out _);
        // No db on disk: the download panel owns the window and the download
        // starts on construction; with the dictionary never installing, the
        // audio pack flow never starts either, so the two bars cannot
        // interact.
        var window = new MainWindow(new DictionaryService(dictionaryPath, locations.HistoryDatabasePath),
            downloader: new HangAfterProgressDownloader(), locations: locations, autoDownload: true,
            downloadDirectory: NewScratchDirectory());
        window.Show();

        var bar = window.FindControl<ProgressBar>("DictionaryDownloadBar")!;
        var cancel = window.FindControl<Button>("CancelDictionaryButton")!;
        WaitUntil(() => bar.IsVisible && !bar.IsIndeterminate && bar.Value > 40);
        cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        WaitUntil(() => !bar.IsVisible);
        Assert.Equal(Translations.Instance.DownloadCancelled,
            window.FindControl<TextBlock>("DictionaryDownloadStatus")!.Text);
        Assert.True(window.FindControl<Button>("DownloadDictionaryButton")!.IsVisible);
        Assert.False(cancel.IsVisible);
    }

    /// <summary>
    /// The B-008 death-loop story (corrupt bytes + NO published checksum →
    /// purge and redownload) belonged to the zip era, where the per-entry CRC
    /// proved byte damage. Since data-3 the audio asset is a database:
    /// published checksums are the integrity defense (mismatch purges at
    /// download — see CrossSessionStaleDatabase… and
    /// ChecksumPurgeFailure…), and a structurally invalid database with no
    /// published checksum fails validation, which is deliberately a
    /// non-damage "keep and retry" — redownloading the same bytes could
    /// never fix it. The scenario is unreachable; the test retired with the
    /// zip download path.
    /// </summary>
    /// <summary>
    /// B-009 TC-002, audio side: the download delete used to run before the
    /// completion action, so a transient lock on the just-written file
    /// surfaced as "发音包导入失败" with the pack installed but the panel
    /// stuck on Retry — and that retry is short-circuited by
    /// AudioPackInstalled(), freezing the panel until restart. The
    /// completion action lands first now; the delete is best-effort cleanup.
    /// Fault injection follows the ZipDeleteFailure… shape — pure filesystem
    /// state that makes File.Delete throw on every platform: Windows blocks
    /// deletion via the read-only file attribute, unix via a non-writable
    /// containing directory.
    /// </summary>
    [AvaloniaFact]
    public void AudioPackDeleteFailureStillCompletesInstall()
    {
        var locations = NewLocations(out var dictionaryPath, out _);
        // The dictionary db exists, so the constructor starts the audio pack
        // download directly and the dictionary flow never runs.
        using (DictionaryDatabase.Create(dictionaryPath)) { }
        var downloadDirectory = NewScratchDirectory();
        var destination = Path.Combine(downloadDirectory, ReleaseAssets.AudioPackAsset);
        var pack = MakeAudioPackDb(NewScratchDirectory());
        var downloader = new UndeletableZipDownloader(pack, ReleaseAssets.AudioPackAsset);
        var window = new MainWindow(new DictionaryService(dictionaryPath, locations.HistoryDatabasePath),
            downloader: downloader, locations: locations, autoDownload: true,
            downloadDirectory: downloadDirectory);
        window.Show();

        // The completion action landed despite the deletion fault: the panel
        // went away and the pack is installed.
        WaitUntil(() => !window.FindControl<Border>("AudioPackPanel")!.IsVisible);

        Assert.True(File.Exists(locations.AudioPackDatabasePath));
        // The last status write is the import state line — never the
        // import-failure text, never the Retry button: the misleading
        // terminal state the unguarded delete used to produce.
        Assert.Equal(Translations.Instance.Importing,
            window.FindControl<TextBlock>("AudioPackStatus")!.Text);
        // The fault really fired: the download is still on disk, kept for
        // the reuse path.
        Assert.True(File.Exists(destination));
    }

    /// <summary>
    /// The purge inside the checksum-mismatch recovery is best-effort: a
    /// transient Windows lock (antivirus, indexer) makes File.Delete throw,
    /// and one escaping this async void would kill the process. Fault
    /// injection: the ".part" path is made a DIRECTORY, so the purge's second
    /// File.Delete throws on every platform — the flow must degrade to the
    /// plain import-failure text with the real error (a "deleted" claim would
    /// be a lie) and still converge on the next retry. The downloaded file
    /// itself is purged before the ".part" throws, so the retry downloads
    /// fresh instead of re-verifying the rejected bytes.
    /// </summary>
    [AvaloniaFact]
    public void ChecksumPurgeFailureDegradesToPlainFailedTextAndFlowSurvives()
    {
        var locations = NewLocations(out var dictionaryPath, out _);
        using (var db = DictionaryDatabase.Create(dictionaryPath))
            db.InsertWord("cat", "kæt", "kæt", "n:100", "n. 猫", "", 1775, 0, "");
        var downloadDirectory = NewScratchDirectory();
        var destination = Path.Combine(downloadDirectory, ReleaseAssets.AudioPackAsset);
        // The fault: a directory where the purge expects the ".part" file.
        Directory.CreateDirectory(destination + ".part");
        var corrupt = NewScratchDirectory();
        File.WriteAllBytes(Path.Combine(corrupt, "garbage.db"), [0xDE, 0xAD, 0xBE, 0xEF]);
        var good = MakeAudioPackDb(NewScratchDirectory());
        var corruptBytes = File.ReadAllBytes(Path.Combine(corrupt, "garbage.db"));
        var downloader = new ScriptedDownloader(ReleaseAssets.AudioPackAsset,
            callIndex => callIndex == 0 ? corruptBytes : File.ReadAllBytes(good),
            _ => HashFile(good));
        var window = new MainWindow(new DictionaryService(dictionaryPath, locations.HistoryDatabasePath),
            downloader: downloader, locations: locations, autoDownload: true,
            downloadDirectory: downloadDirectory);
        window.Show();

        // Attempt 1: the download verifies against the WRONG checksum — the
        // purge dies on the locked ".part" after deleting the file itself —
        // the flow surfaces the REAL error through the plain import-failure
        // text and does not claim a deletion that did not happen.
        WaitUntil(() => window.FindControl<Button>("AudioPackActionButton")!.Content as string
            == Translations.Instance.Retry);
        var status = window.FindControl<TextBlock>("AudioPackStatus")!.Text!;
        Assert.True(status.StartsWith(string.Format(Translations.Instance.AudioPackImportFailedFormat, "")),
            $"unexpected status: {status}");
        Assert.NotEqual(Translations.Instance.ExtractCorruptPurged, status);
        Assert.True(Directory.Exists(destination + ".part"));

        // The flow is alive: the next retry downloads again and installs.
        window.FindControl<Button>("AudioPackActionButton")!
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        WaitUntil(() => File.Exists(locations.AudioPackDatabasePath));
        Assert.Equal(2, downloader.RequestCount);
        Assert.False(downloader.DestinationExisted[1]);
    }

    /// <summary>
    /// B-009 TC-001: the success path's zip delete used to run before
    /// FinishDictionarySetup, so a transient lock on the just-written file
    /// (Windows antivirus, indexer) surfaced as "解压失败" with a working
    /// dictionary left uninstalled and the download panel stuck. The delete
    /// is best-effort cleanup after the install now. Fault injection follows
    /// the PurgeFailure… shape — pure filesystem state that makes File.Delete
    /// throw on every platform: Windows blocks deletion via the read-only
    /// file attribute, unix via a non-writable containing directory.
    /// </summary>
    [AvaloniaFact]
    public void ZipDeleteFailureStillCompletesSetup()
    {
        var locations = NewLocations(out var dictionaryPath, out _);
        var (zipPath, _) = MakeDictionaryZipWithHash();
        var downloadDirectory = NewScratchDirectory();
        var destination = Path.Combine(downloadDirectory, ReleaseAssets.DictionaryAsset);
        var downloader = new UndeletableZipDownloader(zipPath, ReleaseAssets.DictionaryAsset);
        var window = new MainWindow(new DictionaryService(dictionaryPath, locations.HistoryDatabasePath),
            downloader: downloader, locations: locations, autoDownload: true,
            downloadDirectory: downloadDirectory);
        window.Show();

        // Setup completed despite the deletion fault: the panel went away
        // and the dictionary is installed.
        WaitUntil(() => !window.FindControl<StackPanel>("DictionaryDownloadPanel")!.IsVisible);

        Assert.True(File.Exists(locations.DictionaryDatabasePath));
        // The success path's last status write is "解压中…" — never the
        // extraction-failure text the unguarded delete used to produce.
        Assert.Equal(Translations.Instance.Extracting,
            window.FindControl<TextBlock>("DictionaryDownloadStatus")!.Text);
        // The fault really fired: the zip is still on disk, kept for the
        // reuse path.
        Assert.True(File.Exists(destination));
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
        var db = Path.Combine(scratch, "audio-pack.db");

        var ex = Assert.Throws<InvalidOperationException>(
            () => MainWindow.ImportAudioPack(zipPath, db));

        Assert.Equal(Translations.Instance.ImportMissingPack, ex.Message);
        // A wrong zip must not leave a pack behind.
        Assert.False(File.Exists(db));
    }

    [AvaloniaFact]
    public void AudioPackImportConvertsPackIntoDatabase()
    {
        var scratch = NewScratchDirectory();
        var source = Path.Combine(scratch, "src");
        Directory.CreateDirectory(Path.Combine(source, "uk"));
        Directory.CreateDirectory(Path.Combine(source, "us"));
        File.WriteAllText(Path.Combine(source, "uk", "cat.mp3"), "uk-cat");
        File.WriteAllText(Path.Combine(source, "us", "cat.mp3"), "us-cat");
        var zipPath = Path.Combine(scratch, "pack.zip");
        ZipFile.CreateFromDirectory(source, zipPath);

        var db = Path.Combine(scratch, "audio-pack.db");
        MainWindow.ImportAudioPack(zipPath, db);

        var row = ReadPackRow(db, "cat");
        Assert.Equal("uk-cat", Encoding.UTF8.GetString(row.Uk!));
        Assert.Equal("us-cat", Encoding.UTF8.GetString(row.Us!));
    }

    [AvaloniaFact]
    public void AudioPackImportTakesReservedDeviceNameEntries()
    {
        var scratch = NewScratchDirectory();
        var zipPath = Path.Combine(scratch, "pack.zip");
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            AddEntry(archive, "us/con.mp3", "us-con");
            AddEntry(archive, "uk/con.mp3", "uk-con");
            AddEntry(archive, "us/cat.mp3", "us-cat");
        }

        var db = Path.Combine(scratch, "audio-pack.db");
        MainWindow.ImportAudioPack(zipPath, db);

        // "con" is a real headword (and so are aux/nul/com1 lookalikes in
        // principle): a legitimate pack carries device-name entries. In the
        // database they are plain text keys — no reserved name ever
        // materializes on the filesystem, which closes the Windows
        // device-name class for new installs entirely.
        var con = ReadPackRow(db, "con");
        Assert.Equal("uk-con", Encoding.UTF8.GetString(con.Uk!));
        Assert.Equal("us-con", Encoding.UTF8.GetString(con.Us!));
        Assert.Equal("us-cat", Encoding.UTF8.GetString(ReadPackRow(db, "cat").Us!));
        // The loose layout is not created by imports anymore.
        Assert.False(Directory.Exists(Path.Combine(scratch, "audio")));
    }

    [AvaloniaFact]
    public void AudioPackImportAcceptsPackDatabase()
    {
        var scratch = NewScratchDirectory();
        var zipPath = Path.Combine(scratch, "pack.zip");
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            AddEntry(archive, "uk/cat.mp3", "uk-cat");
            AddEntry(archive, "us/cat.mp3", "us-cat");
        }
        var source = Path.Combine(scratch, "picked.db");
        AudioPackConverter.ConvertZipToDatabase(zipPath, source);
        var target = Path.Combine(scratch, "audio-pack.db");

        MainWindow.ImportAudioPack(source, target);

        Assert.True(File.Exists(target));
        Assert.True(File.Exists(source)); // the user's file is copied, not moved
        Assert.Equal("uk-cat", Encoding.UTF8.GetString(ReadPackRow(target, "cat").Uk!));
    }

    [AvaloniaFact]
    public void AudioPackImportRejectsInvalidDatabase()
    {
        var scratch = NewScratchDirectory();
        var source = Path.Combine(scratch, "fake.db");
        File.WriteAllText(source, "this is not sqlite");
        var target = Path.Combine(scratch, "audio-pack.db");

        var ex = Assert.Throws<InvalidOperationException>(
            () => MainWindow.ImportAudioPack(source, target));

        Assert.Equal(Translations.Instance.ImportInvalidDatabase, ex.Message);
        Assert.False(File.Exists(target));
    }

    [AvaloniaFact]
    public void AudioPackImportRejectsOtherFormats()
    {
        var scratch = NewScratchDirectory();
        var source = Path.Combine(scratch, "pack.rar");
        File.WriteAllText(source, "x");
        var target = Path.Combine(scratch, "audio-pack.db");

        var ex = Assert.Throws<InvalidOperationException>(
            () => MainWindow.ImportAudioPack(source, target));

        Assert.Equal(Translations.Instance.ImportUnsupportedFormat, ex.Message);
        Assert.False(File.Exists(target));
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

    /// <summary>
    /// B-008: ZipFile does not verify entry CRCs while streaming — a
    /// corrupted-but-decodable entry (flipped byte inside a stored entry)
    /// reads back silently on .NET 10 — so ExtractZip computes the CRC over
    /// the decompressed bytes itself and refuses the entry. This is the
    /// per-entry backstop the checksum-less reuse path stands on; without it
    /// a damaged kept zip installs corrupted content with no error at all.
    /// </summary>
    [AvaloniaFact]
    public void ExtractZipRejectsEntryWithCrcMismatch()
    {
        var scratch = NewScratchDirectory();
        var destination = Path.Combine(scratch, "dest");
        Directory.CreateDirectory(destination);
        var zipPath = MakeCrcMismatchZip(scratch);

        var ex = Assert.Throws<InvalidDataException>(() => MainWindow.ExtractZip(zipPath, destination));

        Assert.Contains("cat.mp3", ex.Message);
        // All-or-nothing still holds: nothing lands in the destination.
        Assert.Empty(Directory.EnumerateFileSystemEntries(destination));
    }

    /// <summary>Regression: intact entries — stored and deflated alike — extract unchanged.</summary>
    [AvaloniaFact]
    public void ExtractZipAcceptsWellFormedEntriesWithoutFalseAlarm()
    {
        var scratch = NewScratchDirectory();
        var destination = Path.Combine(scratch, "dest");
        var zipPath = Path.Combine(scratch, "mixed.zip");
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            AddStoredEntry(archive, "uk/cat.mp3", "stored-cat");
            AddEntry(archive, "us/cat.mp3", "deflated-cat");
            archive.CreateEntry("empty.txt"); // zero bytes: CRC 0, no data
        }

        MainWindow.ExtractZip(zipPath, destination);

        Assert.Equal("stored-cat", File.ReadAllText(Path.Combine(destination, "uk", "cat.mp3")));
        Assert.Equal("deflated-cat", File.ReadAllText(Path.Combine(destination, "us", "cat.mp3")));
        Assert.True(File.Exists(Path.Combine(destination, "empty.txt")));
    }

    /// <summary>The CRC-32 algorithm itself: known answer, composition, zip agreement.
    /// AvaloniaFact like every test here: the class constructor's SetLanguage
    /// needs the headless application bootstrapped.</summary>
    [AvaloniaFact]
    public void Crc32MatchesKnownAnswerAndZipEntries()
    {
        // The classic CRC-32 check value.
        Assert.Equal(0xCBF43926u, Crc32.Compute("123456789"u8));

        // Incremental updates compose: chunked feeding equals one-shot.
        var data = "The quick brown fox jumps over the lazy dog"u8;
        var incremental = Crc32.Value(Crc32.Update(Crc32.Update(Crc32.InitialState, data[..10]), data[10..]));
        Assert.Equal(Crc32.Compute(data), incremental);

        // And it agrees with what ZipArchive publishes for a real entry.
        var scratch = NewScratchDirectory();
        var zipPath = Path.Combine(scratch, "roundtrip.zip");
        var payload = "some arbitrary entry payload for the crc roundtrip"u8;
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            using var stream = archive.CreateEntry("payload.bin").Open();
            stream.Write(payload);
        }
        using var read = ZipFile.OpenRead(zipPath);
        var entry = read.Entries[0];
        using var decompressed = entry.Open();
        using var buffer = new MemoryStream();
        decompressed.CopyTo(buffer);
        Assert.Equal(Crc32.Compute(payload), entry.Crc32);
        Assert.Equal(Crc32.Compute(payload), Crc32.Compute(buffer.ToArray()));
    }

    // ---- helpers ----

    private static AppLocations NewLocations(out string dictionaryPath, out string historyPath)
    {
        var directory = NewScratchDirectory();
        dictionaryPath = Path.Combine(directory, "dictionary.db");
        historyPath = Path.Combine(directory, "history.db");
        return new AppLocations(directory, dictionaryPath, historyPath, Path.Combine(directory, "audio"),
            Path.Combine(directory, "audio-pack.db"));
    }

    private static void AddEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream);
        writer.Write(content);
    }

    private static void AddStoredEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream);
        writer.Write(content);
    }

    /// <summary>
    /// A zip whose only entry decodes fine but does not match its published
    /// CRC: the stored entry's data is flipped after the fact, so the damage
    /// is invisible to decompression — exactly the corruption class a B-006
    /// weld or a truncated redownload produces.
    /// </summary>
    private static string MakeCrcMismatchZip(string directory)
    {
        var zipPath = Path.Combine(directory, "crc-mismatch.zip");
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            AddStoredEntry(archive, "uk/cat.mp3", "cat sound bytes");
        var raw = File.ReadAllBytes(zipPath);
        var nameLength = raw[26] | (raw[27] << 8);
        var extraLength = raw[28] | (raw[29] << 8);
        raw[30 + nameLength + extraLength] ^= 0xFF;
        File.WriteAllBytes(zipPath, raw);
        return zipPath;
    }

    private static string NewScratchDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "stupiddict-uitests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>Reads one word's blobs back from a pack database.</summary>
    private static (byte[]? Uk, byte[]? Us) ReadPackRow(string db, string word)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT uk, us FROM audio WHERE word = $w";
        command.Parameters.AddWithValue("$w", word);
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read(), $"word '{word}' missing from {db}");
        return (reader.IsDBNull(0) ? null : (byte[])reader.GetValue(0),
                reader.IsDBNull(1) ? null : (byte[])reader.GetValue(1));
    }

    private static bool PackRowExists(string db, string word)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM audio WHERE word = $w";
        command.Parameters.AddWithValue("$w", word);
        return (long)command.ExecuteScalar()! > 0;
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

    /// <summary>Builds a small pack database through the real converter.</summary>
    private static string MakeAudioPackDb(string directory, string entryName = "cat.mp3")
    {
        var zip = MakeAudioPackZip(directory, "pack.zip", entryName);
        var db = Path.Combine(directory, "audio-pack.db");
        AudioPackConverter.ConvertZipToDatabase(zip, db);
        return db;
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
    /// Serves a real asset zip and then makes it undeletable on every
    /// platform — the stand-in for a Windows antivirus/indexer lock on the
    /// just-written file (B-009): Windows blocks deletion via the read-only
    /// file attribute, unix via a non-writable containing directory. The
    /// checksum source answers null, so the download installs without a
    /// verification round trip. Requests for any other asset are declined
    /// with a plain exception, NOT an xunit assert: the flows' catch chains
    /// are designed to absorb those, and a swallowed assert failure would be
    /// indistinguishable from a passing flow (the dictionary-side test's
    /// auto-queued audio pack download rides this decline).
    /// </summary>
    private sealed class UndeletableZipDownloader(string zipPath, string expectedAsset) : IAssetDownloader
    {
        public Task<DownloadResult> DownloadAsync(string assetName, string destinationFile,
            IProgress<DownloadProgress>? progress, CancellationToken cancellation)
        {
            if (assetName != expectedAsset)
                throw new InvalidOperationException($"stub only serves {expectedAsset}, got {assetName}");
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
            File.Copy(zipPath, destinationFile, overwrite: true);
            if (OperatingSystem.IsWindows())
                File.SetAttributes(destinationFile, FileAttributes.ReadOnly);
            else
                File.SetUnixFileMode(Path.GetDirectoryName(destinationFile)!,
                    UnixFileMode.UserRead | UnixFileMode.UserExecute);
            return Task.FromResult(new DownloadResult(destinationFile, "stub://undeletable"));
        }

        public Task<string?> FetchChecksumAsync(string assetName, CancellationToken cancellation) =>
            Task.FromResult<string?>(null);
    }

    /// <summary>
    /// One determinate progress tick (47 of 100 MB), then a download that
    /// only ends when the flow cancels it: the bar is visibly drawn at a
    /// percentage before the test cancels.
    /// </summary>
    private sealed class HangAfterProgressDownloader : IAssetDownloader
    {
        public async Task<DownloadResult> DownloadAsync(string assetName, string destinationFile,
            IProgress<DownloadProgress>? progress, CancellationToken cancellation)
        {
            Assert.Equal(ReleaseAssets.DictionaryAsset, assetName);
            progress?.Report(new DownloadProgress(47L << 20, 100L << 20, "stub://hang"));
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
