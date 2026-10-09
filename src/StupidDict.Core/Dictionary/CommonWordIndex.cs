namespace StupidDict.Core.Dictionary;

/// <summary>
/// The common-word set (corpus-ranked headwords) loaded once into memory.
/// Serves short-prefix live completion and the fuzzy matcher's candidate set:
/// both only need these ~57k words, not the full 3.4M-headword table.
/// </summary>
public sealed class CommonWordIndex
{
    // ExecutionAndPublication: the full-table scan runs exactly once even when
    // WarmupAsync and the first keystroke race — the loser waits out the winner
    // instead of repeating the ~1-2 s scan (a plain ??= let both threads run it).
    private readonly Lazy<IReadOnlyList<CommonWord>> _ranked;

    public CommonWordIndex(Func<List<CommonWord>> load) =>
        _ranked = new Lazy<IReadOnlyList<CommonWord>>(() => Ranked(load()));

    /// <summary>All common words, most common first. First access runs the full-table scan (~1-2 s).</summary>
    public IReadOnlyList<CommonWord> Words => _ranked.Value;

    /// <summary>Common words starting with <paramref name="lower"/>, most common first.</summary>
    public List<string> FindPrefix(string lower, int limit)
    {
        List<string> results = new(limit);
        foreach (var candidate in Words)
        {
            if (!candidate.WordLower.StartsWith(lower, StringComparison.Ordinal)) continue;
            results.Add(candidate.WordLower);
            if (results.Count == limit) break;
        }
        return results;
    }

    // Same rule as the SQL ORDER BY (DictionaryStore.CommonalitySql), via
    // CommonWord.Commonality: freq first, bnc fallback. Ordinal word compare
    // breaks ties like the SQL path's word_lower ordering does.
    private static List<CommonWord> Ranked(List<CommonWord> words)
    {
        words.Sort((a, b) =>
        {
            var cmp = a.Commonality.CompareTo(b.Commonality);
            return cmp != 0 ? cmp : string.CompareOrdinal(a.WordLower, b.WordLower);
        });
        return words;
    }
}
