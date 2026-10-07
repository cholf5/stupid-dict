using Microsoft.Data.Sqlite;
using StupidDict.Core.Application;
using StupidDict.Core.Dictionary;
using Xunit;
using static StupidDict.Core.Tests.TestDatabase;

namespace StupidDict.Core.Tests;

public class ThesaurusTests
{
    [Fact]
    public void LookupReturnsSynonymAndAntonymLines()
    {
        var paths = TestDatabase.Create(
            new Row("good", Freq: 100, Translation: "adj. 好的",
                Syn: ["n.:advantage, vantage", "adj.:great, nice"], Ant: ["adj.:bad, evil"]),
            new Row("advantage", Freq: 90, Translation: "n. 优势"),
            new Row("great", Freq: 80, Translation: "adj. 伟大的"),
            new Row("nice", Freq: 70, Translation: "adj. 美好的"),
            new Row("vantage", Freq: 60),
            new Row("bad", Freq: 50, Translation: "adj. 坏的"),
            new Row("evil", Freq: 40));
        using var service = new DictionaryService(paths.DictionaryPath, paths.HistoryPath);

        var result = service.Lookup("good");

        Assert.NotNull(result.Primary);
        Assert.Equal(2, result.Synonyms.Count);
        Assert.Equal("n.", result.Synonyms[0].Pos);
        Assert.Equal(["advantage", "vantage"], result.Synonyms[0].Words);
        Assert.Equal("adj.", result.Synonyms[1].Pos);
        Assert.Equal(["great", "nice"], result.Synonyms[1].Words);
        Assert.Single(result.Antonyms);
        Assert.Equal("adj.", result.Antonyms[0].Pos);
        Assert.Equal(["bad", "evil"], result.Antonyms[0].Words);
        // Corpus-rank order across the whole synonym+antonym pool:
        // evil(40) bad(50) vantage(60) nice(70) great(80) advantage(90).
        Assert.Equal(["evil", "bad", "vantage", "nice", "great", "advantage"],
            result.RelatedWords.Select(w => w.Word));
    }

    [Fact]
    public void RelatedWordsRankCommonWordsFirstWithGlosses()
    {
        var paths = TestDatabase.Create(
            new Row("good", Freq: 100, Syn: ["n.:rare, common"]),
            new Row("rare", Freq: 900, Translation: "adj. 稀有的, 罕见的；珍贵的"),
            new Row("common", Freq: 5, Translation: "adj. 普通的，常见的\nn. 平民"));
        using var service = new DictionaryService(paths.DictionaryPath, paths.HistoryPath);

        var result = service.Lookup("good");

        // Lower corpus rank = more common, so "common" leads. The gloss is the
        // first sense line with the POS prefix stripped, capped at two chunks
        // (ECDICT separates senses with both comma widths).
        Assert.Equal(
        [
            new RelatedWord("common", "普通的，常见的"),
            new RelatedWord("rare", "稀有的，罕见的"),
        ], result.RelatedWords);
    }

    [Fact]
    public void RelatedWordsSkipWordsMissingFromDictionary()
    {
        var paths = TestDatabase.Create(
            new Row("good", Freq: 100, Syn: ["n.:real, ghostword"]),
            new Row("real", Freq: 50, Translation: "adj. 真的"));
        using var service = new DictionaryService(paths.DictionaryPath, paths.HistoryPath);

        var result = service.Lookup("good");

        // "ghostword" has no headword row, so it can't be glossed or linked.
        Assert.Equal(["real"], result.RelatedWords.Select(w => w.Word));
    }

    [Fact]
    public void WordFormLookupCarriesThesaurusOfBaseWord()
    {
        var paths = TestDatabase.Create(
            new Row("cat", Freq: 100, Exchange: "s:cats", Syn: ["n.:tiger"]),
            new Row("tiger", Freq: 90));
        using var service = new DictionaryService(paths.DictionaryPath, paths.HistoryPath);

        var result = service.Lookup("cats");

        Assert.Equal("cat", result.Primary!.Word);
        Assert.Equal(["tiger"], result.Synonyms.Single().Words);
    }

    [Fact]
    public void MissesAndChineseLookupsHaveNoThesaurus()
    {
        var paths = TestDatabase.Create(
            new Row("cat", Freq: 100, Translation: "n. 猫", Syn: ["n.:tiger"]),
            new Row("tiger", Freq: 90));
        using var service = new DictionaryService(paths.DictionaryPath, paths.HistoryPath);

        var miss = service.Lookup("cath");
        Assert.Empty(miss.Synonyms);
        Assert.Empty(miss.Antonyms);
        Assert.Empty(miss.RelatedWords);

        var chinese = service.Lookup("猫");
        Assert.Empty(chinese.Synonyms);
    }

    [Fact]
    public void DictionaryWithoutThesaurusTableKeepsWorking()
    {
        var paths = TestDatabase.Create(new Row("cat", Freq: 100));
        using (var connection = new SqliteConnection($"Data Source={paths.DictionaryPath}"))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DROP TABLE syn_group";
            cmd.ExecuteNonQuery();
        }
        using var service = new DictionaryService(paths.DictionaryPath, paths.HistoryPath);

        var result = service.Lookup("cat");

        Assert.Equal("cat", result.Primary!.Word);
        Assert.Empty(result.Synonyms);
        Assert.Empty(result.Antonyms);
        Assert.Empty(result.RelatedWords);
    }
}
