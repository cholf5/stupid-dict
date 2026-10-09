using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.Data.Sqlite;
using StupidDict.Core.Dictionary;

return DictionaryBuilder.Run(args);

/// <summary>
/// DataBuilder 构建编排（kanban Q-003）：参数解析 → 词典构建 → 产物落盘。
/// 从顶语句抽成静态入口是为了能在 Core.Tests 进程内驱动——顶语句形式
/// 无法注入「构建中途异常」验证不留半成品库。构建写入同目录临时文件、
/// 全部成功后原子改名为正式输出；中途失败只清理临时文件，正式路径上
/// 要么是完整新库、要么原样保留旧库。
/// </summary>
internal static class DictionaryBuilder
{
    public static int Run(string[] args)
    {
        var options = BuilderOptions.Parse(args, out var parseError);
        if (options is null)
        {
            Console.Error.WriteLine(parseError);
            Console.Error.WriteLine();
            PrintUsage();
            return 1;
        }

        var usPhonetics = options.CmudictFile is { } file ? CmuPhonetics.Load(file) : null;
        var source = options.Source;
        var output = options.Output;

        var commonTags = new HashSet<string> { "zk", "gk", "cet4", "cet6", "ky", "toefl", "ielts", "gre" };
        var zhTermRegex = new Regex(@"[\u3400-\u9FFF]+", RegexOptions.Compiled);
        var started = Stopwatch.StartNew();
        long entries = 0, skipped = 0, common = 0, zhTerms = 0, forms = 0, usPhoneticCount = 0;

        // 临时文件与正式输出同目录（保证 File.Move 原子改名走同卷 rename）。
        // 旧实现直接在输出路径上删旧建新：journal_mode=OFF 下事务中途异常
        // 既回滚不了也留不下可自检的库，而默认输出（ApplicationData/StupidDict/
        // dictionary.db）正是应用启动自动查找的词典位置——半成品库会被应用
        // 当词典加载且无 built_at 无法自检。临时文件名带 GUID 与 .tmp 后缀，
        // 应用按精确文件名查找，永不误认。
        var tempOutput = Path.Combine(
            Path.GetDirectoryName(output)!,
            $"{Path.GetFileName(output)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var db = DictionaryDatabase.Create(tempOutput))
            {
                using var transaction = db.BeginTransaction();
                foreach (var row in ReadRows(source))
                {
                    var word = row.Word.Trim();
                    if (word.Length == 0) { skipped++; continue; }

                    var isCommon = row.Frq > 0 || row.Bnc > 0 || row.Collins > 0 || row.Oxford > 0
                        || row.Tag.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(commonTags.Contains);

                    var usPhonetic = usPhonetics is { } map && map.TryGetValue(word.ToLowerInvariant(), out var ipa) ? ipa : "";
                    var wordId = db.InsertWord(word, row.Phonetic, usPhonetic, row.Pos, row.Translation, row.Definition, row.Frq, row.Bnc, row.Tag);
                    if (wordId < 0) { skipped++; continue; }
                    if (usPhonetic.Length > 0) usPhoneticCount++;
                    entries++;

                    if (isCommon)
                    {
                        common++;
                        if (row.Translation.Length > 0)
                        {
                            HashSet<string> seen = [];
                            foreach (Match match in zhTermRegex.Matches(row.Translation))
                            {
                                if (match.Value.Length > 12 || !seen.Add(match.Value)) continue;
                                db.InsertZhTerm(match.Value, wordId);
                                zhTerms++;
                            }
                        }
                    }

                    if (row.Exchange.Length > 0)
                    {
                        foreach (var pair in row.Exchange.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            if (pair.Length < 3 || pair[1] != ':') continue;
                            if (pair[0] is not ('p' or 'd' or 'i' or '3' or 'r' or 't' or 's')) continue;
                            var form = pair[2..].ToLowerInvariant();
                            if (form.Length == 0) continue;
                            db.InsertWordForm(form, wordId);
                            forms++;
                        }
                    }

                    if (entries % 200_000 == 0)
                        Console.WriteLine($"... {entries:N0} 词条，用时 {started.Elapsed.TotalSeconds:F0}s");
                }

                db.SetMeta("source_file", Path.GetFileName(source));
                db.SetMeta("built_at", DateTime.UtcNow.ToString("o"));
                db.SetMeta("entries", entries.ToString());
                db.SetMeta("us_phonetics", usPhoneticCount.ToString());
                if (options.CmudictFile is { } cmuFile) db.SetMeta("cmudict_file", Path.GetFileName(cmuFile));
                db.CommitTransaction();

                if (options.WordNetDir is { } dir)
                {
                    Console.WriteLine("正在生成近义词/反义词（WordNet）...");
                    db.BeginTransaction();
                    var (thesaurusWords, thesaurusLines) = WordNetThesaurus.Build(db, dir);
                    db.SetMeta("wordnet_dir", dir);
                    db.SetMeta("thesaurus_words", thesaurusWords.ToString());
                    db.SetMeta("thesaurus_lines", thesaurusLines.ToString());
                    db.CommitTransaction();
                    Console.WriteLine($"词库扩展: {thesaurusWords:N0} 词头，{thesaurusLines:N0} 行");
                }
                else
                {
                    Console.Error.WriteLine("提示: 未提供 --wordnet，近义词/反义词/联想词板块将为空。");
                }
            }

            // 全部成功才替换正式输出：替换窗口从整个构建期收窄为一次 rename。
            // 输出被占用（应用开着旧库）等改名失败在这里抛出 → 走下方清理，
            // 旧库原样保留，构建决不会损坏可用产物。
            File.Move(tempOutput, output, overwrite: true);
        }
        catch (Exception ex)
        {
            // 清理尽力而为：删不掉的残留是带 .tmp 后缀的一次性文件，不在应用查找路径上。
            try { File.Delete(tempOutput); }
            catch
            {
                // ignore
            }

            Console.Error.WriteLine($"构建失败: {ex.Message}");
            return 1;
        }

        var sizeMb = new FileInfo(output).Length / 1024.0 / 1024.0;
        Console.WriteLine($"完成: {entries:N0} 词条（跳过 {skipped:N0}），常用词 {common:N0}，中文索引 {zhTerms:N0}，词形映射 {forms:N0}，美音音标 {usPhoneticCount:N0}");
        Console.WriteLine($"输出: {output}（{sizeMb:F0} MB，用时 {started.Elapsed.TotalSeconds:F0}s）");
        return 0;

        static void PrintUsage() => Console.Error.WriteLine("""
            用法: dotnet run --project src/StupidDict.DataBuilder -- <ECDICT 源文件> [输出 dictionary.db] [--wordnet <WordNet dict 目录>] [--cmudict <cmudict 文件>]

            源文件支持:
              * ECDICT 官方发布包中的 stardict.db（推荐，收词约 340 万）
              * ECDICT 的 ecdict.csv / ecdict.mini.csv

            --wordnet 指向 WordNet 的 dict 目录（含 index.noun / data.noun 等），
            提供后生成近义词/反义词/联想词；省略则这些板块为空。

            --cmudict 指向 CMU 美式发音词典（cmudict-0.7b，约 13.4 万词），提供后
            生成美音音标；省略则美音音标为空，应用照常工作。

            输出默认写入用户词典目录（应用启动时自动查找同一位置）。
            """);
    }

