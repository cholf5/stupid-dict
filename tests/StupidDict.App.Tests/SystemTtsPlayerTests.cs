using System.Diagnostics;
using StupidDict.App.Speech;
using Xunit;

namespace StupidDict.App.Tests;

/// <summary>
/// macOS voice enumeration (B-011). The probe used to run synchronously on
/// the UI thread inside the first TTS fallback: ReadToEnd with no timeout
/// froze the app whenever `say` hung, and the timeout branch left orphans.
/// It now runs once on the thread pool through an injected probe seam, and
/// plays fall back to the default voice while the table is pending. Pure
/// [Fact] tests — the player constructor touches no Avalonia state; the
/// process-level mechanics of the bounded read live in SubprocessOutputTests.
/// </summary>
public sealed class SystemTtsPlayerTests
{
    private static readonly string SayListing =
        "Alex                en_US    # Most people recognize me by my voice.\n" +
        "Daniel              en_GB    # Hello, my name is Daniel.\n" +
        "Thomas              fr_FR    # Bonjour, je m'appelle Thomas.\n" +
        "Samantha            en_US    # Hello, my name is Samantha.\n";

    private static Dictionary<string, string> Parsed(string output)
    {
        var voices = new Dictionary<string, string>();
        SystemTtsPlayer.ParseVoiceList(output, voices);
        return voices;
    }

    // --- ParseVoiceList: pure parsing, every platform ---

    [Fact]
    public void ParseVoiceListExtractsEnglishVoiceNamesAndLocales()
    {
        var voices = Parsed(SayListing);

        Assert.Equal("en_US", voices["Alex"]);
        Assert.Equal("en_GB", voices["Daniel"]);
        Assert.Equal("en_US", voices["Samantha"]);
    }

    [Fact]
    public void ParseVoiceListSkipsNonEnglishAndMalformedLines()
    {
        var voices = Parsed("Thomas              fr_FR    # Bonjour.\n" +
                            "garbage line without columns\n" +
                            "\n");

        Assert.Empty(voices);
    }

    [Fact]
    public void ParseVoiceListKeepsTheFirstEntryForDuplicateNames()
    {
        var voices = Parsed("Daniel   en_GB\nDaniel   en_US\n");

        Assert.Equal("en_GB", voices["Daniel"]);
    }

    // --- SelectMacVoice: pure selection, every platform ---

    [Fact]
    public void SelectMacVoicePrefersThePlatformVoiceForTheAccent()
    {
        var voices = Parsed(SayListing);

        Assert.Equal("Daniel", SystemTtsPlayer.SelectMacVoice(voices, SpeechAccent.British));
        // AmericanPreferences lead with Samantha; Alex is only a later entry.
        Assert.Equal("Samantha", SystemTtsPlayer.SelectMacVoice(voices, SpeechAccent.American));
    }

    [Fact]
    public void SelectMacVoiceFallsBackToTheOtherEnglishLocale()
    {
        var voices = Parsed("Daniel   en_GB\n");

        // No en_US voice installed at all: an American word still gets Daniel.
        Assert.Equal("Daniel", SystemTtsPlayer.SelectMacVoice(voices, SpeechAccent.American));
    }

    [Fact]
    public void SelectMacVoiceReturnsNullForAnEmptyTableSoTheDefaultVoiceSpeaks()
    {
        Assert.Null(SystemTtsPlayer.SelectMacVoice([], SpeechAccent.British));
    }

    /// <summary>
    /// `say -v ?` lists enhanced voices as "Daniel (Enhanced)" — a distinct
    /// key, so the preference list misses it and the locale walk picks it up.
    /// Pinned because it is the selector's long-standing behavior, not an
    /// accident to be "fixed" silently.
    /// </summary>
    [Fact]
    public void SelectMacVoiceMatchesEnhancedVariantsOnlyThroughTheLocaleWalk()
    {
        var voices = Parsed("Daniel (Enhanced)   en_GB    # Hello.\n" +
                            "Alex                en_US    # Hello.\n");

        Assert.Equal("Daniel (Enhanced)", SystemTtsPlayer.SelectMacVoice(voices, SpeechAccent.British));
    }

    // --- SelectWindowsVoice: pure selection, every platform ---

