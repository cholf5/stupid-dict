namespace StupidDict.App.Speech;

/// <summary>
/// Plays pre-generated word audio from the pronunciation pack. File
/// resolution (single SQLite database, legacy loose directories) lives in
/// the store; this class is the seam to the platform file players. A miss
/// here is normal — the pack only covers common words — so it returns false
/// and the composite falls through to system TTS.
/// </summary>
public sealed class AudioPackPlayer(IAudioPackStore store, IAudioFilePlayer player) : ISpeechPlayer
{
    public bool Play(string word, SpeechAccent accent)
    {
        if (!store.TryGetAudioFile(word, accent, out var file)) return false;
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
        // After the player stops, the temp file it was given is no longer
        // needed; the store deletes it best-effort (a lingering handle just
        // leaves it for the OS temp sweeper).
        store.Cleanup();
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
