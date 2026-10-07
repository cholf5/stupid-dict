using StupidDict.Core.Application;
using StupidDict.Core.Dictionary;
using Xunit;

namespace StupidDict.Core.Tests;

public class LookupNavigatorTests
{
    [Fact]
    public void BackReturnsEntriesInReverseOrderAndForwardRedoes()
    {
        var nav = new LookupNavigator();
        nav.Push(Result("cat"));
        nav.Push(Result("catch"));
        nav.Push(Result("猫"));

        Assert.Equal("catch", nav.GoBack()!.Query);
        Assert.Equal("cat", nav.GoBack()!.Query);
        Assert.Equal("catch", nav.GoForward()!.Query);
        Assert.Equal("猫", nav.GoForward()!.Query);
        Assert.Null(nav.GoForward());
    }

    [Fact]
    public void CannotGoBackOrForwardBeyondEnds()
    {
        var nav = new LookupNavigator();
        Assert.Null(nav.GoBack());
        Assert.Null(nav.GoForward());

        nav.Push(Result("cat"));
        nav.Push(Result("catch"));
        Assert.True(nav.CanGoBack);
        Assert.False(nav.CanGoForward);
        nav.GoBack();
        Assert.True(nav.CanGoForward);
        Assert.False(nav.CanGoBack);
        nav.GoForward();
        Assert.False(nav.CanGoForward);
        Assert.True(nav.CanGoBack);
    }

    [Fact]
    public void NewQueryTruncatesForwardBranch()
    {
        var nav = new LookupNavigator();
        nav.Push(Result("cat"));
        nav.Push(Result("catch"));
        nav.GoBack();

        nav.Push(Result("dog"));

        Assert.False(nav.CanGoForward);
        Assert.Equal("cat", nav.GoBack()!.Query);
        Assert.False(nav.CanGoBack);
    }

    [Fact]
    public void RePushingCurrentPageDoesNotGrowHistory()
    {
        var nav = new LookupNavigator();
        nav.Push(Result("cat"));
        nav.Push(Result("cat"));
        nav.Push(Result("CAT"));

        Assert.False(nav.CanGoBack);
        Assert.False(nav.CanGoForward);
    }

    [Fact]
    public void CapsAtMaxEntries()
    {
        var nav = new LookupNavigator();
        for (var i = 0; i < 150; i++) nav.Push(Result($"word{i}"));

        var current = nav.GoBack();
        Assert.Equal("word148", current!.Query);
        var oldest = current;
        while (nav.CanGoBack) oldest = nav.GoBack();
        Assert.Equal("word50", oldest!.Query);
    }

    private static LookupResult Result(string query) => LookupResult.NotFound(query, isChinese: false);
}
