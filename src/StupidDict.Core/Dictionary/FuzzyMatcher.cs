namespace StupidDict.Core.Dictionary;

/// <summary>Bounded Damerau-Levenshtein (optimal string alignment) fuzzy matching over common words.</summary>
public static class FuzzyMatcher
{
    /// <summary>
    /// Returns the candidate words within a small edit distance of the query,
    /// nearest and most common first.
    /// </summary>
    public static List<string> Find(string query, IReadOnlyList<CommonWord> candidates, int limit)
    {
        var cutoff = MaxDistance(query.Length);
        var results = new List<(string Word, int Dist, int Commonality)>();
        foreach (var candidate in candidates)
        {
            if (candidate.WordLower == query) continue;
            if (Math.Abs(candidate.WordLower.Length - query.Length) > cutoff) continue;
            var distance = BoundedDistance(query, candidate.WordLower, cutoff);
            if (distance >= 0)
                results.Add((candidate.WordLower, distance, candidate.Commonality));
        }
        results.Sort((a, b) =>
        {
            var cmp = a.Dist.CompareTo(b.Dist);
            if (cmp != 0) return cmp;
            // CommonWord.Commonality = the SQL CASE rule (freq first, bnc fallback):
            // ties must order like the SQLite paths do.
            cmp = a.Commonality.CompareTo(b.Commonality);
            return cmp != 0 ? cmp : string.CompareOrdinal(a.Word, b.Word);
        });
        return results.Take(limit).Select(r => r.Word).ToList();
    }

    private static int MaxDistance(int length) => length switch
    {
        <= 4 => 1,
        <= 8 => 2,
        _ => 3,
    };

    /// <summary>Edit distance with transpositions, abandoning early when the cutoff cannot be met. Returns -1 if over cutoff.</summary>
    private static int BoundedDistance(string a, string b, int cutoff)
    {
        int n = a.Length, m = b.Length;
        int[] prevPrev = new int[m + 1];
        int[] prev = new int[m + 1];
        int[] curr = new int[m + 1];
        for (var j = 0; j <= m; j++) prev[j] = j;

        for (var i = 1; i <= n; i++)
        {
            curr[0] = i;
            var rowMin = i;
            for (var j = 1; j <= m; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                var value = Math.Min(curr[j - 1] + 1, Math.Min(prev[j] + 1, prev[j - 1] + cost));
                if (i > 1 && j > 1 && a[i - 2] == b[j - 1] && a[i - 1] == b[j - 2])
                    value = Math.Min(value, prevPrev[j - 2] + 1);
                curr[j] = value;
                if (value < rowMin) rowMin = value;
            }
            if (rowMin > cutoff) return -1;
            (prevPrev, prev, curr) = (prev, curr, prevPrev);
        }
        return prev[m] <= cutoff ? prev[m] : -1;
    }
}
