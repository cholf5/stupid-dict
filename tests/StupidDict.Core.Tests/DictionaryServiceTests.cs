using StupidDict.Core.Application;
using StupidDict.Core.Dictionary;
using Xunit;
using static StupidDict.Core.Tests.TestDatabase;

namespace StupidDict.Core.Tests;

public class DictionaryServiceTests
{
    [Fact]
    public void ExactBeatPrefix()
    {
        var paths = TestDatabase.Create(
            new Row("cat", Freq: 100, Phonetic: "kæt", Translation: "n. 猫", Definition: "a small animal"),
            new Row("catch", Freq: 90),
            new Row("catalog", Freq: 80));
        using var service = new DictionaryService(paths.DictionaryPath, paths.HistoryPath);

        var result = service.Lookup("cat");

        Assert.Equal(MatchTier.Exact, result.Tier);
        Assert.Equal("cat", result.Primary!.Word);
        Assert.Empty(result.WordSuggestions);
    }

    [Fact]
    public void LookupIsCaseAndWhitespaceInsensitive()
    {
        var paths = TestDatabase.Create(new Row("cat", Freq: 100));
        using var service = new DictionaryService(paths.DictionaryPath, paths.HistoryPath);

        var result = service.Lookup("  CAT  ");

        Assert.Equal(MatchTier.NormalizedExact, result.Tier);
        Assert.Equal("cat", result.Primary!.Word);
        Assert.Equal("CAT", service.History.GetRecent().Single().Query);
    }

    [Fact]
    public void WordFormResolvesToBaseWord()
    {
        var paths = TestDatabase.Create(new Row("cat", Freq: 100, Exchange: "s:cats"));
        using var service = new DictionaryService(paths.DictionaryPath, paths.HistoryPath);

        var result = service.Lookup("Cats");

        Assert.Equal(MatchTier.WordForm, result.Tier);
        Assert.Equal("cat", result.Primary!.Word);
        Assert.Equal("cat", result.WordFormNote);
    }

    [Fact]
    public void ChineseQueryFindsEnglishWord()
    {
        var paths = TestDatabase.Create(
            new Row("cat", Freq: 50, Phonetic: "kæt", Translation: "n. 猫, 恶妇\nvi. 呕吐"),
            new Row("wildcat", Freq: 100, Translation: "n. 猫, 野猫"));
        using var service = new DictionaryService(paths.DictionaryPath, paths.HistoryPath);

        var result = service.Lookup("猫");

        Assert.Equal(MatchTier.Chinese, result.Tier);
        Assert.Equal("cat", result.Primary!.Word);
        Assert.Contains(result.ChineseMatches, m => m.Entry.Word == "wildcat");
    }

    [Fact]
    public void ChinesePrefixOffersRelatedTerms()
    {
        var paths = TestDatabase.Create(new Row("tiger", Freq: 70, Translation: "n. 老虎, 猫科动物"));
        using var service = new DictionaryService(paths.DictionaryPath, paths.HistoryPath);

        var result = service.Lookup("猫科");

        Assert.Equal(MatchTier.Prefix, result.Tier);
        Assert.Null(result.Primary);
        Assert.Contains(result.ChineseMatches, m => m.Term == "猫科动物" && m.Entry.Word == "tiger");
    }

    [Fact]
    public void MissOffersSuggestionsInsteadOfAutoPicking()
    {
        var paths = TestDatabase.Create(
            new Row("cat", Freq: 100),
            new Row("catch", Freq: 90));
        using var service = new DictionaryService(paths.DictionaryPath, paths.HistoryPath);

        var result = service.Lookup("cath");

        Assert.Null(result.Primary);
        Assert.Equal(MatchTier.Fuzzy, result.Tier);
        Assert.Contains(result.WordSuggestions, w => w.Word == "cat");
        Assert.Contains(result.WordSuggestions, w => w.Word == "catch");
        Assert.Empty(service.History.GetRecent());
    }

    [Fact]
    public void PrefixSuggestionsOrderedByFrequency()
    {
        var paths = TestDatabase.Create(
            new Row("cata", Freq: 50),
            new Row("catb", Freq: 5),
            new Row("catz", Freq: 30));
        using var service = new DictionaryService(paths.DictionaryPath, paths.HistoryPath);

        var result = service.Lookup("cat");

        Assert.Null(result.Primary);
        Assert.Equal(MatchTier.Prefix, result.Tier);
        Assert.Equal("catb", result.WordSuggestions[0].Word);
        Assert.Equal("catz", result.WordSuggestions[1].Word);
        Assert.Equal("cata", result.WordSuggestions[2].Word);
    }

    [Fact]
    public void HistoryOnlyRecordsHits()
    {
        var paths = TestDatabase.Create(new Row("cat", Freq: 100));
        using var service = new DictionaryService(paths.DictionaryPath, paths.HistoryPath);

        service.Lookup("cath");
        Assert.Empty(service.History.GetRecent());

        service.Lookup("cat");
        var recent = service.History.GetRecent();
        Assert.Single(recent);
        Assert.Equal("cat", recent[0].Query);
    }
}
