using System.Text;
using StupidDict.Core.Dictionary;
using StupidDict.Core.History;

namespace StupidDict.Core.Application;

/// <summary>
/// Turns a raw query string into a <see cref="LookupResult"/>. Implements the
/// match ordering the product depends on: exact → normalized exact → word form
/// → prefix → fuzzy. Enter always means "look up what I typed", never
/// "jump to the top suggestion".
/// </summary>
public sealed class DictionaryService : IDisposable
{
    private const int SuggestLimit = 8;

    private readonly Lazy<DictionaryDatabase> _database;
    private readonly CommonWordIndex _commonWords;
    private readonly EnglishChineseDictionary _englishChinese;
    private readonly EnglishEnglishDictionary _englishEnglish;
    private readonly ChineseEnglishDictionary _chineseEnglish;

    public string DictionaryPath { get; }

    public RecentSearchStore History { get; }

    public DictionaryService(string dictionaryPath, string historyPath)
    {
        DictionaryPath = dictionaryPath;
        _database = new Lazy<DictionaryDatabase>(() => DictionaryDatabase.OpenRead(dictionaryPath));
        var store = new DictionaryStore(_database);
        _commonWords = new CommonWordIndex(store.GetCommonWords);
        _englishChinese = new EnglishChineseDictionary(store, _commonWords);
        _englishEnglish = new EnglishEnglishDictionary(store, _commonWords);
        _chineseEnglish = new ChineseEnglishDictionary(store);
        History = new RecentSearchStore(historyPath);
    }

    public Task<LookupResult> LookupAsync(string query) => Task.Run(() => Lookup(query));

    /// <summary>
    /// Loads the common-word index off the UI thread. Call once at startup so the
    /// first keystroke and the first fuzzy miss don't pay the full-table scan.
    /// </summary>
    public Task WarmupAsync() => Task.Run(() => _ = _commonWords.Words);

    public LookupResult Lookup(string query)
    {
        var trimmed = query.Trim();
        if (trimmed.Length == 0)
            return LookupResult.NotFound(trimmed, IsChineseQuery(trimmed));
        return IsChineseQuery(trimmed) ? LookupChinese(trimmed) : LookupEnglish(trimmed);
    }

