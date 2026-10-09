namespace StupidDict.App.Speech;

/// <summary>Plays an MP3 file, stopping whatever was playing before.</summary>
public interface IAudioFilePlayer
{
    bool Play(string file);
}

/// <summary>Plays a file with a CLI player such as afplay or mpg123.</summary>
internal sealed class ProcessAudioFilePlayer(string program, params string[] prefixArguments) : IAudioFilePlayer
{
    private readonly ProcessPlayer _process = new();

    public bool Play(string file)
    {
        var arguments = new List<string>(prefixArguments) { file };
        return _process.Play(program, [.. arguments]);
    }
}

/// <summary>
/// Linux has no guaranteed CLI player; try the common ones. If none exists
/// the audio pack is unusable and lookups fall through to espeak-based TTS.
/// </summary>
internal sealed class LinuxAudioFilePlayer : IAudioFilePlayer
{
    private static readonly (string Program, string[] Arguments)[] Candidates =
    [
        ("mpg123", ["-q"]),
        ("mpv", ["--no-video", "--really-quiet", "--no-terminal"]),
        ("ffplay", ["-nodisp", "-autoexit", "-loglevel", "quiet"]),
    ];

    private IAudioFilePlayer? _resolved;

    public bool Play(string file)
    {
        if (_resolved is { } resolved) return resolved.Play(file);
        foreach (var (program, arguments) in Candidates)
        {
            var path = PathLookup.Which(program);
            if (path is null) continue;
            _resolved = new ProcessAudioFilePlayer(path, arguments);
            return _resolved.Play(file);
        }
        return false;
    }
}

/// <summary>Windows plays MP3 through the Media Control Interface (winmm).</summary>
internal sealed class MciAudioFilePlayer : IAudioFilePlayer
{
    private const string Alias = "stupiddict_audio";
    private bool _open;

    public bool Play(string file)
    {
        if (!OperatingSystem.IsWindows()) return false;
        Stop();
        try
        {
            // mciSendString needs short paths without / separators issues; the
            // alias is fixed because only one clip plays at a time.
            if (Send($"open \"{Path.GetFullPath(file)}\" type mpegvideo alias {Alias}") != 0) return false;
            _open = true;
            Send($"play {Alias}");
            return true;
        }
        catch
        {
            // Interop faults belong to the same "no engine could speak" path as
            // a failed open: report false so the composite falls through to TTS,
            // never let them escape and take the process down.
            Stop();
            return false;
        }
    }

    public void Stop()
    {
        if (!_open) return;
        _open = false;
        try
        {
            Send($"close {Alias}");
        }
        catch
        {
            // the interop binding itself may be the thing that is broken
        }
    }

    private static int Send(string command) =>
        MciSendString(command, null, 0, nint.Zero);

    /// <summary>
    /// Windows resolves imports case-sensitively and winmm only exports the
    /// decorated names, so the entry point must be spelled mciSendStringW —
    /// a C#-style MciSendString probes MciSendStringW/MciSendString, finds
    /// neither, and throws EntryPointNotFoundException on the first click.
    /// ExactSpelling also stops the runtime from re-deriving the suffix.
    /// </summary>
    [System.Runtime.InteropServices.DllImport(
        "winmm.dll",
        EntryPoint = "mciSendStringW",
        CharSet = System.Runtime.InteropServices.CharSet.Unicode,
        ExactSpelling = true)]
    private static extern int MciSendString(string command, string? returnBuffer, int returnLength, nint callback);
}
