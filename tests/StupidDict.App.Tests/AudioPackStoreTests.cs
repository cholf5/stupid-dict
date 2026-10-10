using Microsoft.Data.Sqlite;
using StupidDict.App.Speech;
using Xunit;

namespace StupidDict.App.Tests;

/// <summary>
/// The pack store resolves a headword to a playable MP3 file path. The pack
/// lives either as a single SQLite database (word → MP3 blobs; the new
/// layout) or as the legacy loose uk/us directories; the database wins so a
/// manual import takes effect even for users who still have loose files.
/// Database hits materialize to a uniquely named file in the OS temp
/// directory — the players need a real path, and the name never contains a
/// headword, so Windows reserved device names cannot occur on the play path.
/// Every miss — no pack, missing word, broken database — is a plain false
/// for the TTS fallback; a faulting store must never throw into playback.
/// Pure [Fact] tests; no Avalonia surface.
/// </summary>
public sealed class AudioPackStoreTests
{
    private static string NewScratchDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "stupiddict-uitests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string CreatePackDb(string directory, string word, byte[]? uk, byte[]? us)
    {
        var path = Path.Combine(directory, "audio-pack.db");
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using var create = connection.CreateCommand();
        create.CommandText = "CREATE TABLE audio(word TEXT PRIMARY KEY, uk BLOB, us BLOB) WITHOUT ROWID";
        create.ExecuteNonQuery();
        using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO audio(word, uk, us) VALUES($w, $uk, $us)";
        insert.Parameters.AddWithValue("$w", word);
        insert.Parameters.AddWithValue("$uk", (object?)uk ?? DBNull.Value);
        insert.Parameters.AddWithValue("$us", (object?)us ?? DBNull.Value);
        insert.ExecuteNonQuery();
        return path;
    }

    [Fact]
    public void DatabaseHitWritesBlobToTempFile()
    {
        var scratch = NewScratchDirectory();
        var audio = new byte[] { 1, 2, 3, 4 };
        var db = CreatePackDb(scratch, "cat", audio, [9, 9]);
        var store = new AudioPackStore(Path.Combine(scratch, "loose"), db);

        var found = store.TryGetAudioFile("Cat", SpeechAccent.British, out var file);

        Assert.True(found);
        Assert.StartsWith(Path.GetTempPath(), file);
        Assert.StartsWith("stupiddict-audio-", Path.GetFileName(file));
        Assert.Equal(audio, File.ReadAllBytes(file));
        store.Cleanup();
    }

    [Fact]
    public void AccentPicksTheColumn()
    {
        var scratch = NewScratchDirectory();
        var usAudio = new byte[] { 7, 7, 7 };
        var db = CreatePackDb(scratch, "cat", [1], usAudio);
        var store = new AudioPackStore(Path.Combine(scratch, "loose"), db);

        var found = store.TryGetAudioFile("cat", SpeechAccent.American, out var file);

        Assert.True(found);
        Assert.Equal(usAudio, File.ReadAllBytes(file));
        store.Cleanup();
    }

    [Fact]
    public void MissingWordIsAMiss()
    {
        var scratch = NewScratchDirectory();
        var db = CreatePackDb(scratch, "cat", [1], null);
        var store = new AudioPackStore(Path.Combine(scratch, "loose"), db);

        Assert.False(store.TryGetAudioFile("dog", SpeechAccent.British, out _));
        Assert.False(store.TryGetAudioFile("cat", SpeechAccent.American, out _)); // us is null
    }

    [Fact]
    public void NullBlobIsAMiss()
    {
        var scratch = NewScratchDirectory();
        var db = CreatePackDb(scratch, "cat", [1], null);
        var store = new AudioPackStore(Path.Combine(scratch, "loose"), db);

        Assert.False(store.TryGetAudioFile("cat", SpeechAccent.American, out _));
    }

    [Fact]
    public void BrokenDatabaseIsAMissNotAThrow()
    {
        var scratch = NewScratchDirectory();
        var db = Path.Combine(scratch, "audio-pack.db");
        File.WriteAllText(db, "this is not sqlite");
        var store = new AudioPackStore(Path.Combine(scratch, "loose"), db);

        Assert.False(store.TryGetAudioFile("cat", SpeechAccent.British, out _));
    }

