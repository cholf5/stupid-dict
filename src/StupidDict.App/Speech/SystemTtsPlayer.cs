using System.Diagnostics;
using System.Text.RegularExpressions;

namespace StupidDict.App.Speech;

/// <summary>
/// Offline text-to-speech through the OS, the fallback when the audio pack
/// has no recording for a word. macOS uses the preinstalled voices via say
/// (British: Daniel/Arthur/Gordon, American: Samantha/Aaron); Windows drives
/// the same SAPI voices through a Windows PowerShell one-liner (in-proc
/// System.Speech is COM interop, which NativeAOT cannot run); Linux uses
/// espeak. All of them are free, no download.
/// </summary>
public sealed class SystemTtsPlayer : ISpeechPlayer
{
    private static readonly string[] BritishPreferences = ["Daniel", "Arthur", "Serena", "Kate", "Gordon", "Sonia"];
    private static readonly string[] AmericanPreferences = ["Samantha", "Aaron", "Ava", "Alex", "Allison", "Zoe", "Nicky"];

    private const int VoiceProbeTimeoutMs = 3000;

    private readonly ProcessPlayer _process = new();
    // Voice enumeration runs off the UI thread at most once (B-011): the first
    // TTS fallback kicks it off and speaks with the default voice while the
    // probe is still running, later plays pick from the parsed table.
    private readonly object _voicesGate = new();
    private Task<Dictionary<string, string>>? _voices;
    // Test seam for the probe (B-011): null in production (real `say -v ?` /
    // powershell listing below); a test substitutes its own command. The
    // timeout rides along so a test can use a short deadline against a hung
    // substitute.
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