    [Fact]
    public void SelectWindowsVoiceReturnsTheVoiceNameForTheExactAccentCulture()
    {
        var installed = new (string Name, string Culture)[]
        {
            ("Microsoft Huihui Desktop", "zh-CN"),
            ("Microsoft Zira Desktop", "en-US"),
            ("Microsoft Hazel Desktop", "en-GB"),
        };

        Assert.Equal("Microsoft Zira Desktop",
            SystemTtsPlayer.SelectWindowsVoice(installed, SpeechAccent.American));
        Assert.Equal("Microsoft Hazel Desktop",
            SystemTtsPlayer.SelectWindowsVoice(installed, SpeechAccent.British));
    }

    /// <summary>
    /// The return must be the voice NAME SpeechSynthesizer.SelectVoice(string)
    /// matches on — handing it the culture ("en-US") throws ArgumentException,
    /// and the caller's best-effort catch swallowed it, so Windows TTS never
    /// made a sound (2026-10-10, reproduced on a zh-CN box with Zira en-US).
    /// </summary>
    [Fact]
    public void SelectWindowsVoiceFallsBackToAnyEnglishVoiceNameWhenTheAccentCultureIsMissing()
    {
        var installed = new (string Name, string Culture)[]
        {
            ("Microsoft Huihui Desktop", "zh-CN"),
            ("Microsoft Zira Desktop", "en-US"),
        };

        // No en-GB voice: a British word still gets the en-US voice, by name.
        Assert.Equal("Microsoft Zira Desktop",
            SystemTtsPlayer.SelectWindowsVoice(installed, SpeechAccent.British));
    }

    [Fact]
    public void SelectWindowsVoiceReturnsNullWhenNoEnglishVoiceIsInstalled()
    {
        var installed = new (string Name, string Culture)[]
        {
            ("Microsoft Huihui Desktop", "zh-CN"),
        };

        Assert.Null(SystemTtsPlayer.SelectWindowsVoice(installed, SpeechAccent.American));
        Assert.Null(SystemTtsPlayer.SelectWindowsVoice(installed, SpeechAccent.British));
    }

    // --- MacVoicesNow: probe seam, unix test hosts (CI ubuntu + macOS) ---

    /// <summary>
    /// The enumeration is pending right after it starts (a probe that delays
    /// before writing), so the caller gets null and speaks with the default
    /// voice; once the probe settles the parsed table arrives, and the probe
    /// ran exactly once for the player's lifetime.
    /// </summary>
    [Fact]
    public async Task VoiceEnumerationRunsOnceOffTheCallerThreadAndReturnsNullUntilReady()
    {
        if (OperatingSystem.IsWindows()) return; // no /bin/sh there

        var starts = 0;
        var player = new SystemTtsPlayer(info =>
        {
            Interlocked.Increment(ref starts);
            info.FileName = "/bin/sh";
            info.ArgumentList.Clear();
            info.ArgumentList.Add("-c");
            info.ArgumentList.Add("sleep 0.5; printf 'Daniel   en_GB\\n'");
            return Process.Start(info);
        }, voiceProbeTimeoutMs: 5000);

        // First call kicks the probe off and must not block on it: the probe
        // needs 500ms before writing, so the answer right away is "not ready".
        var pending = player.MacVoicesNow();
        Assert.Null(pending);

        // The table lands afterwards, parsed from the substituted output.
        Dictionary<string, string>? voices = null;
        for (var attempt = 0; attempt < 100 && voices is null; attempt++)
        {
            await Task.Delay(100);
            voices = player.MacVoicesNow();
        }

        Assert.NotNull(voices);
        Assert.Equal("en_GB", voices["Daniel"]);
        Assert.Equal(1, starts);

        // Later calls reuse the cached table without re-probing.
        Assert.Same(voices, player.MacVoicesNow());
        Assert.Equal(1, starts);
    }

    /// <summary>A probe that cannot start degrades to an empty table, never an exception.</summary>
    [Fact]
    public async Task FailedProbeYieldsAnEmptyTableAndStillSettles()
    {
        var player = new SystemTtsPlayer(_ => null, voiceProbeTimeoutMs: 500);

        Dictionary<string, string>? voices = null;
        for (var attempt = 0; attempt < 50 && voices is null; attempt++)
        {
            await Task.Delay(50);
            voices = player.MacVoicesNow();
        }

        Assert.NotNull(voices);
        Assert.Empty(voices);
    }
}
