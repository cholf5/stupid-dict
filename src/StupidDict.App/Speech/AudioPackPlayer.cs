using StupidDict.App.Assets;

namespace StupidDict.App.Speech;

/// <summary>
/// Plays pre-generated word audio from the pronunciation pack (uk/us
/// directories of per-word MP3 files). A miss here is normal — the pack only
/// covers common words — so it returns false and the composite falls through
/// to system TTS. Reserved DOS device names never sit on disk: extraction
/// maps them to '_'-prefixed file names (Assets/ReservedDeviceNames), and
/// lookup applies the same mapping so "con" stays playable everywhere; the
/// raw name is kept as a fallback for packs extracted by older builds, where
/// it is still reachable (Unix, Windows 11).
/// </summary>
public sealed class AudioPackPlayer(string directory, IAudioFilePlayer player) : ISpeechPlayer
{
    public bool Play(string word, SpeechAccent accent)
    {
        var subdirectory = accent == SpeechAccent.British ? "uk" : "us";
        var stem = word.ToLowerInvariant() + ".mp3";
        var mapped = Path.Combine(directory, subdirectory, ReservedDeviceNames.MapSegment(stem));
        if (File.Exists(mapped)) return player.Play(mapped);
        var raw = Path.Combine(directory, subdirectory, stem);
        if (raw != mapped && File.Exists(raw)) return player.Play(raw);
        return false;
    }

    public void Stop()
    {
        try
        {
            player.Stop();
        }
        catch
        {
            // exit-time cleanup is best-effort: a faulting file player must not
            // break the composite's Stop or the window-close path
        }
    }
}

/// <summary>Tries each player in order; the first that starts playback wins.</summary>
public sealed class CompositeSpeechPlayer(params ISpeechPlayer[] players) : ISpeechPlayer
{
    public bool Play(string word, SpeechAccent accent)
    {
        foreach (var player in players)
            if (player.Play(word, accent)) return true;
        return false;
    }

    public void Stop()
    {
        foreach (var player in players)
        {
            try
            {
                player.Stop();
            }
            catch
            {
                // one broken player must neither stop the others' cleanup nor
                // let its fault escape into the window-close path
            }
        }
    }
}
