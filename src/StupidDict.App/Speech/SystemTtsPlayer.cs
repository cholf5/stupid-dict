using System.Text.RegularExpressions;

namespace StupidDict.App.Speech;

/// <summary>
/// Offline text-to-speech through the OS, the fallback when the audio pack
/// has no recording for a word. macOS uses the preinstalled voices via say
/// (British: Daniel/Arthur/Gordon, American: Samantha/Aaron); Windows uses
/// System.Speech; Linux uses espeak. All of them are free, no download.
/// </summary>
public sealed class SystemTtsPlayer : ISpeechPlayer
{
    private static readonly string[] BritishPreferences = ["Daniel", "Arthur", "Serena", "Kate", "Gordon", "Sonia"];
    private static readonly string[] AmericanPreferences = ["Samantha", "Aaron", "Ava", "Alex", "Allison", "Zoe", "Nicky"];

    private const int VoiceProbeTimeoutMs = 3000;

    private readonly ProcessPlayer _process = new();
    // Voice enumeration runs off the UI thread at most once (B-011): the first
    // macOS playback kicks it off and speaks with the default voice while the
    // probe is still running, later plays pick from the parsed table.
    private readonly object _macVoicesGate = new();
    private Task<Dictionary<string, string>>? _macVoices;
    // Test seam for the probe (B-011): null in production (real `say -v ?`
    // below); a test substitutes its own command. The timeout rides along so a
    // test can use a short deadline against a hung substitute.
    private readonly ProcessStarter? _voiceProbeOverride;
    private readonly int _voiceProbeTimeoutMs;

    public SystemTtsPlayer() : this(null, VoiceProbeTimeoutMs)
    {
    }

    internal SystemTtsPlayer(ProcessStarter? voiceProbeOverride, int voiceProbeTimeoutMs)
    {
        _voiceProbeOverride = voiceProbeOverride;
        _voiceProbeTimeoutMs = voiceProbeTimeoutMs;
    }

    /// <summary>Starts a probe process and returns it, or null when it could not start.</summary>
    internal delegate System.Diagnostics.Process? ProcessStarter(System.Diagnostics.ProcessStartInfo info);

#if WINDOWS
    private System.Speech.Synthesis.SpeechSynthesizer? _synthesizer;
#endif

    public bool Play(string word, SpeechAccent accent)
    {
        if (OperatingSystem.IsMacOS()) return PlayMac(word, accent);
#if WINDOWS
        if (OperatingSystem.IsWindows()) return PlayWindows(word, accent);
#endif
        if (OperatingSystem.IsLinux()) return PlayLinux(word, accent);
        return false;
    }

    private bool PlayMac(string word, SpeechAccent accent)
    {
        if (!File.Exists("/usr/bin/say")) return false;
        var voice = VoiceFor(accent);
        return voice is not null
            ? _process.Play("/usr/bin/say", "-v", voice, word)
            : _process.Play("/usr/bin/say", word);
    }

    private bool PlayLinux(string word, SpeechAccent accent)
    {
        foreach (var program in new[] { "espeak-ng", "espeak" })
        {
            var path = PathLookup.Which(program);
            if (path is null) continue;
            var variant = accent == SpeechAccent.British ? "en-gb" : "en-us";
            return _process.Play(path, "-v", variant, word);
        }
        return false;
    }

#if WINDOWS
    private bool PlayWindows(string word, SpeechAccent accent)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            _synthesizer ??= new System.Speech.Synthesis.SpeechSynthesizer();
            var installed = _synthesizer.GetInstalledVoices()
                .Where(v => v.Enabled)
                .Select(v => (v.VoiceInfo.Name, Culture: v.VoiceInfo.Culture.Name))
                .ToList();
            if (SelectWindowsVoice(installed, accent) is not { } voice) return false;
            _synthesizer.SpeakAsyncCancelAll();
            _synthesizer.SelectVoice(voice);
            _synthesizer.SpeakAsync(word);
            return true;
        }
        catch
        {
            return false;
        }
    }
