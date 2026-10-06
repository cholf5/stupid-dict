namespace StupidDict.Core.Dictionary;

public enum MatchTier
{
    /// <summary>The query equals a headword as typed.</summary>
    Exact,

    /// <summary>The query equals a headword after trim + lowercase.</summary>
    NormalizedExact,

    /// <summary>The query is an inflected form ("cats") of a headword ("cat").</summary>
    WordForm,

    /// <summary>A Chinese term with exact matches.</summary>
    Chinese,

    /// <summary>No exact hit; these are prefix matches.</summary>
    Prefix,

    /// <summary>No exact hit; these are fuzzy ("did you mean") matches.</summary>
    Fuzzy,

    NotFound,
}

/// <summary>One Chinese-term hit: the term plus the English headword whose gloss contains it.</summary>
public sealed record ChineseMatch(string Term, DictionaryEntry Entry);

public sealed record LookupResult
{
    public required string Query { get; init; }
    public required bool IsChineseQuery { get; init; }
    public MatchTier Tier { get; init; } = MatchTier.NotFound;

    /// <summary>The entry to render as the main result, if any.</summary>
    public DictionaryEntry? Primary { get; init; }

    /// <summary>Base word when the query was resolved through a word form ("cats" → "cat").</summary>
    public string? WordFormNote { get; init; }

    /// <summary>English words offered when there is no exact hit (prefix, then fuzzy).</summary>
    public IReadOnlyList<DictionaryEntry> WordSuggestions { get; init; } = [];

    /// <summary>Chinese-term hits for a Chinese query, most relevant first.</summary>
    public IReadOnlyList<ChineseMatch> ChineseMatches { get; init; } = [];

    public static LookupResult NotFound(string query, bool isChinese) => new()
    {
        Query = query,
        IsChineseQuery = isChinese,
        Tier = MatchTier.NotFound,
    };
}
