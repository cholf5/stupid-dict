using StupidDict.Core.History;
using Xunit;

namespace StupidDict.Core.Tests;

public class RecentSearchStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), "stupiddict-tests", Guid.NewGuid().ToString("N"), "history.db");

    public void Dispose()
    {
        var directory = Path.GetDirectoryName(_path);
        if (directory is not null && Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public void OrdersNewestFirst()
    {
        using var store = new RecentSearchStore(_path);
        store.Add("cat");
        store.Add("dog");
        Assert.Equal(["dog", "cat"], store.GetRecent().Select(r => r.Query));
    }

    [Fact]
    public void DedupesCaseInsensitivelyAndKeepsLatestCasing()
    {
        using var store = new RecentSearchStore(_path);
        store.Add("cat");
        store.Add("CAT");
        store.Add("Dog");
        var recent = store.GetRecent();
        Assert.Equal(2, recent.Count);
        Assert.Equal("Dog", recent[0].Query);
        Assert.Equal("CAT", recent[1].Query);
    }

    [Fact]
    public void RepeatedQueryMovesToFront()
    {
        using var store = new RecentSearchStore(_path);
        store.Add("cat");
        store.Add("dog");
        store.Add("cat");
        Assert.Equal(["cat", "dog"], store.GetRecent().Select(r => r.Query));
    }

    [Fact]
    public void TrimsToThirtyEntries()
    {
        using var store = new RecentSearchStore(_path);
        for (var i = 0; i < 35; i++) store.Add($"word{i}");
        var recent = store.GetRecent();
        Assert.Equal(RecentSearchStore.MaxEntries, recent.Count);
        Assert.Equal("word34", recent[0].Query);
        Assert.DoesNotContain(recent, r => r.Query == "word4");
    }

    [Fact]
    public void ConcurrentAddAndGetRecentStayConsistent()
    {
        // Reads (UI RefreshRecents) and writes (thread-pool lookups) share one
        // SqliteConnection, which tolerates only serialized access — both sides
        // must take the same gate. A concurrent violation surfaces here as a
        // SqliteException escaping Parallel.For; the assertions then pin the
        // dedupe/trim/ordering semantics the serialization must preserve.
        using var store = new RecentSearchStore(_path);
        const int iterations = 400;

        Parallel.For(0, iterations, i =>
        {
            store.Add($"word{i % 40}");
            store.GetRecent();
        });

        var recent = store.GetRecent();
        Assert.True(recent.Count <= RecentSearchStore.MaxEntries);
        var normalized = recent.Select(r => r.Query.ToLowerInvariant()).ToList();
        Assert.Equal(normalized.Count, normalized.Distinct().Count());
        for (var i = 1; i < recent.Count; i++)
            Assert.True(recent[i - 1].QueriedAt >= recent[i].QueriedAt);
    }
}
