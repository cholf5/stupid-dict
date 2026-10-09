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

    [Fact]
    public void ShortPrefixSuggestsCommonWordsRankedByFrequency()
    {
        var paths = TestDatabase.Create(
            new Row("cat", Freq: 100),
            new Row("Cab", Freq: 90),
            new Row("catalog", Freq: 80),
            new Row("catfish", Freq: 0));
        using var service = new DictionaryService(paths.DictionaryPath, paths.HistoryPath);

        var words = service.Suggest("ca");

        // 1-2 char prefixes draw from the in-memory common-word set: ranked
        // common-first (lower freq rank = more common), long-tail words
        // excluded, original casing preserved.
        Assert.Equal(["catalog", "Cab", "cat"], words);
    }

    [Fact]
    public void LongPrefixSuggestsLongTailWords()
    {
        var paths = TestDatabase.Create(
            new Row("zebra", Freq: 100),
            new Row("zebrawood", Freq: 0),
            new Row("zebu", Freq: 0));
        using var service = new DictionaryService(paths.DictionaryPath, paths.HistoryPath);

        var words = service.Suggest("zebra");

        // 3+ char prefixes hit the SQLite index: rare words are reachable.
        Assert.Equal(["zebra", "zebrawood"], words);
    }

    [Fact]
    public void ExactMatchLeadsLongPrefixSuggestions()
    {
        var paths = TestDatabase.Create(
            new Row("catch", Freq: 586),
            new Row("category", Freq: 1461),
            new Row("cat", Freq: 1775));
        using var service = new DictionaryService(paths.DictionaryPath, paths.HistoryPath);

        // "cat" is a headword: it leads despite "catch" being more common.
        Assert.Equal(["cat", "catch", "category"], service.Suggest("cat"));
    }

    [Fact]
    public void ExactMatchLeadsShortPrefixSuggestions()
    {
        var paths = TestDatabase.Create(
            new Row("down", Freq: 900),
            new Row("do", Freq: 1775));
        using var service = new DictionaryService(paths.DictionaryPath, paths.HistoryPath);

        Assert.Equal(["do", "down"], service.Suggest("do"));
    }

    [Fact]
    public void SuggestIsCaseAndWhitespaceInsensitive()
    {
        var paths = TestDatabase.Create(new Row("cat", Freq: 100));
        using var service = new DictionaryService(paths.DictionaryPath, paths.HistoryPath);

        Assert.Equal(service.Suggest("ca"), service.Suggest("  CA  "));
    }

    [Fact]
    public void SuggestSkipsEmptyAndChineseQueries()
    {
        var paths = TestDatabase.Create(new Row("cat", Freq: 100));
        using var service = new DictionaryService(paths.DictionaryPath, paths.HistoryPath);

        Assert.Empty(service.Suggest(""));
        Assert.Empty(service.Suggest("   "));
        Assert.Empty(service.Suggest("猫"));
    }

    [Theory]
    [InlineData("〇", true)]                    // U+3007 ideographic number zero（二〇二五）
    [InlineData("二〇二五", true)]
    [InlineData("abc〇", true)]                 // mixed: any ideograph routes Chinese
    [InlineData("猫", true)]                    // URO
    [InlineData("\U00020000", true)]            // Ext B first code point 𠀀
    [InlineData("\U00020BB7", true)]            // 𠮷（Ext B）
    [InlineData("\U0002A700", true)]            // Ext C first code point
    [InlineData("\U0002EC00", true)]            // Ext I tail segment（blocks.txt: 2EBF0–2EE5F；上界曾误写 2EBFF）
    [InlineData("\U0002F800", true)]            // compatibility ideographs supplement
    [InlineData("\U00030000", true)]            // Ext G first code point
    [InlineData("\U000323AF", true)]            // Ext H block end
    [InlineData("\U000323B0", true)]            // Ext J first code point（18.0 新增块 323B0–3347F）
    [InlineData("", false)]
    [InlineData("cat", false)]
    [InlineData("123", false)]
    [InlineData("、", false)]                   // U+3001 ideographic comma — punctuation, not an ideograph
    [InlineData("。", false)]                   // U+3002 ideographic full stop — same
    [InlineData("\U0001F600", false)]           // 😀 astral non-CJK must stay English
    [InlineData("\U0001D54F", false)]           // 𝕏 astral non-CJK
    [InlineData("\uD800", false)]               // lone surrogate decodes to U+FFFD, not an ideograph
    public void IsChineseQueryClassifiesIdeographs(string text, bool expected)
    {
        Assert.Equal(expected, DictionaryService.IsChineseQuery(text));
    }

    [Fact]
    public void IdeographicZeroQueryRoutesToChinesePath()
    {
        // 〇 (U+3007) used to fall through to the English path and render the
        // English not-found page; the query is a Chinese query by any definition.
        var paths = TestDatabase.Create(new Row("cat", Freq: 100));
        using var service = new DictionaryService(paths.DictionaryPath, paths.HistoryPath);

        var result = service.Lookup("二〇二五");

        Assert.True(result.IsChineseQuery);
        Assert.Null(result.Primary);
        Assert.Empty(result.ChineseMatches);
    }

    [Fact]
    public void MemoryAndSqlPathsRankBncFallbackIdentically()
    {
        // "aaba" has no freq but bnc=1000; "aabb" has freq=5000. The SQL CASE
        // (freq → bnc → 999999) ranks aaba first; the in-memory index used to
        // map freq==0 to int.MaxValue and rank it last. The 2-char prefix goes
        // through the in-memory index, the 3-char one through SQLite ORDER BY —
        // both must agree.
        var paths = TestDatabase.Create(
            new Row("aaba", Freq: 0, Bnc: 1000),
            new Row("aabb", Freq: 5000));
        using var service = new DictionaryService(paths.DictionaryPath, paths.HistoryPath);

        Assert.Equal(["aaba", "aabb"], service.Suggest("aa"));
        Assert.Equal(["aaba", "aabb"], service.Suggest("aab"));
    }
}
