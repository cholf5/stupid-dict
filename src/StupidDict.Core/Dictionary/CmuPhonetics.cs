namespace StupidDict.Core.Dictionary;

/// <summary>
/// Converts CMUdict (ARPAbet) transcriptions to the American IPA shown next
/// to the British one from ECDICT. CMUdict itself is BSD-licensed; the
/// ARPAbet→IPA mapping below is the standard one used by Wiktionary-style
/// General American transcriptions: no length marks, r-colored vowels kept.
/// </summary>
public static class CmuPhonetics
{
    private static readonly Dictionary<string, string> Vowels = new()
    {
        ["AA"] = "ɑ", ["AE"] = "æ", ["AH"] = "ʌ", ["AO"] = "ɔ", ["AW"] = "aʊ",
        ["AY"] = "aɪ", ["EH"] = "ɛ", ["ER"] = "ɜr", ["EY"] = "eɪ", ["IH"] = "ɪ",
        ["IY"] = "i", ["OW"] = "oʊ", ["OY"] = "ɔɪ", ["UH"] = "ʊ", ["UW"] = "u",
    };

    private static readonly Dictionary<string, string> Consonants = new()
    {
        ["B"] = "b", ["CH"] = "tʃ", ["D"] = "d", ["DH"] = "ð", ["F"] = "f",
        ["G"] = "ɡ", ["HH"] = "h", ["JH"] = "dʒ", ["K"] = "k", ["L"] = "l",
        ["M"] = "m", ["N"] = "n", ["NG"] = "ŋ", ["P"] = "p", ["R"] = "r",
        ["S"] = "s", ["SH"] = "ʃ", ["T"] = "t", ["TH"] = "θ", ["V"] = "v",
        ["W"] = "w", ["Y"] = "j", ["Z"] = "z", ["ZH"] = "ʒ",
    };

    /// <summary>
    /// Multi-consonant onsets English phonotactics allows (as IPA). Stress
    /// marks go before the onset of the stressed syllable, so the converter
    /// needs them to split e.g. "consider" /kən.ˈsɪ/ vs "translation"
    /// /trænz.ˈleɪ/: the onset is the longest legal suffix of the
    /// consonant run between two vowels; single consonants are always legal.
    /// </summary>
    private static readonly HashSet<string> LegalOnsets = new()
    {
        "pl", "pr", "bl", "br", "tr", "tw", "dr", "dw", "kl", "kr", "kw",
        "ɡl", "ɡr", "fl", "fr", "θr", "ʃl", "ʃr", "sl", "sw", "sm", "sn",
        "sp", "st", "sk", "sf", "mj", "nj", "kj", "hj",
        "spl", "spr", "str", "skr", "skw",
    };

    private sealed record Piece(string Ipa, bool IsVowel, int Stress);

    /// <summary>
    /// Loads a cmudict file into lowercase headword → IPA. The first
    /// pronunciation of a headword wins; the "(2)"-suffixed alternates are
    /// skipped. Lines that fail to convert are dropped, never fatal.
    /// </summary>
    public static Dictionary<string, string> Load(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0 || line.StartsWith(";;;")) continue;

            var space = line.IndexOf(' ');
            if (space <= 0) continue;
            var word = line[..space];
            if (word.Contains('(')) continue;

            var ipa = ToIpa(line[(space + 1)..]);
            if (ipa.Length > 0) result.TryAdd(word.ToLowerInvariant(), ipa);
        }
        return result;
    }

    /// <summary>Transcribes one space-separated ARPAbet phoneme line to IPA.</summary>
    public static string ToIpa(string phonemes)
    {
        List<Piece> pieces = [];
        foreach (var phoneme in phonemes.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var (symbol, stress) = SplitStress(phoneme);
            if (Vowels.TryGetValue(symbol, out var ipa))
            {
                // Unstressed reductions: AH0 → schwa, ER0 → r-colored schwa.
                if (symbol == "AH" && stress == 0) ipa = "ə";
                if (symbol == "ER" && stress == 0) ipa = "ər";
                pieces.Add(new Piece(ipa, true, stress));
            }
            else if (Consonants.TryGetValue(symbol, out var consonant))
                pieces.Add(new Piece(consonant, false, 0));
            // unknown symbols are skipped rather than corrupting the line
        }

        var tokens = new List<string>();
        var pieceToken = new int[pieces.Count];
        for (var i = 0; i < pieces.Count; i++)
        {
            pieceToken[i] = tokens.Count;
            tokens.Add(pieces[i].Ipa);
        }

        var marks = new List<(int TokenIndex, string Mark)>();
        for (var i = 0; i < pieces.Count; i++)
        {
            if (!pieces[i].IsVowel || pieces[i].Stress == 0) continue;
            var clusterStart = i;
            while (clusterStart > 0 && !pieces[clusterStart - 1].IsVowel) clusterStart--;
            var onsetStart = i;
            if (i > clusterStart)
            {
                var onsetLength = LongestLegalOnset(pieces, clusterStart, i - clusterStart);
                onsetStart = i - onsetLength;
            }
            marks.Add((pieceToken[onsetStart], pieces[i].Stress == 1 ? "ˈ" : "ˌ"));
        }

        var sb = new System.Text.StringBuilder();
        var marksByToken = marks.ToLookup(m => m.TokenIndex, m => m.Mark);
        for (var t = 0; t < tokens.Count; t++)
        {
            foreach (var mark in marksByToken[t]) sb.Append(mark);
            sb.Append(tokens[t]);
        }
        return sb.ToString();
    }

    private static int LongestLegalOnset(List<Piece> pieces, int clusterStart, int clusterLength)
    {
        for (var length = clusterLength; length > 1; length--)
        {
            var candidate = string.Concat(pieces.GetRange(clusterStart + clusterLength - length, length).Select(p => p.Ipa));
            if (LegalOnsets.Contains(candidate)) return length;
        }
        return 1;
    }

    private static (string Symbol, int Stress) SplitStress(string phoneme)
    {
        if (phoneme.Length > 1 && char.IsAsciiDigit(phoneme[^1]))
            return (phoneme[..^1], phoneme[^1] - '0');
        return (phoneme, 0);
    }
}
