using StupidDict.App.Speech;
using Xunit;

namespace StupidDict.App.Tests;

/// <summary>
/// The pack player resolves headwords through the same reserved-device-name
/// mapping extraction applies when it materializes files (us/con.mp3 lands
/// as us/_con.mp3), so "con" — a real headword — stays playable on every
/// Windows. The raw name remains a fallback so packs extracted by older
/// builds stay audible where that name is reachable (Unix, Windows 11;
/// pre-Win11 an existence probe on it is the documented safe no — the CON
/// redirection reports false, never blocks — and playback falls to TTS).
/// Pure [Fact] tests; no Avalonia surface.
/// </summary>
public sealed class AudioPackLookupTests
{
    private sealed class RecordingAudioFilePlayer : IAudioFilePlayer
    {
        public string? PlayedFile;
        public bool Play(string file) { PlayedFile = file; return true; }
        public void Stop() { }
    }

    private static string NewPackDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "stupiddict-uitests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    // A pack-extracted raw reserved-name file exists only through the \\?\
    // prefix on Windows; Unix writes it plainly.
    private static void WriteFile(string path, string content)
    {
        if (OperatingSystem.IsWindows()) path = MainWindow.ToExtendedPath(path);
        File.WriteAllText(path, content);
    }

    // Whether the raw name "us/con.mp3" is reachable by ordinary Win32
    // probes: yes on Unix and Windows 11 (build 22000+), no earlier — there
    // the name redirects to the CON device and File.Exists reports false.
    private static bool RawReservedNameIsReachable =>
        !OperatingSystem.IsWindows() || Environment.OSVersion.Version.Build >= 22000;

    [Fact]
    public void ReservedHeadwordPlaysThroughMappedName()
    {
        var packDirectory = NewPackDirectory();
        var uk = Path.Combine(packDirectory, "uk");
        Directory.CreateDirectory(uk);
        File.WriteAllText(Path.Combine(uk, "_con.mp3"), "audio");
        var filePlayer = new RecordingAudioFilePlayer();

        var played = new AudioPackPlayer(packDirectory, filePlayer).Play("con", SpeechAccent.British);

        Assert.True(played);
        Assert.EndsWith(Path.Combine("uk", "_con.mp3"), filePlayer.PlayedFile);
    }

    [Fact]
    public void PlainHeadwordPlaysItsOwnFile()
    {
        var packDirectory = NewPackDirectory();
        var us = Path.Combine(packDirectory, "us");
        Directory.CreateDirectory(us);
        File.WriteAllText(Path.Combine(us, "cat.mp3"), "audio");
        var filePlayer = new RecordingAudioFilePlayer();

        var played = new AudioPackPlayer(packDirectory, filePlayer).Play("cat", SpeechAccent.American);

        Assert.True(played);
        Assert.EndsWith(Path.Combine("us", "cat.mp3"), filePlayer.PlayedFile);
    }

    [Fact]
    public void RawNameFallbackKeepsPacksExtractedByOlderBuildsAudible()
    {
        var packDirectory = NewPackDirectory();
        var uk = Path.Combine(packDirectory, "uk");
        Directory.CreateDirectory(uk);
        WriteFile(Path.Combine(uk, "con.mp3"), "audio");
        var filePlayer = new RecordingAudioFilePlayer();

        var played = new AudioPackPlayer(packDirectory, filePlayer).Play("con", SpeechAccent.British);

        Assert.Equal(RawReservedNameIsReachable, played);
        if (played)
            Assert.EndsWith(Path.Combine("uk", "con.mp3"), filePlayer.PlayedFile);
    }

    [Fact]
    public void MappedNameWinsOverRawName()
    {
        var packDirectory = NewPackDirectory();
        var uk = Path.Combine(packDirectory, "uk");
        Directory.CreateDirectory(uk);
        File.WriteAllText(Path.Combine(uk, "_con.mp3"), "mapped");
        WriteFile(Path.Combine(uk, "con.mp3"), "raw");
        var filePlayer = new RecordingAudioFilePlayer();

        var played = new AudioPackPlayer(packDirectory, filePlayer).Play("con", SpeechAccent.British);

        Assert.True(played);
        Assert.EndsWith(Path.Combine("uk", "_con.mp3"), filePlayer.PlayedFile);
    }
}
