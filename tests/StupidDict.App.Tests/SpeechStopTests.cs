using StupidDict.App.Speech;
using Xunit;

namespace StupidDict.App.Tests;

/// <summary>
/// Stop-on-exit (B-012): ISpeechPlayer.Stop fans out through the composite and
/// the pack player down to the file players and process killers, and every
/// hop is best-effort — a faulting player must never throw into the
/// window-close path, and later players still get stopped. Pure [Fact] tests;
/// the window-level wiring is covered headlessly in HeadlessTests.
/// </summary>
public sealed class SpeechStopTests
{
    private sealed class FakeSpeechPlayer : ISpeechPlayer
    {
        public int Stops;
        public bool ThrowOnStop;

        public bool Play(string word, SpeechAccent accent) => true;

        public void Stop()
        {
            Stops++;
            if (ThrowOnStop) throw new InvalidOperationException("broken player");
        }
    }

    private sealed class FakeAudioFilePlayer : IAudioFilePlayer
    {
        public int Stops;
        public bool ThrowOnStop;

        public bool Play(string file) => true;

        public void Stop()
        {
            Stops++;
            if (ThrowOnStop) throw new InvalidOperationException("broken file player");
        }
    }

    [Fact]
    public void CompositeStopForwardsToEveryPlayer()
    {
        var pack = new FakeSpeechPlayer();
        var tts = new FakeSpeechPlayer();
        var composite = new CompositeSpeechPlayer(pack, tts);

        composite.Stop();

        Assert.Equal(1, pack.Stops);
        Assert.Equal(1, tts.Stops);
    }

    /// <summary>A faulting player is contained; the remaining players still stop.</summary>
    [Fact]
    public void CompositeStopSwallowsAFaultingPlayerAndStillStopsTheRest()
    {
        var broken = new FakeSpeechPlayer { ThrowOnStop = true };
        var healthy = new FakeSpeechPlayer();
        var composite = new CompositeSpeechPlayer(broken, healthy);

        composite.Stop(); // must not throw

        Assert.Equal(1, broken.Stops);
        Assert.Equal(1, healthy.Stops);
    }

    [Fact]
    public void AudioPackStopReachesTheFilePlayer()
    {
        var filePlayer = new FakeAudioFilePlayer();
        var pack = new AudioPackPlayer("/nonexistent-directory", filePlayer);

        pack.Stop();

        Assert.Equal(1, filePlayer.Stops);
    }

    [Fact]
    public void AudioPackStopSwallowsAFaultingFilePlayer()
    {
        var pack = new AudioPackPlayer("/nonexistent-directory",
            new FakeAudioFilePlayer { ThrowOnStop = true });

        pack.Stop(); // must not throw
    }

    /// <summary>Stop with nothing playing — and repeated — stays harmless.</summary>
    [Fact]
    public void SystemTtsPlayerStopIsBestEffortAndRepeatable()
    {
        var player = new SystemTtsPlayer();

        player.Stop();
        player.Stop();
    }
}
