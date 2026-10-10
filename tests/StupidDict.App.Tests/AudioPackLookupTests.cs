using StupidDict.App.Speech;
using Xunit;

namespace StupidDict.App.Tests;

/// <summary>
/// Player-level seams around the pack: the player resolves the file through
/// <see cref="IAudioPackStore"/> (db or legacy loose layout — see
/// AudioPackStoreTests), hands it to the file player, and on Stop stops the
/// file player first and then lets the store clean its temp files. Pure
/// [Fact] tests; no Avalonia surface.
/// </summary>
public sealed class AudioPackLookupTests
{
    private sealed class FakeStore : IAudioPackStore
    {
        public bool Hit;
        public string? HandedOutFile;
        public int Cleanups;

        public bool TryGetAudioFile(string word, SpeechAccent accent, out string file)
        {
            file = HandedOutFile ?? "";
            return Hit;
        }

        public void Cleanup() => Cleanups++;
    }

    private sealed class RecordingAudioFilePlayer : IAudioFilePlayer
    {
        public string? PlayedFile;
        public int Stops;
        public bool Play(string file) { PlayedFile = file; return true; }
        public void Stop() => Stops++;
    }

    [Fact]
    public void StoreHitIsPlayed()
    {
        var store = new FakeStore { Hit = true, HandedOutFile = "/tmp/audio.mp3" };
        var filePlayer = new RecordingAudioFilePlayer();

        var played = new AudioPackPlayer(store, filePlayer).Play("cat", SpeechAccent.British);

        Assert.True(played);
        Assert.Equal("/tmp/audio.mp3", filePlayer.PlayedFile);
    }

    [Fact]
    public void StoreMissReturnsFalseForTheTtsFallback()
    {
        var filePlayer = new RecordingAudioFilePlayer();

        var played = new AudioPackPlayer(new FakeStore(), filePlayer).Play("cat", SpeechAccent.British);

        Assert.False(played);
        Assert.Null(filePlayer.PlayedFile);
    }

    [Fact]
    public void StopStopsTheFilePlayerAndCleansTheStore()
    {
        var store = new FakeStore();
        var filePlayer = new RecordingAudioFilePlayer();
        var player = new AudioPackPlayer(store, filePlayer);

        player.Stop();

        Assert.Equal(1, filePlayer.Stops);
        Assert.Equal(1, store.Cleanups);
    }
}
