using Microsoft.Data.Sqlite;
using StupidDict.Core.Dictionary;
using Xunit;

namespace StupidDict.Core.Tests;

/// <summary>
/// WordNetThesaurus.Build 的数据质量测试（kanban B-001）：用迷你 index.*/data.* 夹具
/// 钉住四类缺陷——短 index 行被预检丢弃、跨 data.* 文件同 offset 互相覆盖、
/// 卫星形容词（ss_type 's'）池不输出、专有名词词头未小写归一导致断链。
/// 夹具按 WordNet 文件语法手工构造，不带真实 3.0 文件末尾的两个尾随空格
/// （带空格的原始文件由真实数据全量重建覆盖）。
/// </summary>
public class WordNetThesaurusTests
{
    private sealed record SynRow(string Kind, string Pos, string Words);

    /// <summary>Writes the mini WordNet dict, seeds the word table, runs Build.</summary>
    private static string Build(string[] words, params (string File, string[] Lines)[] wordnetFiles)
    {
        var dir = Path.Combine(Path.GetTempPath(), "stupiddict-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (var (file, lines) in wordnetFiles)
            File.WriteAllLines(Path.Combine(dir, file), lines);
        var dictionaryPath = Path.Combine(dir, "dictionary.db");
        using (var db = DictionaryDatabase.Create(dictionaryPath))
        {
            foreach (var word in words)
                db.InsertWord(word, "", "", "", "", "", 0, 0, "");
            WordNetThesaurus.Build(db, dir);
        }
        return dictionaryPath;
    }

    /// <summary>The single syn_group row of a headword; fails if absent or duplicated.</summary>
    private static SynRow SoleSynRow(string dictionaryPath, string word)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dictionaryPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.kind, s.pos, s.words FROM syn_group s
            JOIN word w ON w.id = s.word_id WHERE w.word_lower = $word
            """;
        command.Parameters.AddWithValue("$word", word.ToLowerInvariant());
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read(), $"no syn_group row for '{word}'");
        var row = new SynRow(reader.GetString(0), reader.GetString(1), reader.GetString(2));
        Assert.False(reader.Read(), $"multiple syn_group rows for '{word}'");
        return row;
    }

    [Fact]
    public void ShortIndexLineIsKeptAndGarbageLineSkipped()
    {
        // 7-token index 行（p_cnt=0、synset_cnt=1）是合法最短行；3-token 行是截断残片。
        var path = Build(
            ["oxford", "university"],
            ("index.noun", ["oxford n 1 0 1 0 1740", "junk n 1"]),
            ("data.noun", ["00001740 01 n 02 oxford 0 university 0 0 | a city on the Thames"]));

        Assert.Equal(new SynRow("syn", "n.", "university"), SoleSynRow(path, "oxford"));
    }

    [Fact]
    public void SameOffsetInDifferentPosFilesDoesNotClobber()
    {
        // WordNet offset 是文件内字节偏移，四个 data.* 都从 00001740 起编——
        // 共用裸 offset 键会让后解析的文件覆盖前面的。
        var path = Build(
            ["animal", "beast", "warm", "hot"],
            ("index.noun", ["animal n 1 0 1 0 1740"]),
            ("index.adj", ["warm a 1 0 1 0 1740"]),
            ("data.noun", ["00001740 01 n 02 animal 0 beast 0 0 | a living organism"]),
            ("data.adj", ["00001740 00 a 02 warm 0 hot 0 0 | having heat"]));

        Assert.Equal(new SynRow("syn", "n.", "beast"), SoleSynRow(path, "animal"));
        Assert.Equal(new SynRow("syn", "adj.", "hot"), SoleSynRow(path, "warm"));
    }

    [Fact]
    public void SatelliteAdjectiveSynonymsAreWritten()
    {
        // 卫星形容词 ss_type='s'（index 侧 pos 仍记 'a'，与真实数据一致），
        // 经 & 指针牵出头形容词作近义词，输出为 adj. 行。
        var path = Build(
            ["muggy", "humid"],
            ("index.adj", ["muggy a 1 0 1 0 1740"]),
            ("data.adj",
            [
                "00001740 00 s 01 muggy 0 1 & 00001741 a 0000 | humid or wet",
                "00001741 00 a 01 humid 0 0 | containing moisture",
            ]));

        Assert.Equal(new SynRow("syn", "adj.", "humid"), SoleSynRow(path, "muggy"));
    }

    [Fact]
    public void ProperNounLemmasMatchLowercaseHeadwords()
    {
        // word_lower 全链路小写，index/data 里的专有名词原文必须归一后才可命中。
        var path = Build(
            ["rome", "empire"],
            ("index.noun", ["Rome n 1 0 1 0 1740"]),
            ("data.noun", ["00001740 01 n 02 Rome 0 empire 0 0 | the Roman Empire"]));

        Assert.Equal(new SynRow("syn", "n.", "empire"), SoleSynRow(path, "rome"));
    }
}
