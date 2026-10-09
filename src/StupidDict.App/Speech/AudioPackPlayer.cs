namespace StupidDict.App.Speech;

/// <summary>
/// Plays pre-generated word audio from the pronunciation pack (uk/us
/// directories of per-word MP3 files). A miss here is normal — the pack only
/// covers common words — so it returns false and the composite falls through
/// to system TTS.
/// </summary>
public sealed class AudioPackPlayer(string directory, IAudioFilePlayer player) : ISpeechPlayer
{
    public bool Play(string word, SpeechAccent accent)
    {
        var subdirectory = accent == SpeechAccent.British ? "uk" : "us";
        var file = Path.Combine(directory, subdirectory, word.ToLowerInvariant() + ".mp3");
        if (!File.Exists(file)) return false;
        return player.Play(file);
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
