using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.Data.Sqlite;
using StupidDict.Core.Dictionary;

if (args.Length < 1)
{
    Console.Error.WriteLine("""
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
    return 1;
}

var wordnetDir = (string?)null;
var cmudictFile = (string?)null;
List<string> positional = [];
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--wordnet" && i + 1 < args.Length) wordnetDir = Path.GetFullPath(args[++i]);
    else if (args[i] == "--cmudict" && i + 1 < args.Length) cmudictFile = Path.GetFullPath(args[++i]);
    else positional.Add(args[i]);
}
if (wordnetDir is { } wn && !Directory.Exists(wn))
{
    Console.Error.WriteLine($"WordNet 目录不存在: {wn}");
    return 1;
}
if (cmudictFile is { } cmu && !File.Exists(cmu))
{
    Console.Error.WriteLine($"cmudict 文件不存在: {cmu}");
    return 1;
}

var usPhonetics = cmudictFile is { } file ? CmuPhonetics.Load(file) : null;

var source = Path.GetFullPath(positional[0]);
var output = positional.Count > 1 ? Path.GetFullPath(positional[1]) : DefaultOutputPath();
if (!File.Exists(source))
{
    Console.Error.WriteLine($"源文件不存在: {source}");
    return 1;
}

var commonTags = new HashSet<string> { "zk", "gk", "cet4", "cet6", "ky", "toefl", "ielts", "gre" };
var zhTermRegex = new Regex(@"[\u3400-\u9FFF]+", RegexOptions.Compiled);
var started = Stopwatch.StartNew();
long entries = 0, skipped = 0, common = 0, zhTerms = 0, forms = 0, usPhoneticCount = 0;

using (var db = DictionaryDatabase.Create(output))
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
    if (cmudictFile is { } cmuFile) db.SetMeta("cmudict_file", Path.GetFileName(cmuFile));
    db.CommitTransaction();

    if (wordnetDir is { } dir)
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

var sizeMb = new FileInfo(output).Length / 1024.0 / 1024.0;
Console.WriteLine($"完成: {entries:N0} 词条（跳过 {skipped:N0}），常用词 {common:N0}，中文索引 {zhTerms:N0}，词形映射 {forms:N0}，美音音标 {usPhoneticCount:N0}");
Console.WriteLine($"输出: {output}（{sizeMb:F0} MB，用时 {started.Elapsed.TotalSeconds:F0}s）");
return 0;

static string DefaultOutputPath() => Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "StupidDict", "dictionary.db");

static IEnumerable<EcdictRow> ReadRows(string path) =>
    path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ? ReadCsv(path) : ReadSqlite(path);

static IEnumerable<EcdictRow> ReadSqlite(string path)
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

static IEnumerable<EcdictRow> ReadCsv(string path)
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

static int ParseInt(string? value) => int.TryParse(value, out var parsed) ? parsed : 0;

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
