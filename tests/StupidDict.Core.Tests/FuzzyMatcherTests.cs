using StupidDict.Core.Dictionary;
using Xunit;

namespace StupidDict.Core.Tests;

public class FuzzyMatcherTests
{
    [Fact]
    public void EqualDistanceCandidatesRankByBncFallbackLikeSqlCase()
    {
        // Both candidates are one edit from "cat", so ordering falls to the
        // commonality rule. "caz" carries only bnc=500: the SQL CASE (freq →
        // bnc → 999999) puts it ahead of "cab" (freq=3000). The old freq-only
        // EffectiveRank mapped "caz" to int.MaxValue and ordered it last.
        IReadOnlyList<CommonWord> candidates =
        [
            new("cab", 3000),
            new("caz", 0, 500),
        ];

        Assert.Equal(["caz", "cab"], FuzzyMatcher.Find("cat", candidates, limit: 10));
    }

    [Fact]
    public void DistanceStillDominatesCommonality()
    {
        // A nearer word wins regardless of rank; the CASE rule only breaks
        // distance ties, then ordinal word order breaks rank ties.
        IReadOnlyList<CommonWord> candidates =
        [
            new("cats", 5000), // distance 1
            new("cax", 1),     // distance 1 — highest rank → first
            new("caa", 5000),  // distance 1 — ties "cats" on rank, ordinal decides
        ];

        Assert.Equal(["cax", "caa", "cats"], FuzzyMatcher.Find("cat", candidates, limit: 10));
    }
}
