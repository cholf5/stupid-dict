namespace StupidDict.Core.Dictionary;

/// <summary>English headword side: lookup by headword / word form, plus prefix suggestions.</summary>
public sealed class EnglishChineseDictionary
{
    private readonly DictionaryStore _store;

    internal EnglishChineseDictionary(DictionaryStore store) => _store = store;

    /// <summary>Case-sensitive exact hit — what Enter must prefer above everything else.</summary>
    public DictionaryEntry? FindWord(string word) => _store.FindWord(word);

    /// <summary>Hit after trim + lowercase ("CAT" → "cat").</summary>
    public DictionaryEntry? FindNormalized(string lower) => _store.FindNormalized(lower);

    /// <summary>Resolves an inflected form ("cats") to its base word ("cat").</summary>
    public DictionaryEntry? FindByWordForm(string lower) => _store.FindByWordForm(lower);

    /// <summary>Headwords starting with the query, common words first.</summary>
    public List<DictionaryEntry> FindPrefix(string lower, int limit) => _store.FindPrefix(lower, limit);
}
