using System.Text;
using System.Text.RegularExpressions;

namespace StupidDict.App.Translation;

public static class TranslationText
{
    private static readonly char[] SentenceTerminators = ['.', '?', '!', '。', '？', '！', '…'];

    public static bool LooksLikeSentence(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return false;

        var tokens = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length >= 2) return true;
        if (CountCjk(trimmed) >= 6) return true;
        return CountCjk(trimmed) >= 2 && trimmed.IndexOfAny(SentenceTerminators) >= 0;
    }

    public static TranslationDirection DetectDirection(string text)
    {
        var cjk = CountCjk(text);
        var latinWords = CountLatinWords(text);
        return cjk > latinWords ? TranslationDirection.ChineseToEnglish : TranslationDirection.EnglishToChinese;
    }

    /// <summary>
    /// Direction counts latin word runs, not letters: "OpenAI 发布了 GPT-5。" is
    /// a Chinese sentence with embedded brand names, and letter counting would
    /// misfile it as English — then the quality gate's English-to-Chinese
    /// threshold would reject its correct English translation forever.
    /// </summary>
    private static int CountLatinWords(string text)
    {
        var count = 0;
        var inWord = false;
        foreach (var c in text)
        {
            if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z')
            {
                if (!inWord) count++;
                inWord = true;
            }
            else
            {
                inWord = false;
            }
        }
        return count;
    }

    /// <summary>Whitespace/Unicode normalization only; case and punctuation stay significant for cache keys.</summary>
    public static string NormalizeForCache(string text) =>
        Regex.Replace(
            text.Replace("\r\n", "\n").Replace('\r', '\n').Normalize(NormalizationForm.FormC).Trim(),
            @"[ \t]+", " ");

    public static int CountCjk(string text)
    {
        var count = 0;
        foreach (var rune in text.EnumerateRunes())
            if (rune.Value is >= 0x3400 and <= 0x9FFF
                or >= 0xF900 and <= 0xFAFF
                or >= 0x20000 and <= 0x2FA1F
                or >= 0x30000 and <= 0x323AF)
                count++;
        return count;
    }

    public static int CountLatinLetters(string text)
    {
        var count = 0;
        foreach (var c in text)
            if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z')
                count++;
        return count;
    }
}