    private static IEnumerable<EcdictRow> ReadRows(string path) =>
        path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ? ReadCsv(path) : ReadSqlite(path);

    private static IEnumerable<EcdictRow> ReadSqlite(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT word, IFNULL(phonetic,''), IFNULL(pos,''), IFNULL(translation,''), IFNULL(definition,''),
                   IFNULL(collins,0), IFNULL(oxford,0), IFNULL(tag,''), IFNULL(bnc,0), IFNULL(frq,0), IFNULL(exchange,'')
            FROM stardict
            ORDER BY rowid
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            yield return new EcdictRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt32(5),
                reader.GetInt32(6),
                reader.GetString(7),
                reader.GetInt32(8),
                reader.GetInt32(9),
                reader.GetString(10));
        }
    }

    private static IEnumerable<EcdictRow> ReadCsv(string path)
    {
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            MissingFieldFound = null,
            BadDataFound = null,
            HeaderValidated = null,
        };
        using var reader = new StreamReader(path);
        using var csv = new CsvReader(reader, config);
        foreach (var row in csv.GetRecords<EcdictCsvRow>())
        {
            yield return new EcdictRow(
                row.word ?? "",
                row.phonetic ?? "",
                row.pos ?? "",
                row.translation ?? "",
                row.definition ?? "",
                ParseInt(row.collins),
                ParseInt(row.oxford),
                row.tag ?? "",
                ParseInt(row.bnc),
                ParseInt(row.frq),
                row.exchange ?? "");
        }
    }

    private static int ParseInt(string? value) => int.TryParse(value, out var parsed) ? parsed : 0;
}

