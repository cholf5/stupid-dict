using StupidDict.App.Speech;
using Xunit;

namespace StupidDict.App.Tests;

public sealed class SpeechInteropTests
{
    /// <summary>
    /// Windows resolves DLL imports case-sensitively and winmm exports only the
    /// decorated names, so a declaration spelled MciSendString probed
    /// MciSendStringW/MciSendString, found neither, and threw
    /// EntryPointNotFoundException out of the async void click handler — the
    /// process died on every pronunciation click with the audio pack installed.
    /// A missing file keeps this test silent and side-effect free: the open
    /// fails either way and the player must report false, not throw.
    /// </summary>
    [Fact]
    public void MciPlayerReportsFailureInsteadOfThrowingWhenTheFileIsMissing()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"stupiddict-missing-{Guid.NewGuid():N}.mp3");
        var player = new MciAudioFilePlayer();

        Assert.False(player.Play(missing));
        player.Stop();
    }
}
