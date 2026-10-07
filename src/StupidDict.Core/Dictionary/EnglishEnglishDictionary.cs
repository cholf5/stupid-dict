namespace StupidDict.Core.Dictionary;

/// <summary>English-English side: fuzzy "did you mean" suggestions over the common-word set.</summary>
public sealed class EnglishEnglishDictionary
{
    private readonly DictionaryStore _store;
    private readonly CommonWordIndex _commonWords;

    internal EnglishEnglishDictionary(DictionaryStore store, CommonWordIndex commonWords)
    {
        _store = store;
        _commonWords = commonWords;
    }

    /// <summary>Common words within a small edit distance of the query, nearest and most common first.</summary>
    public List<DictionaryEntry> FindSimilar(string lower, int limit)
    {
        var entries = new List<DictionaryEntry>(limit);
        foreach (var word in FuzzyMatcher.Find(lower, _commonWords.Words, limit))
        {
            if (_store.FindNormalized(word) is { } entry) entries.Add(entry);
        }
        return entries;
    }
}