/// <summary>DataBuilder 命令行解析结果（路径均已转全路径）。</summary>
internal sealed record BuilderOptions(string Source, string Output, string? WordNetDir, string? CmudictFile)
{
    /// <summary>解析命令行；失败返回 null 并给出 error（调用方负责补打印用法）。</summary>
    public static BuilderOptions? Parse(IReadOnlyList<string> args, out string? error)
    {
        error = null;
        string? wordnetDir = null, cmudictFile = null;
        List<string> positional = [];
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == "--wordnet" && i + 1 < args.Count) wordnetDir = Path.GetFullPath(args[++i]);
            else if (args[i] == "--cmudict" && i + 1 < args.Count) cmudictFile = Path.GetFullPath(args[++i]);
            else positional.Add(args[i]);
        }

        // 只挡零参数不够：`--wordnet /dir`（忘传源文件）这类调用 args.Length≥1
        // 过得了旧首检，positional 却是空的，positional[0] 直接越界抛裸堆栈
        // （kanban Q-003-1）。源文件是必填项，缺失时连同用法一起给出。
        if (positional.Count == 0)
        {
            error = "缺少 ECDICT 源文件参数。";
            return null;
        }

        if (wordnetDir is { } wn && !Directory.Exists(wn))
        {
            error = $"WordNet 目录不存在: {wn}";
            return null;
        }

        if (cmudictFile is { } cmu && !File.Exists(cmu))
        {
            error = $"cmudict 文件不存在: {cmu}";
            return null;
        }

        var source = Path.GetFullPath(positional[0]);
        var output = positional.Count > 1 ? Path.GetFullPath(positional[1]) : DefaultOutputPath();
        if (!File.Exists(source))
        {
            error = $"源文件不存在: {source}";
            return null;
        }

        return new BuilderOptions(source, output, wordnetDir, cmudictFile);
    }

    private static string DefaultOutputPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "StupidDict", "dictionary.db");
}

internal sealed record EcdictRow(
    string Word,
    string Phonetic,
    string Pos,
    string Translation,
    string Definition,
    int Collins,
    int Oxford,
    string Tag,
    int Bnc,
    int Frq,
    string Exchange);

internal sealed class EcdictCsvRow
{
    public string? word { get; set; }
    public string? phonetic { get; set; }
    public string? pos { get; set; }
    public string? translation { get; set; }
    public string? definition { get; set; }
    public string? collins { get; set; }
    public string? oxford { get; set; }
    public string? tag { get; set; }
    public string? bnc { get; set; }
    public string? frq { get; set; }
    public string? exchange { get; set; }
}