    private LookupResult LookupEnglish(string query)
    {
        var lower = query.ToLowerInvariant();

        var primary = _englishChinese.FindWord(query);
        var tier = MatchTier.Exact;
        if (primary is null)
        {
            primary = _englishChinese.FindNormalized(lower);
            if (primary is not null) tier = MatchTier.NormalizedExact;
        }

        string? wordFormNote = null;
        if (primary is null)
        {
            primary = _englishChinese.FindByWordForm(lower);
            if (primary is not null)
            {
                tier = MatchTier.WordForm;
                wordFormNote = primary.Word;
            }
        }

        List<DictionaryEntry> suggestions = [];
        var prefixCount = 0;
        if (primary is null)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var prefixMatch in _englishChinese.FindPrefix(lower, 10))
                if (seen.Add(prefixMatch.Word)) suggestions.Add(prefixMatch);
            prefixCount = suggestions.Count;
            foreach (var similar in _englishEnglish.FindSimilar(lower, 10))
                if (seen.Add(similar.Word)) suggestions.Add(similar);
            if (suggestions.Count > 10)
                suggestions = suggestions.Take(10).ToList();
        }

        if (primary is null && suggestions.Count == 0)
            return LookupResult.NotFound(query, isChinese: false);

        if (primary is not null)
            History.Add(query);

        var thesaurus = primary is not null
            ? _englishChinese.GetThesaurus(primary.Word.ToLowerInvariant())
            : null;

        return new LookupResult
        {
            Query = query,
            IsChineseQuery = false,
            Tier = primary is not null ? tier : prefixCount > 0 ? MatchTier.Prefix : MatchTier.Fuzzy,
            Primary = primary,
            WordFormNote = wordFormNote,
            WordSuggestions = suggestions,
            Synonyms = thesaurus?.Synonyms ?? [],
            Antonyms = thesaurus?.Antonyms ?? [],
            RelatedWords = thesaurus?.RelatedWords ?? [],
        };
    }

    private LookupResult LookupChinese(string query)
    {
        var exact = _chineseEnglish.FindTerm(query, 12);
        if (exact.Count > 0)
        {
            History.Add(query);
            return new LookupResult
            {
                Query = query,
                IsChineseQuery = true,
                Tier = MatchTier.Chinese,
                Primary = exact[0].Entry,
                ChineseMatches = exact,
            };
        }

        var related = _chineseEnglish.FindTermPrefix(query, 12);
        if (related.Count == 0)
            return LookupResult.NotFound(query, isChinese: true);

        return new LookupResult
        {
            Query = query,
            IsChineseQuery = true,
            Tier = MatchTier.Prefix,
            ChineseMatches = related,
        };
    }

    /// <summary>
    /// Live completion while typing. Short prefixes (1–2 chars) come from the
    /// in-memory common-word set — the full-table SQLite scan costs ~100 ms there
    /// and rare words are noise at that length; longer prefixes use the SQLite
    /// index (~1 ms) which reaches the long tail. When the typed word is itself
    /// a headword it leads the list. Chinese queries return nothing: IME
    /// composition makes per-character prefixes meaningless.
    /// </summary>
    public Task<List<string>> SuggestAsync(string query) => Task.Run(() => Suggest(query));

    public List<string> Suggest(string query)
    {
        var trimmed = query.Trim();
        if (trimmed.Length == 0 || IsChineseQuery(trimmed)) return [];
        var lower = trimmed.ToLowerInvariant();
        var words = lower.Length <= 2
            ? _englishChinese.SuggestFromCommon(lower, SuggestLimit)
            : _englishChinese.SuggestPrefix(lower, SuggestLimit);

        // The typed word itself, when it exists, always leads the list: by the
        // time the prefix is a complete headword, looking it up is the dominant
        // intent — corpus commonality (catch before cat) must not bury it.
        if (_englishChinese.FindNormalized(lower) is { } exact)
        {
            words.Remove(exact.Word);
            words.Insert(0, exact.Word);
            if (words.Count > SuggestLimit) words.RemoveAt(words.Count - 1);
        }
        return words;
    }

    /// <summary>
    /// True when the text contains any CJK ideograph — the routing switch between
    /// the English and Chinese paths (and the UI's no-completion / no-double-click
    /// gates). Only the ideographs themselves count; their surrounding punctuation
    /// and symbol blocks (、。ＣＡＴ fullwidth forms, radicals, Seal script) do not —
    /// a query carrying punctuation is still classified by its characters. Accepted
    /// ranges (Unicode 18.0 Blocks.txt): U+3007 (〇, as in 二〇二五), U+3400–U+9FFF
    /// (Ext A + URO; as before, the span also sweeps in the Yijing hexagram symbols
    /// 4DC0–4DFF sitting inside it), U+F900–U+FAFF (compatibility ideographs —
    /// already a superset of the builder's zh_index term class), and every CJK
    /// Unified Ideographs Extension block from Ext B on (U+20000+, blocks listed
    /// below). Supplementary characters are compared as code points via
    /// <see cref="Rune"/>: per-UTF-16-unit checks can never see them, so 𠀀-class
    /// queries used to route English. Such queries stay not-found either way
    /// (zh_index holds no such terms until a data rebuild), the fix classifies
    /// them correctly.
    /// </summary>
    public static bool IsChineseQuery(string text)
    {
        foreach (var rune in text.EnumerateRunes())
            if (rune.Value is 0x3007                           // ideographic number zero
                or (>= 0x3400 and <= 0x9FFF)                   // Ext A + URO
                or (>= 0xF900 and <= 0xFAFF)                   // compatibility ideographs
                or (>= 0x20000 and <= 0x2A6DF)                 // Ext B
                or (>= 0x2A700 and <= 0x2B73F)                 // Ext C
                or (>= 0x2B740 and <= 0x2B81F)                 // Ext D
                or (>= 0x2B820 and <= 0x2CEAF)                 // Ext E
                or (>= 0x2CEB0 and <= 0x2EE5F)                 // Ext F (2CEB0–2EBEF) + Ext I (2EBF0–2EE5F, 15.1+)
                or (>= 0x2F800 and <= 0x2FA1F)                 // compatibility ideographs supplement
                or (>= 0x30000 and <= 0x323AF)                 // Ext G + Ext H
                or (>= 0x323B0 and <= 0x3347F))                // Ext J (18.0)
                return true;
        return false;
    }

    public void Dispose()
    {
        History.Dispose();
        if (_database.IsValueCreated) _database.Value.Dispose();
    }
}
