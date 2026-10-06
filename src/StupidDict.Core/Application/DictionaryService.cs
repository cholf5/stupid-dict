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
    private readonly Lazy<DictionaryDatabase> _database;
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
        _englishChinese = new EnglishChineseDictionary(store);
        _englishEnglish = new EnglishEnglishDictionary(store);
        _chineseEnglish = new ChineseEnglishDictionary(store);
        History = new RecentSearchStore(historyPath);
    }

    public Task<LookupResult> LookupAsync(string query) => Task.Run(() => Lookup(query));

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

        return new LookupResult
        {
            Query = query,
            IsChineseQuery = false,
            Tier = primary is not null ? tier : prefixCount > 0 ? MatchTier.Prefix : MatchTier.Fuzzy,
            Primary = primary,
            WordFormNote = wordFormNote,
            WordSuggestions = suggestions,
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

    internal static bool IsChineseQuery(string text) =>
        text.Any(ch => ch is (>= '\u3400' and <= '\u9FFF') or (>= '\uF900' and <= '\uFAFF'));

    public void Dispose()
    {
        History.Dispose();
        if (_database.IsValueCreated) _database.Value.Dispose();
    }
}
