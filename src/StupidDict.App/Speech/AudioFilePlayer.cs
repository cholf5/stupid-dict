namespace StupidDict.App.Speech;

/// <summary>Plays an MP3 file, stopping whatever was playing before.</summary>
public interface IAudioFilePlayer
{
    bool Play(string file);

    /// <summary>Stops playback in progress; best-effort, never throws.</summary>
    void Stop();
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

    public void Stop() => _process.Stop();
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

    public void Stop() => _resolved?.Stop();
}

/// <summary>Windows plays MP3 through the Media Control Interface (winmm).</summary>
internal sealed class MciAudioFilePlayer : IAudioFilePlayer
{
    private const string Alias = "stupiddict_audio";
    // Test seam for the command state machine: null in production (real winmm
    // below), a fake mciSendString in tests. The platform guard only protects
    // the real P/Invoke — a seam instance drives the same commands against a
    // fake, so the state machine is testable on every platform.
    private readonly Func<string, int>? _sendOverride;
    private bool _open;

    public MciAudioFilePlayer() { }

    internal MciAudioFilePlayer(Func<string, int> sendOverride) => _sendOverride = sendOverride;

    public bool Play(string file)
    {
        if (_sendOverride is null && !OperatingSystem.IsWindows()) return false;
        Stop();
        try
        {
            // mciSendString needs short paths without / separators issues; the
            // alias is fixed because only one clip plays at a time.
            if (Send($"open \"{Path.GetFullPath(file)}\" type mpegvideo alias {Alias}") != 0) return false;
            _open = true;
            if (Send($"play {Alias}") != 0)
            {
                // mciSendString reports failure through its return code, never
                // exceptions: a refused play (device busy, waveout exhausted)
                // would otherwise read as success — the composite would skip
                // the TTS fallback and the click would be silent. Release the
                // alias we just opened and report false so TTS takes over.
                Stop();
                return false;
            }
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
        try
        {
            // Clear the state bit only when winmm confirms the close. A failed
            // close leaves the alias alive in winmm; clearing _open anyway would
            // turn every later Stop into a no-op while the stale alias keeps
            // blocking the next open — the pack chain would silently stay dead
            // until restart. Keeping _open set makes the next Play retry the
            // close first, so state stays consistent with reality.
            if (Send($"close {Alias}") == 0) _open = false;
        }
        catch
        {
            // the interop binding itself may be the thing that is broken; the
            // same reasoning applies — keep _open so a later Stop retries
        }
    }

    private int Send(string command) =>
        _sendOverride is { } send ? send(command) : MciSendString(command, null, 0, nint.Zero);

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
