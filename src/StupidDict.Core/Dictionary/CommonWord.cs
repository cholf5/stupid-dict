namespace StupidDict.Core.Dictionary;

/// <summary>
/// A headword with a corpus frequency rank (lower = more common); the fuzzy matcher's candidate set.
/// <see cref="Commonality"/> mirrors the SQL ordering rule (DictionaryStore.CommonalitySql:
/// <c>CASE WHEN freq &gt; 0 THEN freq WHEN bnc &gt; 0 THEN bnc ELSE 999999 END</c>) so the
/// in-memory paths rank words exactly like the SQLite ORDER BY paths do.
/// </summary>
public sealed record CommonWord(string WordLower, int Freq, int Bnc = 0)
{
    /// <summary>The effective rank used for ordering: freq first, bnc fallback, 999999 for neither.</summary>
    public int Commonality => Freq > 0 ? Freq : Bnc > 0 ? Bnc : 999999;
}
