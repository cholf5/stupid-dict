using StupidDict.Core.Dictionary;
using Xunit;

namespace StupidDict.Core.Tests;

public class CommonWordIndexTests
{
    [Fact]
    public void WordsRankByFreqThenBncLikeSqlCase()
    {
        // DictionaryStore's ORDER BY is CASE WHEN freq > 0 THEN freq WHEN bnc > 0
        // THEN bnc ELSE 999999 END (CommonalitySql). The in-memory ranking must
        // apply the same rule — a bnc-ranked word used to sink behind every
        // freq>0 word (EffectiveRank mapped freq==0 to int.MaxValue).
        var index = new CommonWordIndex(() => new List<CommonWord>
        {
            new("zzz", 5000),      // freq only → 5000
            new("aaa", 0, 1000),   // bnc fallback → 1000, must lead
            new("mmm", 0, 0),      // neither → 999999 tail (injectable loader only;
        });                        // the real loader's WHERE clause excludes these)

        Assert.Equal(["aaa", "zzz", "mmm"], index.Words.Select(w => w.WordLower).ToList());
    }

    [Fact]
    public void ConcurrentFirstAccessLoadsExactlyOnce()
    {
        // WarmupAsync and the first keystroke race on Words. Lazy
        // (ExecutionAndPublication) makes the ~57k-row scan run exactly once;
        // the injected loader doubles as the counting seam.
        var loads = 0;
        var index = new CommonWordIndex(() =>
        {
            Interlocked.Increment(ref loads);
            // Millisecond-level stall: gives the test built-in discriminating
            // power against a ??= regression (torn double-load) without a
            // temporary patch during red-checks. Correctness does not depend
            // on timing — with the Lazy this passes whatever the interleaving.
            Thread.Sleep(1);
            return new List<CommonWord> { new("a", 1) };
        });

        const int readers = 16;
        using var start = new ManualResetEventSlim(false);
        var threads = new Thread[readers];
        IReadOnlyList<CommonWord>? first = null;
        var errors = new List<Exception>();
        for (var i = 0; i < readers; i++)
        {
            threads[i] = new Thread(() =>
            {
                start.Wait();
                try
                {
                    var words = index.Words;
                    // CompareExchange returns the list the first reader saw;
                    // any reader getting a different instance is a torn load.
                    if (Interlocked.CompareExchange(ref first, words, null) is { } winner
                        && !ReferenceEquals(winner, words))
                        lock (errors) errors.Add(new InvalidOperationException("readers saw different lists"));
                }
                catch (Exception ex) { lock (errors) errors.Add(ex); }
            });
            threads[i].Start();
        }

        start.Set();
        foreach (var thread in threads) thread.Join();

        Assert.Empty(errors);
        Assert.Equal(1, loads);
    }
}
