namespace StupidDict.Core.Dictionary;

/// <summary>English-English side: fuzzy "did you mean" suggestions over the common-word set.</summary>
public sealed class EnglishEnglishDictionary
{
    private readonly DictionaryStore _store;
    private List<CommonWord>? _commonWords;

    internal EnglishEnglishDictionary(DictionaryStore store) => _store = store;

    /// <summary>Common words within a small edit distance of the query, nearest and most common first.</summary>
    public List<DictionaryEntry> FindSimilar(string lower, int limit)
    {
        _commonWords ??= _store.GetCommonWords();
        var entries = new List<DictionaryEntry>(limit);
        foreach (var word in FuzzyMatcher.Find(lower, _commonWords, limit))
        {
            if (_store.FindNormalized(word) is { } entry) entries.Add(entry);
        }
        return entries;
    }
}
