namespace StupidDict.App.Translation;

/// <summary>Rejects obviously unusable model output; it is a guard, not a quality scorer.</summary>
public static class TranslationQualityGate
{
    private static readonly string[] RefusalMarkers =
    [
        "i can't", "i cannot", "i'm sorry", "sorry, i", "as an ai",
        "不能翻译", "无法翻译", "作为 ai", "我不能", "无法完成",
    ];

    public static TranslationFailureKind? FindProblem(
        string source, string translation, TranslationDirection direction)
    {
        var text = translation.Trim();
        if (text.Length == 0)
            return TranslationFailureKind.Incomplete;

        if (text.EndsWith(',') || text.EndsWith('，') || text.EndsWith('、')
            || text.EndsWith(':') || text.EndsWith('：') || text.EndsWith(';') || text.EndsWith('；'))
            return TranslationFailureKind.Incomplete;

        if (HasRepeatedRun(text))
            return TranslationFailureKind.Incomplete;

        if (LooksLikeRefusal(text))
            return TranslationFailureKind.Refused;

        if (TooShort(source, text, direction))
            return TranslationFailureKind.Incomplete;

        return null;
    }

    private static bool HasRepeatedRun(string text)
    {
        var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var run = 1;
        for (var i = 1; i < tokens.Length; i++)
        {
            run = tokens[i] == tokens[i - 1] ? run + 1 : 1;
            if (run >= 3) return true;
        }

        var compact = string.Concat(tokens);
        for (var n = 3; n <= 8; n++)
        {
            for (var i = 0; i + n * 3 <= compact.Length; i++)
            {
                if (compact.AsSpan(i, n).SequenceEqual(compact.AsSpan(i + n, n))
                    && compact.AsSpan(i, n).SequenceEqual(compact.AsSpan(i + n * 2, n)))
                    return true;
            }
        }

        return false;
    }

    private static bool LooksLikeRefusal(string text)
    {
        var lower = text.ToLowerInvariant();
        return RefusalMarkers.Any(marker => lower.Contains(marker, StringComparison.Ordinal));
    }

    private static bool TooShort(string source, string translation, TranslationDirection direction)
    {
        var sourceWords = source.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        if (direction == TranslationDirection.EnglishToChinese)
            return sourceWords >= 8 && TranslationText.CountCjk(translation) < 2;
        return TranslationText.CountCjk(source) >= 8
            && TranslationText.CountLatinLetters(translation) < 3;
    }
}
