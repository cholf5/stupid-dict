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
}