#endif

    /// <summary>
    /// Windows voice selection, pure so tests can run off the SAPI stack (CI
    /// is ubuntu). Returns the voice NAME — SpeechSynthesizer.SelectVoice
    /// matches names only; handing it a culture string ("en-US") throws
    /// ArgumentException, which PlayWindows's best-effort catch swallowed,
    /// so Windows TTS never made a sound (2026-10-10).
    /// </summary>
    internal static string? SelectWindowsVoice(
        IReadOnlyList<(string Name, string Culture)> installed, SpeechAccent accent)
    {
        var wanted = accent == SpeechAccent.British ? "en-GB" : "en-US";
        foreach (var (name, culture) in installed)
            if (culture.Equals(wanted, StringComparison.OrdinalIgnoreCase)) return name;
        foreach (var (name, culture) in installed)
            if (culture.StartsWith("en", StringComparison.OrdinalIgnoreCase)) return name;
        return null;
    }

    /// <summary>
    /// Exit-time cleanup (B-012): kills an in-flight CLI voice, and on Windows
    /// cancels the async utterance and releases the synthesizer. Best-effort —
    /// every fault is swallowed so the window-close path never breaks.
    /// </summary>
    public void Stop()
    {
        _process.Stop();
#if WINDOWS
        try
        {
            if (_synthesizer is { } synthesizer)
            {
                // Cancel first so the in-flight SpeakAsync is not running while
                // the engine is disposed; null the field so a later Play
                // recreates the synthesizer instead of touching a disposed one.
                synthesizer.SpeakAsyncCancelAll();
                synthesizer.Dispose();
                _synthesizer = null;
            }
        }
        catch
        {
            // a broken synthesizer must not break the close path
        }
#endif
    }

    /// <summary>
    /// The parsed voice table, or null while the first enumeration has not
    /// finished yet. The probe (`say -v ?` — reads can hang on a wedged
    /// speech stack) runs on the thread pool at most once per player, never
    /// on the UI thread (B-011): callers speak with the default voice until
    /// the table lands. Internal for tests, which drive the probe seam.
    /// </summary>
    internal Dictionary<string, string>? MacVoicesNow()
    {
        if (_macVoices is null)
        {
            lock (_macVoicesGate)
            {
                _macVoices ??= Task.Run(EnumerateMacVoices);
            }
        }

        return _macVoices.Status == TaskStatus.RanToCompletion ? _macVoices.Result : null;
    }

    private Dictionary<string, string> EnumerateMacVoices()
    {
        var voices = new Dictionary<string, string>();
        try
        {
            var info = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "/usr/bin/say",
                ArgumentList = { "-v", "?" },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            using var process = _voiceProbeOverride is { } probe ? probe(info) : System.Diagnostics.Process.Start(info);
            if (process is not null)
            {
                var output = SubprocessOutput.ReadWithTimeout(process, _voiceProbeTimeoutMs);
                if (output is not null) ParseVoiceList(output, voices);
            }
        }
        catch
        {
            // enumeration is best-effort; the default voice remains available
        }

        return voices;
    }

    /// <summary>Parses `say -v ?` output: voice name → locale ("Daniel" → "en_GB").</summary>
    internal static void ParseVoiceList(string output, Dictionary<string, string> voices)
    {
        foreach (var line in output.Split('\n'))
        {
            // "Daniel (Enhanced)      en_GB    # Hello, my name is Daniel."
            var columns = Regex.Split(line.Trim(), @"\s{2,}");
            if (columns.Length >= 2 && columns[1].StartsWith("en_"))
                voices.TryAdd(columns[0], columns[1]);
        }
    }

    private string? VoiceFor(SpeechAccent accent)
    {
        var voices = MacVoicesNow();
        return voices is null ? null : SelectMacVoice(voices, accent);
    }

    /// <summary>
    /// Picks a voice for the accent: the known-good per-accent list first, then
    /// any voice for the target locale, then the other English locale. Null —
    /// including an empty or still-pending table — means "speak with the
    /// default voice", which is how a failed enumeration already behaved.
    /// Internal for tests.
    /// </summary>
    internal static string? SelectMacVoice(Dictionary<string, string> voices, SpeechAccent accent)
    {
        var target = accent == SpeechAccent.British ? "en_GB" : "en_US";
        var fallback = accent == SpeechAccent.British ? "en_US" : "en_GB";
        var preferences = accent == SpeechAccent.British ? BritishPreferences : AmericanPreferences;

        foreach (var name in preferences)
            if (voices.TryGetValue(name, out var locale) && locale.StartsWith(target, StringComparison.Ordinal))
                return name;
        foreach (var (name, locale) in voices)
            if (locale.StartsWith(target, StringComparison.Ordinal)) return name;
        foreach (var (name, locale) in voices)
            if (locale.StartsWith(fallback, StringComparison.Ordinal)) return name;
        return null;
    }
}