    [Fact]
    public void AbsentPackIsAMiss()
    {
        var scratch = NewScratchDirectory();
        var store = new AudioPackStore(Path.Combine(scratch, "loose"), Path.Combine(scratch, "audio-pack.db"));

        Assert.False(store.TryGetAudioFile("cat", SpeechAccent.British, out _));
    }

    [Fact]
    public void LooseDirectoryServesWhenThereIsNoDatabase()
    {
        var scratch = NewScratchDirectory();
        var loose = Path.Combine(scratch, "loose");
        Directory.CreateDirectory(Path.Combine(loose, "uk"));
        File.WriteAllText(Path.Combine(loose, "uk", "cat.mp3"), "loose-audio");
        var store = new AudioPackStore(loose, Path.Combine(scratch, "audio-pack.db"));

        var found = store.TryGetAudioFile("cat", SpeechAccent.British, out var file);

        Assert.True(found);
        Assert.EndsWith(Path.Combine("uk", "cat.mp3"), file);
        Assert.Equal("loose-audio", File.ReadAllText(file));
    }

    [Fact]
    public void LooseResolutionMapsReservedDeviceNames()
    {
        var scratch = NewScratchDirectory();
        var loose = Path.Combine(scratch, "loose");
        Directory.CreateDirectory(Path.Combine(loose, "uk"));
        File.WriteAllText(Path.Combine(loose, "uk", "_con.mp3"), "con-audio");
        var store = new AudioPackStore(loose, Path.Combine(scratch, "audio-pack.db"));

        var found = store.TryGetAudioFile("con", SpeechAccent.British, out var file);

        Assert.True(found);
        Assert.EndsWith(Path.Combine("uk", "_con.mp3"), file);
    }

    [Fact]
    public void DatabaseWinsOverLooseDirectory()
    {
        var scratch = NewScratchDirectory();
        var loose = Path.Combine(scratch, "loose");
        Directory.CreateDirectory(Path.Combine(loose, "uk"));
        File.WriteAllText(Path.Combine(loose, "uk", "cat.mp3"), "loose-audio");
        var db = CreatePackDb(scratch, "cat", [5, 5, 5], null);
        var store = new AudioPackStore(loose, db);

        var found = store.TryGetAudioFile("cat", SpeechAccent.British, out var file);

        Assert.True(found);
        // The loose file lives under the scratch directory; a database hit
        // materializes into the OS temp directory instead.
        Assert.StartsWith(Path.GetTempPath(), file);
        Assert.Equal([5, 5, 5], File.ReadAllBytes(file));
        store.Cleanup();
    }

    [Fact]
    public void CleanupDeletesTheTempFile()
    {
        var scratch = NewScratchDirectory();
        var db = CreatePackDb(scratch, "cat", [1, 2, 3], null);
        var store = new AudioPackStore(Path.Combine(scratch, "loose"), db);
        store.TryGetAudioFile("cat", SpeechAccent.British, out var file);
        Assert.True(File.Exists(file));

        store.Cleanup();

        Assert.False(File.Exists(file));
        store.Cleanup(); // idempotent
    }

    [Fact]
    public void NextHitReplacesThePreviousTempFile()
    {
        var scratch = NewScratchDirectory();
        var db = CreatePackDb(scratch, "cat", [1], null);
        using (var connection = new SqliteConnection($"Data Source={db}"))
        {
            connection.Open();
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO audio(word, uk) VALUES('dog', $b)";
            insert.Parameters.AddWithValue("$b", new byte[] { 2 });
            insert.ExecuteNonQuery();
        }
        var store = new AudioPackStore(Path.Combine(scratch, "loose"), db);
        store.TryGetAudioFile("cat", SpeechAccent.British, out var first);
        store.TryGetAudioFile("dog", SpeechAccent.British, out var second);

        Assert.NotEqual(first, second);
        Assert.False(File.Exists(first)); // best-effort deleted when the next hit materializes
        Assert.True(File.Exists(second));
        store.Cleanup();
    }
}
