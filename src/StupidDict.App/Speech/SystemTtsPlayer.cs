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

    private readonly ProcessPlayer _process = new();
    private Dictionary<string, string>? _macVoices;

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
            if (SelectVoice(_synthesizer, accent) is not { } voice) return false;
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

    private static string? SelectVoice(System.Speech.Synthesis.SpeechSynthesizer synthesizer, SpeechAccent accent)
    {
        var wanted = accent == SpeechAccent.British ? "en-GB" : "en-US";
        var installed = synthesizer.GetInstalledVoices()
            .Where(v => v.Enabled)
            .Select(v => v.VoiceInfo.Culture.Name)
            .ToList();
        return installed.FirstOrDefault(c => c.Equals(wanted, StringComparison.OrdinalIgnoreCase))
            ?? installed.FirstOrDefault(c => c.StartsWith("en", StringComparison.OrdinalIgnoreCase));
    }
#endif

    /// <summary>Parses `say -v ?` once: voice name → locale ("Daniel" → "en_GB").</summary>
    private Dictionary<string, string> MacVoices()
    {
        if (_macVoices is not null) return _macVoices;
        _macVoices = [];
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
            using var process = System.Diagnostics.Process.Start(info);
            if (process is null) return _macVoices;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(3000);
            foreach (var line in output.Split('\n'))
            {
                // "Daniel (Enhanced)      en_GB    # Hello, my name is Daniel."
                var columns = Regex.Split(line.Trim(), @"\s{2,}");
                if (columns.Length >= 2 && columns[1].StartsWith("en_"))
                    _macVoices.TryAdd(columns[0], columns[1]);
            }
        }
        catch
        {
            // enumeration is best-effort; the default voice remains available
        }
        return _macVoices;
    }

    private string? VoiceFor(SpeechAccent accent)
    {
        var voices = MacVoices();
        if (voices.Count == 0) return null;

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
