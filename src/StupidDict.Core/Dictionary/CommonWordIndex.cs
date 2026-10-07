namespace StupidDict.Core.Dictionary;

/// <summary>
/// The common-word set (corpus-ranked headwords) loaded once into memory.
/// Serves short-prefix live completion and the fuzzy matcher's candidate set:
/// both only need these ~57k words, not the full 3.4M-headword table.
/// </summary>
public sealed class CommonWordIndex
{
    private readonly Func<List<CommonWord>> _load;
    private List<CommonWord>? _ranked;

    public CommonWordIndex(Func<List<CommonWord>> load) => _load = load;

    /// <summary>All common words, most common first. First access runs the full-table scan (~1-2 s).</summary>
    public IReadOnlyList<CommonWord> Words => _ranked ??= Ranked(_load());

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

    private static List<CommonWord> Ranked(List<CommonWord> words)
    {
        words.Sort((a, b) =>
        {
            var cmp = EffectiveRank(a.Freq).CompareTo(EffectiveRank(b.Freq));
            return cmp != 0 ? cmp : string.CompareOrdinal(a.WordLower, b.WordLower);
        });
        return words;
    }

    private static int EffectiveRank(int rank) => rank > 0 ? rank : int.MaxValue;
}
