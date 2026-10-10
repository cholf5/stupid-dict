using System.IO.Compression;
using Microsoft.Data.Sqlite;
using StupidDict.App;
using StupidDict.App.Assets;
using StupidDict.App.Localization;
using Xunit;

namespace StupidDict.App.Tests;

/// <summary>
/// The audio pack ships as a zip (data-2 asset, manual browser download) and
/// lives as a single SQLite database. The converter is the bridge — used by
/// the download flow after checksum verification and by manual zip imports:
/// it validates the pack shape (uk/…/us/… MP3 entries), verifies each
/// entry's CRC while streaming (ZipArchive does not; B-008), inserts into
/// one transaction, and lands the database atomically (staging file + move),
/// so a cancelled or damaged conversion never leaves a pack that looks
/// installed. Pure [Fact] tests; no Avalonia surface.
/// </summary>
public sealed class AudioPackConverterTests
{
    private static string NewScratchDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "stupiddict-uitests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void AddEntry(ZipArchive archive, string name, byte[] bytes)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
        using var stream = entry.Open();
        stream.Write(bytes);
    }

    private static string WritePackZip(string directory, params (string Name, byte[] Bytes)[] entries)
    {
        var path = Path.Combine(directory, $"pack-{Guid.NewGuid():N}.zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, bytes) in entries) AddEntry(archive, name, bytes);
        return path;
    }

    private static (int Rows, byte[]? Uk, byte[]? Us) ReadRow(string db, string word)
    {
        using var connection = new SqliteConnection($"Data Source={db}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT uk, us FROM audio WHERE word = $w";
        command.Parameters.AddWithValue("$w", word);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return (0, null, null);
        return (1, reader.IsDBNull(0) ? null : (byte[])reader.GetValue(0),
                   reader.IsDBNull(1) ? null : (byte[])reader.GetValue(1));
    }

    [Fact]
    public void ConvertsPackEntriesIntoDatabase()
    {
        var scratch = NewScratchDirectory();
        var zip = WritePackZip(scratch,
            ("uk/cat.mp3", [1, 2, 3]),
            ("us/cat.mp3", [4, 5]),
            ("uk/dog.mp3", [6]));
        var db = Path.Combine(scratch, "audio-pack.db");

        AudioPackConverter.ConvertZipToDatabase(zip, db);

        var cat = ReadRow(db, "cat");
        Assert.Equal(1, cat.Rows);
        Assert.Equal([1, 2, 3], cat.Uk);
        Assert.Equal([4, 5], cat.Us);
        Assert.Equal([6], ReadRow(db, "dog").Uk);
    }

    [Fact]
    public void WordKeysAreLowercased()
    {
        var scratch = NewScratchDirectory();
        var zip = WritePackZip(scratch, ("uk/Cat.mp3", [1]));
        var db = Path.Combine(scratch, "audio-pack.db");

        AudioPackConverter.ConvertZipToDatabase(zip, db);

        Assert.Equal(0, ReadRow(db, "Cat").Rows); // 键即小写（查找端 ToLowerInvariant）
        var row = ReadRow(db, "cat");
        Assert.Equal(1, row.Rows);
        Assert.Equal([1], row.Uk);
    }

    [Fact]
    public void DirectoryAndForeignEntriesAreSkipped()
    {
        var scratch = NewScratchDirectory();
        var zip = WritePackZip(scratch,
            ("uk/", []),
            ("us/", []),
            ("readme.txt", [0]),
            ("meta/info.json", [0]),
            ("uk/cat.mp3", [1, 1]));
        var db = Path.Combine(scratch, "audio-pack.db");

        AudioPackConverter.ConvertZipToDatabase(zip, db);

        using var connection = new SqliteConnection($"Data Source={db}");
        connection.Open();
        using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM audio";
        Assert.Equal(1L, (long)count.ExecuteScalar()!);
    }

    [Fact]
    public void ZipWithoutPackEntriesThrows()
    {
        var scratch = NewScratchDirectory();
        var zip = WritePackZip(scratch, ("readme.txt", [0]));
        var db = Path.Combine(scratch, "audio-pack.db");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            AudioPackConverter.ConvertZipToDatabase(zip, db));

        Assert.Equal(Translations.Instance.ImportMissingPack, ex.Message);
        Assert.False(File.Exists(db));
    }

    [Fact]
    public void ZipSlipEntryRefusesTheWholeArchive()
    {
        var scratch = NewScratchDirectory();
        var zip = WritePackZip(scratch, ("us/../evil.mp3", [0]), ("uk/cat.mp3", [1]));
        var db = Path.Combine(scratch, "audio-pack.db");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            AudioPackConverter.ConvertZipToDatabase(zip, db));

        // Names become text keys in a conversion, but a zip that tries to
        // traverse is hostile and must not install — same verdict the old
        // extraction-based import gave.
        Assert.Equal(string.Format(Translations.Instance.ZipSlipFormat, "us/../evil.mp3"), ex.Message);
        Assert.False(File.Exists(db));
        Assert.False(File.Exists(db + ".building"));
    }

    [Fact]
    public void ProgressCountsEntries()
    {
        var scratch = NewScratchDirectory();
        var zip = WritePackZip(scratch,
            ("uk/cat.mp3", [1]), ("us/cat.mp3", [2]), ("uk/dog.mp3", [3]));
        var db = Path.Combine(scratch, "audio-pack.db");
        var reports = new List<(int Done, int Total)>();

        AudioPackConverter.ConvertZipToDatabase(zip, db, (done, total) => reports.Add((done, total)));

        Assert.Equal((3, 3), reports[^1]);
        Assert.Equal(0, reports[0].Done);
        Assert.All(reports, r => Assert.True(r.Done <= r.Total));
    }

    [Fact]
    public void CorruptEntryThrowsInvalidDataAndLeavesNoTarget()
    {
        var scratch = NewScratchDirectory();
        // One good entry first, then a stored entry whose payload byte is
        // flipped after writing: the central directory still publishes the
        // original CRC, so streaming must detect the mismatch.
        var zip = WritePackZip(scratch,
            ("uk/cat.mp3", [1, 2, 3]),
            ("uk/bad.mp3", new byte[128]));
        var bytes = File.ReadAllBytes(zip);
        var marker = System.Text.Encoding.ASCII.GetBytes("uk/bad.mp3");
        var nameOffset = bytes.AsSpan().IndexOf(marker);
        Assert.True(nameOffset >= 0);
        // Local header layout: 30 fixed bytes, then the name, then the data
        // (no extra field, no data descriptor for a seekable writer).
        bytes[nameOffset + marker.Length] ^= 0xFF;
        File.WriteAllBytes(zip, bytes);
        var db = Path.Combine(scratch, "audio-pack.db");

        Assert.Throws<InvalidDataException>(() =>
            AudioPackConverter.ConvertZipToDatabase(zip, db));

        Assert.False(File.Exists(db));
        Assert.False(File.Exists(db + ".building"));
    }

    [Fact]
    public void CancellationLeavesNoTargetAndNoStaging()
    {
        var scratch = NewScratchDirectory();
        var zip = WritePackZip(scratch, ("uk/cat.mp3", [1]));
        var db = Path.Combine(scratch, "audio-pack.db");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            AudioPackConverter.ConvertZipToDatabase(zip, db,
                cancellation: cancellation.Token));

        Assert.False(File.Exists(db));
        Assert.False(File.Exists(db + ".building"));
    }

    [Fact]
    public void DatabaseKeepsTheDefaultPageSize()
    {
        var scratch = NewScratchDirectory();
        var zip = WritePackZip(scratch, ("uk/cat.mp3", [1]));
        var db = Path.Combine(scratch, "audio-pack.db");

        AudioPackConverter.ConvertZipToDatabase(zip, db);

        // Deliberately the 4KB default: with this pack's uneven blob sizes,
        // every alternative measured WORSE on real data (4KB 715MB, 8KB
        // 800MB, 16KB 1.11GB, 32KB 2.07GB, 64KB 834MB) — the overflow
        // remainder arithmetic punishes mid-size pages and uneven row sizes
        // fragment large pages. Don't "optimize" without re-measuring.
        using var connection = new SqliteConnection($"Data Source={db}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA page_size";
        Assert.Equal(4096L, (long)command.ExecuteScalar()!);
    }

    [Fact]
    public void ConversionReplacesAnExistingDatabase()
    {
        var scratch = NewScratchDirectory();
        var db = Path.Combine(scratch, "audio-pack.db");
        AudioPackConverter.ConvertZipToDatabase(
            WritePackZip(scratch, ("uk/old.mp3", [9])), db);

        AudioPackConverter.ConvertZipToDatabase(
            WritePackZip(scratch, ("uk/new.mp3", [8])), db);

        Assert.Equal(0, ReadRow(db, "old").Rows);
        Assert.Equal([8], ReadRow(db, "new").Uk);
    }
}
