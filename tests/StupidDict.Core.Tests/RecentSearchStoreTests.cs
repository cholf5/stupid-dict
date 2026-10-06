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
}
