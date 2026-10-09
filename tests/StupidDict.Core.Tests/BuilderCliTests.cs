using Microsoft.Data.Sqlite;
using Xunit;

namespace StupidDict.Core.Tests;

/// <summary>
/// 构建器 CLI 健壮性（kanban Q-003）：参数不足给用法而非裸堆栈（TC-001）、
/// 构建中断不留半成品库且不污染应用词典查找路径（TC-002）、--top 非整数
/// 显式报错而不是把值当词典路径。DictionaryBuilder.Run 进程内驱动完整构建，
/// 解析器单测钉参数语义，真实进程冒烟另行记录在卡面 Verification。
/// </summary>
public class BuilderCliTests
{
    // ---------- DataBuilder：参数解析 ----------

    [Fact]
    public void EmptyArgumentsAreRejectedWithUsage()
    {
        var ok = BuilderOptions.Parse([], out var error);

        Assert.Null(ok);
        Assert.NotNull(error);
    }

    [Fact]
    public void MissingSourceArgumentIsRejectedInsteadOfIndexOutOfRange()
    {
        // TC-001：只给 --wordnet 不给源文件——positional 为空，旧实现越过
        // 零参数首检后在 positional[0] 抛 IndexOutOfRangeException。
        var ok = BuilderOptions.Parse(["--wordnet", "/some/dir"], out var error);

        Assert.Null(ok);
        Assert.NotNull(error);
        Assert.Contains("源文件", error);
    }

    [Fact]
    public void ValidArgumentsResolveSourceOutputAndFlags()
    {
        var dir = Path.Combine(Path.GetTempPath(), "stupiddict-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var source = Path.Combine(dir, "stardict.db");
        File.WriteAllBytes(source, [0x01]);

        var ok = BuilderOptions.Parse([source, "--wordnet", dir], out var error);

        Assert.Null(error);
        Assert.NotNull(ok);
        Assert.Equal(Path.GetFullPath(source), ok.Source);
        Assert.Equal(Path.GetFullPath(dir), ok.WordNetDir);
        Assert.False(string.IsNullOrWhiteSpace(ok.Output));
        Assert.Null(ok.CmudictFile);
    }

    // ---------- DataBuilder：构建编排（临时文件 + 原子改名） ----------

    [Fact]
    public void SuccessfulBuildWritesDictionaryWithMetaAtOutput()
    {
        var dir = NewTempDir();
        var source = WriteEcdictSource(dir, ("cat", "猫"), ("dog", "狗"));
        var output = Path.Combine(dir, "out", "dictionary.db");

        var exit = DictionaryBuilder.Run([source, output]);

        Assert.Equal(0, exit);
        Assert.True(File.Exists(output));
        Assert.Equal("2", ReadMeta(output, "entries"));
        Assert.False(string.IsNullOrEmpty(ReadMeta(output, "built_at")));
    }

    [Fact]
    public void BuildFailureLeavesPreviousDictionaryIntactAndCleansTempFiles()
    {
        // TC-002：主事务提交后 WordNet 步骤中途异常（index.noun 第 3 列非数字，
        // WordNetThesaurus.ReadIndex 的 int.Parse 抛 FormatException）。旧实现在
        // 输出路径上删旧建新，半成品直接留在应用词典查找位置；现在旧库必须
        // 原样保留、无任何 .tmp 残留、退出码非 0。
        var dir = NewTempDir();
        var source = WriteEcdictSource(dir, ("cat", "猫"));
        var outputDir = Path.Combine(dir, "out");
        Directory.CreateDirectory(outputDir);
        var output = Path.Combine(outputDir, "dictionary.db");
        File.WriteAllBytes(output, "previous-dictionary"u8);

        var wordnetDir = Path.Combine(dir, "wordnet-bad");
        Directory.CreateDirectory(wordnetDir);
        File.WriteAllLines(Path.Combine(wordnetDir, "index.noun"), ["oxford n abc 0 1 0 1740"]);

        var exit = DictionaryBuilder.Run([source, output, "--wordnet", wordnetDir]);

        Assert.Equal(1, exit);
        Assert.Equal("previous-dictionary", File.ReadAllText(output));
        Assert.Empty(Directory.GetFiles(outputDir, "*.tmp"));
    }

    [Fact]
    public void InvalidSourceIsRejectedWithoutWritingOutput()
    {
        var dir = NewTempDir();
        var output = Path.Combine(dir, "out", "dictionary.db");

        var exit = DictionaryBuilder.Run([Path.Combine(dir, "missing.db"), output]);

        Assert.Equal(1, exit);
        Assert.False(File.Exists(output));
        Assert.False(Directory.Exists(Path.Combine(dir, "out")));
    }

    // ---------- AudioPackBuilder：--top 解析 ----------

    [Theory]
    [InlineData("--top", "abc")]
    [InlineData("--top", null)]
    public void TopRequiresAnIntegerValue(string flag, string? value)
    {
        // 旧实现 `case "--top" when ... int.TryParse(...)` 失败落 default，把
        // 值当词典路径，最后报「词典不存在： abc」；现在必须显式报 --top 需要整数。
        var args = value is null ? new[] { flag } : new[] { flag, value };
        var ok = AudioPackOptions.Parse(args, out var error);

        Assert.Null(ok);
        Assert.NotNull(error);
        Assert.Contains("--top", error);
    }

    [Fact]
    public void ValidTopIsAcceptedAndDoesNotBecomeTheDictionaryPath()
    {
        var ok = AudioPackOptions.Parse(["--top", "500"], out var error);

        Assert.Null(error);
        Assert.NotNull(ok);
        Assert.Equal(500, ok.Top);
        Assert.Null(ok.Dictionary);
    }

    // ---------- fixtures ----------

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "stupiddict-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Writes a minimal ECDICT stardict.db the builder reads as source.</summary>
    private static string WriteEcdictSource(string dir, params (string Word, string Translation)[] rows)
    {
        var path = Path.Combine(dir, "stardict.db");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        connection.Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE stardict (
                    word TEXT, phonetic TEXT, pos TEXT, translation TEXT, definition TEXT,
                    collins INTEGER, oxford INTEGER, tag TEXT, bnc INTEGER, frq INTEGER, exchange TEXT)
                """;
            command.ExecuteNonQuery();
        }

        foreach (var (word, translation) in rows)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO stardict VALUES ($word, '', 'n.', $translation, '', 0, 0, '', 0, 0, '')";
            insert.Parameters.AddWithValue("$word", word);
            insert.Parameters.AddWithValue("$translation", translation);
            insert.ExecuteNonQuery();
        }

        return path;
    }

    private static string? ReadMeta(string dictionaryPath, string key)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dictionaryPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM meta WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }
}
