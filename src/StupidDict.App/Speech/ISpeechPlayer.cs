namespace StupidDict.App.Speech;

/// <summary>The accent a word is spoken in.</summary>
public enum SpeechAccent
{
    British,
    American,
}

/// <summary>
/// Plays a word aloud. Implementations must stop any playback in progress
/// when a new one starts. Returns false when this player cannot speak the
/// word, letting the caller fall through to the next one.
/// </summary>
public interface ISpeechPlayer
{
    bool Play(string word, SpeechAccent accent);

    /// <summary>
    /// Stops any playback in progress and releases playback resources, so the
    /// app exits silently instead of leaving audio processes to finish their
    /// word (B-012). Best-effort like everything in the chain: implementations
    /// swallow every fault instead of throwing into the close path, and stay
    /// usable for a later Play.
    /// </summary>
    void Stop();
}
