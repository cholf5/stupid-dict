namespace StupidDict.Core.Dictionary;

/// <summary>English headword side: lookup by headword / word form, plus prefix suggestions.</summary>
public sealed class EnglishChineseDictionary
{
    private readonly DictionaryStore _store;
    private readonly CommonWordIndex _commonWords;

    internal EnglishChineseDictionary(DictionaryStore store, CommonWordIndex commonWords)
    {
        _store = store;
        _commonWords = commonWords;
    }

    /// <summary>Case-sensitive exact hit — what Enter must prefer above everything else.</summary>
    public DictionaryEntry? FindWord(string word) => _store.FindWord(word);

    /// <summary>Hit after trim + lowercase ("CAT" → "cat").</summary>
    public DictionaryEntry? FindNormalized(string lower) => _store.FindNormalized(lower);

    /// <summary>Resolves an inflected form ("cats") to its base word ("cat").</summary>
    public DictionaryEntry? FindByWordForm(string lower) => _store.FindByWordForm(lower);

    /// <summary>Headwords starting with the query, common words first.</summary>
    public List<DictionaryEntry> FindPrefix(string lower, int limit) => _store.FindPrefix(lower, limit);

    /// <summary>
    /// Live completion for short prefixes: common words only, most common first.
    /// The in-memory scan is what makes per-keystroke latency possible where the
    /// full-table SQLite prefix scan costs ~100 ms (1 char) to ~1.7 s (common set load).
    /// </summary>
    public List<string> SuggestFromCommon(string lower, int limit)
    {
        List<string> words = new(limit);
        foreach (var candidate in _commonWords.FindPrefix(lower, limit))
            if (_store.FindNormalized(candidate) is { } entry)
                words.Add(entry.Word);
        return words;
    }

    /// <summary>Live completion for longer prefixes: the SQLite index path, reaches the long tail.</summary>
    public List<string> SuggestPrefix(string lower, int limit) =>
        _store.FindPrefix(lower, limit).Select(entry => entry.Word).ToList();
}
