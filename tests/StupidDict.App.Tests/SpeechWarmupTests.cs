using Microsoft.Data.Sqlite;
using StupidDict.App.Speech;
using Xunit;

namespace StupidDict.App.Tests;

/// <summary>
/// First-pronunciation warm-up (B-016): the 700 MB+ pack database costs its
/// cold page reads on the very first lookup — 0.78 s measured inside the click
/// handler against 0.3 ms once warm — so the startup path opens it once on a
/// pool thread instead. The warm-up must stay silent (no temp file, no audio
/// device, no throw) and must leave the file exactly as replaceable as a normal
/// play does, or the data directory's import/replace flows would start failing.
/// Pure [Fact] tests; the startup wiring is covered headlessly in HeadlessTests.
/// </summary>
public sealed class SpeechWarmupTests
{
    private static string NewScratchDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "stupiddict-warmup", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string CreatePackDb(string directory)
    {
        var path = Path.Combine(directory, "audio-pack.db");
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using var create = connection.CreateCommand();
        create.CommandText = "CREATE TABLE audio(word TEXT PRIMARY KEY, uk BLOB, us BLOB) WITHOUT ROWID";
        create.ExecuteNonQuery();
        using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO audio(word, uk, us) VALUES($w, $uk, $us)";
        insert.Parameters.AddWithValue("$w", "the");
        insert.Parameters.AddWithValue("$uk", new byte[] { 1, 2 });
        insert.Parameters.AddWithValue("$us", new byte[] { 3, 4 });
        insert.ExecuteNonQuery();
        return path;
    }

    private sealed class FakeSpeechPlayer : ISpeechPlayer
    {
        public int WarmUps;
        public bool ThrowOnWarmUp;

        public bool Play(string word, SpeechAccent accent) => true;

        public void WarmUp()
        {
            WarmUps++;
            if (ThrowOnWarmUp) throw new InvalidOperationException("broken player");
        }

        public void Stop() { }
    }

    [Fact]
    public void CompositeWarmUpForwardsToEveryPlayer()
    {
        var pack = new FakeSpeechPlayer();
        var tts = new FakeSpeechPlayer();

        new CompositeSpeechPlayer(pack, tts).WarmUp();

        Assert.Equal(1, pack.WarmUps);
        Assert.Equal(1, tts.WarmUps);
    }

    /// <summary>A faulting player is contained; the remaining players still warm.</summary>
    [Fact]
    public void CompositeWarmUpSwallowsAFaultingPlayerAndStillWarmsTheRest()
    {
        var broken = new FakeSpeechPlayer { ThrowOnWarmUp = true };
        var healthy = new FakeSpeechPlayer();

        new CompositeSpeechPlayer(broken, healthy).WarmUp(); // must not throw

        Assert.Equal(1, broken.WarmUps);
        Assert.Equal(1, healthy.WarmUps);
    }

    /// <summary>Warming an absent pack is the same silent miss as playing one.</summary>
    [Fact]
    public void WarmUpWithoutAPackIsSilentAndCreatesNothing()
    {
        var scratch = NewScratchDirectory();
        var db = Path.Combine(scratch, "audio-pack.db");
        var store = new AudioPackStore(Path.Combine(scratch, "loose"), db);

        store.WarmUp();

        Assert.False(File.Exists(db));
    }

    /// <summary>
    /// The point of the warm-up: no temp file. A warm-up that materialized a
    /// blob would leave an mp3 behind on every launch (Cleanup only runs on
    /// Stop, which a never-played pack never sees).
    /// </summary>
    [Fact]
    public void WarmUpWritesNoTempFile()
    {
        var scratch = NewScratchDirectory();
        var db = CreatePackDb(scratch);
        var store = new AudioPackStore(Path.Combine(scratch, "loose"), db);
        var before = Directory.GetFiles(Path.GetTempPath(), "stupiddict-audio-*");

        store.WarmUp();

        Assert.Equal(before.Length, Directory.GetFiles(Path.GetTempPath(), "stupiddict-audio-*").Length);
    }

    /// <summary>
    /// Pooling=false is the reason a warm-up is invisible to everything else:
    /// the connection is gone the moment ReadBlob returns, so the database the
    /// user may replace or delete at any time is not held open by the warm-up
    /// (Windows turns a lingering handle into "being used by another process").
    /// </summary>
    [Fact]
    public void WarmUpLeavesTheDatabaseReplaceable()
    {
        var scratch = NewScratchDirectory();
        var directory = Path.Combine(scratch, "data");
        Directory.CreateDirectory(directory);
        var db = CreatePackDb(directory);
        var store = new AudioPackStore(Path.Combine(scratch, "loose"), db);

        store.WarmUp();

        var moved = Path.Combine(directory, "installed.db");
        File.Move(db, moved); // throws on Windows if the connection is still open
        File.Delete(moved);
        Assert.False(File.Exists(db));
    }

    /// <summary>The warm-up leaves playback untouched: the next play still works.</summary>
    [Fact]
    public void WarmUpDoesNotDisturbTheFollowingPlay()
    {
        var scratch = NewScratchDirectory();
        var db = CreatePackDb(scratch);
        var store = new AudioPackStore(Path.Combine(scratch, "loose"), db);

        store.WarmUp();
        var found = store.TryGetAudioFile("the", SpeechAccent.American, out var file);

        Assert.True(found);
        Assert.Equal(new byte[] { 3, 4 }, File.ReadAllBytes(file));
        store.Cleanup();
    }
}
