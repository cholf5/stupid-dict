namespace StupidDict.App.Speech;

/// <summary>Builds the real playback chain: audio pack first, system TTS as fallback.</summary>
public static class SpeechPlayback
{
    public static ISpeechPlayer Create(string looseAudioDirectory, string audioPackDatabasePath) =>
        new CompositeSpeechPlayer(
            new AudioPackPlayer(new AudioPackStore(looseAudioDirectory, audioPackDatabasePath), CreateFilePlayer()),
            new SystemTtsPlayer());

    private static IAudioFilePlayer CreateFilePlayer()
    {
        if (OperatingSystem.IsMacOS()) return new ProcessAudioFilePlayer("/usr/bin/afplay");
        if (OperatingSystem.IsWindows()) return new MciAudioFilePlayer();
        return new LinuxAudioFilePlayer();
    }
}
