using StupidDict.App.Speech;
using Xunit;

namespace StupidDict.App.Tests;

/// <summary>
/// State-machine tests for the Windows MCI player. mciSendString reports
/// failure through its return code — never exceptions — so the seams inject a
/// fake in place of the P/Invoke and model winmm's observable contract: open
/// with a live alias is refused, close either frees the alias or fails and
/// leaves it alive. The real winmm import is never touched; these run on
/// every platform.
/// </summary>
public sealed class MciStateMachineTests
{
    private const string Alias = "stupiddict_audio";
    private static readonly string SomeFile = Path.Combine(Path.GetTempPath(), "stupiddict-mci-state.mp3");

    /// <summary>Minimal winmm model: alias uniqueness plus close success/failure.</summary>
    private sealed class FakeMci
    {
        public bool AliasOpen;
        public bool CloseFails;
        public int PlayResult;
        public List<string> Commands = [];

        public int Send(string command)
        {
            Commands.Add(command);
            switch (command.Split(' ', 2)[0])
            {
                case "open":
                    if (AliasOpen) return 3; // alias still alive in winmm: re-open is refused
                    AliasOpen = true;
                    return 0;
                case "play":
                    return PlayResult;
                case "close":
                    if (CloseFails) return 5; // a failed close leaves the alias alive
                    AliasOpen = false;
                    return 0;
                default:
                    return 0;
            }
        }
    }

    /// <summary>TC-001: a refused play must close the alias and report false, so the composite falls back to TTS.</summary>
    [Fact]
    public void PlayRefusalClosesAliasAndReportsFalse()
    {
        var mci = new FakeMci { PlayResult = 266 };
        var player = new MciAudioFilePlayer(mci.Send);

        Assert.False(player.Play(SomeFile));

        string[] expected =
        [
            $"open \"{Path.GetFullPath(SomeFile)}\" type mpegvideo alias {Alias}",
            $"play {Alias}",
            $"close {Alias}",
        ];
        Assert.Equal(expected, mci.Commands);
        Assert.False(mci.AliasOpen);
    }

    /// <summary>Composite-level TC-001: pack play refusal falls through to system TTS.</summary>
    [Fact]
    public void CompositeFallsThroughToTtsWhenMciRefusesToPlay()
    {
        var root = Path.Combine(Path.GetTempPath(), $"stupiddict-mci-fallback-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "uk"));
        try
        {
            // Content is never read: the seam owns winmm, only File.Exists matters.
            File.WriteAllBytes(Path.Combine(root, "uk", "cat.mp3"), [0xFF, 0xFB, 0x90, 0x00]);

            var mci = new FakeMci { PlayResult = 266 };
            var tts = new RecordingPlayer();
            var composite = new CompositeSpeechPlayer(
                new AudioPackPlayer(root, new MciAudioFilePlayer(mci.Send)),
                tts);

            Assert.True(composite.Play("cat", SpeechAccent.British));
            Assert.Equal([("cat", SpeechAccent.British)], tts.Played);
            Assert.Contains(mci.Commands, c => c == $"close {Alias}"); // alias released on the refused play
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>
    /// TC-002: a failed close must not clear the state bit — winmm still holds
    /// the alias, so the next Play has to retry the close before re-opening
    /// instead of dying on a duplicate alias forever.
    /// </summary>
    [Fact]
    public void FailedCloseKeepsTheStateBitSoTheNextPlayRetriesTheClose()
    {
        var mci = new FakeMci { CloseFails = true };
        var player = new MciAudioFilePlayer(mci.Send);

        Assert.True(player.Play(SomeFile)); // open+play succeed
        player.Stop();                      // close fails: alias alive in winmm…
        Assert.True(mci.AliasOpen);         // …and the player must agree with reality

        mci.CloseFails = false;             // the transient failure is over
        Assert.True(player.Play(SomeFile)); // next play must close-then-open, not wedge

        Assert.Equal(2, mci.Commands.Count(c => c == $"close {Alias}"));
        var retriedClose = mci.Commands.FindLastIndex(c => c == $"close {Alias}");
        var secondOpen = mci.Commands.FindIndex(1, c => c.StartsWith("open "));
        Assert.True(retriedClose < secondOpen);
    }

    /// <summary>
    /// TC-002 hardened: while close keeps failing, the wedged alias makes
    /// re-open fail and Play must report false (TTS fallback) instead of
    /// claiming success while nothing can play.
    /// </summary>
    [Fact]
    public void PersistentlyFailingCloseNeverLetsPlayClaimSuccessOnAStuckAlias()
    {
        var mci = new FakeMci { CloseFails = true };
        var player = new MciAudioFilePlayer(mci.Send);

        Assert.True(player.Play(SomeFile));
        player.Stop();
        Assert.True(mci.AliasOpen);

        Assert.False(player.Play(SomeFile)); // open refused: duplicate alias
        Assert.Equal(2, mci.Commands.Count(c => c.StartsWith("open ")));
        Assert.Equal(1, mci.Commands.Count(c => c.StartsWith("play "))); // second attempt dies at open
    }

    /// <summary>Sanity: the success path is unchanged — no close while playing, one close per Stop.</summary>
    [Fact]
    public void SuccessfulPlayReportsTrueAndKeepsTheAliasOpenUntilStop()
    {
        var mci = new FakeMci();
        var player = new MciAudioFilePlayer(mci.Send);

        Assert.True(player.Play(SomeFile));
        Assert.DoesNotContain(mci.Commands, c => c.StartsWith("close ")); // still playing

        player.Stop();
        Assert.Equal($"close {Alias}", mci.Commands[^1]);
        Assert.False(mci.AliasOpen);

        player.Stop(); // already closed: no redundant close reaches winmm
        Assert.Equal($"close {Alias}", mci.Commands[^1]);
    }

    private sealed class RecordingPlayer : ISpeechPlayer
    {
        public List<(string Word, SpeechAccent Accent)> Played = [];

        public bool Play(string word, SpeechAccent accent)
        {
            Played.Add((word, accent));
            return true;
        }
    }
}