    public bool Play(string word, SpeechAccent accent)
    {
        if (OperatingSystem.IsMacOS()) return PlayMac(word, accent);
        if (OperatingSystem.IsWindows()) return PlayWindows(word, accent);
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

    private bool PlayWindows(string word, SpeechAccent accent)
    {
        var powershell = WindowsPowerShellPath();
        if (powershell is null) return false;
        var voices = VoicesNow();
        var voice = voices is null ? null : SelectWindowsVoice(WindowsVoiceList(voices), accent);
        // Table landed and no English voice is installed: nothing to say (the
        // old in-proc code returned false in the same case). While the probe
        // is still pending, speak with the default voice instead — the B-011
        // semantics the macOS path already has.
        if (voices is not null && voice is null) return false;
        return _process.Play(powershell, "-NoProfile", "-NonInteractive", "-Command", WindowsSpeakScript(word, voice));
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

    /// <summary>
    /// Windows voice selection, pure so tests can run off the SAPI stack (CI
    /// is ubuntu). Returns the voice NAME — the PowerShell script selects by
    /// name only; handing SelectVoice a culture string ("en-US") throws
    /// ArgumentException, which the best-effort catch swallowed, so Windows
    /// TTS never made a sound (2026-10-10).
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
    /// Exit-time cleanup (B-012): kills an in-flight CLI voice (say, the
    /// powershell one-liner, espeak). Best-effort — every fault is swallowed
    /// so the window-close path never breaks.
    /// </summary>
    public void Stop()
    {
        _process.Stop();
    }

    private string? VoiceFor(SpeechAccent accent)
    {
        var voices = VoicesNow();
        if (voices is null) return null;
        return OperatingSystem.IsWindows()
            ? SelectWindowsVoice(WindowsVoiceList(voices), accent)
            : SelectMacVoice(voices, accent);
    }

    private static List<(string Name, string Culture)> WindowsVoiceList(Dictionary<string, string> voices) =>
        voices.Select(v => (Name: v.Key, Culture: v.Value)).ToList();

    /// <summary>
    /// The one-liner that speaks on Windows. Synchronous Speak: the process
    /// exits when the utterance ends, so ProcessPlayer's Stop (kill) cuts the
    /// speech exactly like it cuts `say`. Single-quoted PowerShell literals;
    /// a single quote inside doubles (headwords are [a-z' -], escape anyway).
    /// </summary>
    private static string WindowsSpeakScript(string word, string? voice)
    {
        static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
        var select = voice is { } name ? "$s.SelectVoice(" + Quote(name) + "); " : "";
        return "Add-Type -AssemblyName System.Speech; " +
               "$s = New-Object System.Speech.Synthesis.SpeechSynthesizer; " +
               select +
               "$s.Speak(" + Quote(word) + ")";
    }

    /// <summary>
    /// Resolves Windows PowerShell (ships with every Windows; pwsh is not a
    /// given). System32 explicitly first — a mangled PATH should not kill the
    /// fallback — then PATH.
    /// </summary>
    private static string? WindowsPowerShellPath()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
        if (!string.IsNullOrEmpty(systemRoot))
        {
            var candidate = Path.Combine(systemRoot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return PathLookup.Which("powershell");
    }

    /// <summary>
    /// The parsed voice table, or null while the first enumeration has not
    /// finished yet. The probe (`say -v ?` on macOS, a PowerShell SAPI voice
    /// listing on Windows — reads can hang on a wedged speech stack) runs on
    /// the thread pool at most once per player, never on the UI thread
    /// (B-011): callers speak with the default voice until the table lands.
    /// Internal for tests, which drive the probe seam.
    /// </summary>
    internal Dictionary<string, string>? VoicesNow()
    {
        if (_voices is null)
        {
            lock (_voicesGate)
            {
                _voices ??= Task.Run(EnumerateVoices);
            }
        }

        return _voices.Status == TaskStatus.RanToCompletion ? _voices.Result : null;
    }

    private Dictionary<string, string> EnumerateVoices()
    {
        var voices = new Dictionary<string, string>();
        try
        {
            var info = VoiceProbeInfo();
            if (info is null) return voices;
            using var process = _voiceProbeOverride is { } probe ? probe(info) : Process.Start(info);
            if (process is not null)
            {
                var output = SubprocessOutput.ReadWithTimeout(process, _voiceProbeTimeoutMs);
                if (output is not null)
                {
                    if (OperatingSystem.IsWindows()) ParseWindowsVoiceList(output, voices);
                    else ParseVoiceList(output, voices);
                }
            }
        }
        catch
        {
            // enumeration is best-effort; the default voice remains available
        }

        return voices;
    }

    /// <summary>
    /// Builds the probe command for the platform, or null when there is
    /// nothing to probe (Linux's espeak takes a -v variant directly; a
    /// Windows box without Windows PowerShell has no SAPI stack either).
    /// With the test seam injected the platform check is bypassed: tests run
    /// on every host and rewrite the info entirely (ubuntu CI drives the
    /// macOS parse path through /bin/sh).
    /// </summary>
    private ProcessStartInfo? VoiceProbeInfo()
    {
        if (_voiceProbeOverride is not null)
            return new ProcessStartInfo
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
        if (OperatingSystem.IsMacOS())
        {
            return new ProcessStartInfo
            {
                FileName = "/usr/bin/say",
                ArgumentList = { "-v", "?" },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
        }
        if (OperatingSystem.IsWindows())
        {
            var powershell = WindowsPowerShellPath();
            if (powershell is null) return null;
            // One line per installed voice, "Name|Culture" — cultures never
            // contain a pipe, so the parser splits on the last one even if a
            // voice name ever did.
            return new ProcessStartInfo
            {
                FileName = powershell,
                ArgumentList =
                {
                    "-NoProfile", "-NonInteractive", "-Command",
                    "Add-Type -AssemblyName System.Speech; " +
                    "$s = New-Object System.Speech.Synthesis.SpeechSynthesizer; " +
                    "$s.GetInstalledVoices() | ForEach-Object { $_.VoiceInfo.Name + '|' + $_.VoiceInfo.Culture.Name }",
                },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
        }
        return null;
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

    /// <summary>Parses the PowerShell listing: "Name|Culture" → ("Microsoft Zira Desktop" → "en-US").</summary>
    internal static void ParseWindowsVoiceList(string output, Dictionary<string, string> voices)
    {
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            var separator = trimmed.LastIndexOf('|');
            if (separator <= 0 || separator == trimmed.Length - 1) continue;
            voices.TryAdd(trimmed[..separator].Trim(), trimmed[(separator + 1)..].Trim());
        }
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
