namespace StupidDict.Core.Dictionary;

/// <summary>Chinese term → English headword, backed by the zh_index reverse index.</summary>
public sealed class ChineseEnglishDictionary
{
    private readonly DictionaryStore _store;

    internal ChineseEnglishDictionary(DictionaryStore store) => _store = store;

    /// <summary>Entries whose Chinese gloss contains the term exactly, common words first.</summary>
    public List<ChineseMatch> FindTerm(string term, int limit) => _store.FindTerm(term, limit);

    /// <summary>Entries whose Chinese gloss starts with the term.</summary>
    public List<ChineseMatch> FindTermPrefix(string term, int limit) => _store.FindTermPrefix(term, limit);
}
