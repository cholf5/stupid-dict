using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

// Builds the pronunciation pack: per-word MP3 files (uk/ and us/) generated
// offline with the Piper TTS engine, ready to ship as a GitHub Release asset
// the app can download. One-off, hours-long, fully local — no cloud calls.

var options = AudioPackOptions.Parse(args, out var parseError);
if (options is null)
{
    Console.Error.WriteLine(parseError);
    return 1;
}

var defaultDictionary = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "StupidDict", "dictionary.db");

string? dictionary = options.Dictionary;
string? outDirectory = options.OutDirectory;
var zipPath = options.ZipPath;
var piper = options.Piper;
var ffmpeg = options.Ffmpeg;
string? modelUk = options.ModelUk;
string? modelUs = options.ModelUs;
var top = options.Top;
var workers = options.Workers;
var makeZip = options.MakeZip;

dictionary ??= defaultDictionary;
outDirectory ??= Path.Combine(Environment.CurrentDirectory, "audio-pack");

if (!File.Exists(dictionary))
{
    Console.Error.WriteLine($"词典不存在: {dictionary}（先用 DataBuilder 构建 dictionary.db）");
    return 1;
}
foreach (var (name, model) in new[] { ("--model-uk", modelUk), ("--model-us", modelUs) })
    if (model is null || !File.Exists(model) || !File.Exists(model + ".json"))
    {
        Console.Error.WriteLine($"""
            {name} 缺失或没有配套的 .onnx.json。从 rhasspy/piper-voices 下载，例如：

              curl -L -o en_GB-alan-medium.onnx \
                https://huggingface.co/rhasspy/piper-voices/resolve/v1.0.0/en/en_GB/alan/medium/en_GB-alan-medium.onnx
              curl -L -o en_GB-alan-medium.onnx.json \
                https://huggingface.co/rhasspy/piper-voices/resolve/v1.0.0/en/en_GB/alan/medium/en_GB-alan-medium.onnx.json
              curl -L -o en_US-lessac-medium.onnx \
                https://huggingface.co/rhasspy/piper-voices/resolve/v1.0.0/en/en_US/lessac/medium/en_US-lessac-medium.onnx
              curl -L -o en_US-lessac-medium.onnx.json \
                https://huggingface.co/rhasspy/piper-voices/resolve/v1.0.0/en/en_US/lessac/medium/en_US-lessac-medium.onnx.json

            引擎与转码工具: pip install piper-tts；ffmpeg 需要 PATH 可用。
            """);
        return 1;
    }

var invalidChars = Path.GetInvalidFileNameChars();
var words = SelectWords(dictionary, top)
    .Where(w => w.Length > 0 && w.Length <= 64 && w.IndexOfAny(invalidChars) < 0)
    .ToList();
if (words.Count == 0)
{
    Console.Error.WriteLine("词典中没有可用词条。");
    return 1;
}

Directory.CreateDirectory(Path.Combine(outDirectory, "uk"));
Directory.CreateDirectory(Path.Combine(outDirectory, "us"));

var started = Stopwatch.StartNew();
var generated = 0;
var skipped = 0;
var failed = 0;
var totalJobs = words.Count * 2;
var done = 0;

Parallel.ForEach(
    words.SelectMany(word => new[] { ("uk", modelUk!, word), ("us", modelUs!, word) }),
    new ParallelOptions { MaxDegreeOfParallelism = workers },
    job =>
    {
        var (accent, model, word) = job;
        var target = Path.Combine(outDirectory, accent, word + ".mp3");
        try
        {
            if (File.Exists(target))
                Interlocked.Increment(ref skipped);
            else if (Generate(word, model, target))
                Interlocked.Increment(ref generated);
            else
                Interlocked.Increment(ref failed);
        }
        catch
        {
            Interlocked.Increment(ref failed);
        }

        var finished = Interlocked.Increment(ref done);
        if (finished % 500 == 0)
            Console.WriteLine($"... {finished}/{totalJobs}，新增 {generated}，跳过 {skipped}，失败 {failed}，用时 {started.Elapsed.TotalMinutes:F0} 分钟");
    });

File.WriteAllText(Path.Combine(outDirectory, "manifest.txt"),
    $"""
    words: {words.Count}
    files: {generated + skipped} ({generated} generated, {skipped} already present, {failed} failed)
    model_uk: {Path.GetFileName(modelUk)}
    model_us: {Path.GetFileName(modelUs)}
    generated_at: {DateTime.UtcNow:O}
    """);

Console.WriteLine($"完成: 新增 {generated}，复用 {skipped}，失败 {failed}，用时 {started.Elapsed.TotalMinutes:F1} 分钟");

if (makeZip)
{
    if (File.Exists(zipPath)) File.Delete(zipPath);
    System.IO.Compression.ZipFile.CreateFromDirectory(outDirectory, zipPath, System.IO.Compression.CompressionLevel.Optimal, includeBaseDirectory: false);
    var sizeMb = new FileInfo(zipPath).Length / 1024.0 / 1024.0;
    Console.WriteLine($"打包: {zipPath}（{sizeMb:F0} MB）");
    using var stream = File.OpenRead(zipPath);
    var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    File.WriteAllText(zipPath + ".sha256", $"{hash}  {Path.GetFileName(zipPath)}\n");
}

return failed > 0 && generated == 0 ? 1 : 0;

/// <summary>Headwords worth recording, most common first. Plain letter words only.</summary>
static List<string> SelectWords(string dictionary, int top)
{
    using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
    {
        DataSource = dictionary,
        Mode = SqliteOpenMode.ReadOnly,
    }.ToString());
    connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText = """
        SELECT word_lower FROM word
        WHERE freq > 0 OR bnc > 0
           OR tag IN ('zk', 'gk', 'cet4', 'cet6', 'ky', 'toefl', 'ielts', 'gre')
        ORDER BY CASE WHEN freq > 0 THEN freq WHEN bnc > 0 THEN bnc ELSE 999999 END, word_lower
        LIMIT $top
        """;
    command.Parameters.AddWithValue("$top", top);
    var words = new List<string>();
    using var reader = command.ExecuteReader();
    while (reader.Read())
    {
        var word = reader.GetString(0).Trim();
        // Internal spaces keep multi-word entries ("ice cream"); anything with
        // digits or symbols reads badly and is left to the TTS fallback.
        if (Regex.IsMatch(word, @"^[a-z][a-z' -]*[a-z]$|^[a-z]$")) words.Add(word);
    }
    return words;
}

/// <summary>Piper: text → WAV, then ffmpeg: WAV → mono MP3. Returns false when a step fails.</summary>
bool Generate(string word, string model, string target)
{
    var tempDirectory = Directory.CreateTempSubdirectory("stupiddict-audio-");
    try
    {
        var wav = Path.Combine(tempDirectory.FullName, "word.wav");
        if (!Run(piper, ["--model", model, "--output_file", wav], stdin: word)) return false;
        if (!File.Exists(wav)) return false;
        return Run(ffmpeg, ["-y", "-loglevel", "error", "-i", wav, "-ac", "1", "-b:a", "48k", target]);
    }
    finally
    {
        tempDirectory.Delete(recursive: true);
    }

    static bool Run(string program, string[] arguments, string? stdin = null)
    {
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = program,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = stdin is not null,
                RedirectStandardError = true,
            };
            foreach (var argument in arguments) info.ArgumentList.Add(argument);
            using var process = Process.Start(info);
            if (process is null) return false;
            if (stdin is not null)
            {
                process.StandardInput.Write(stdin);
                process.StandardInput.Close();
            }
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                Console.Error.WriteLine($"  {program} 失败 (exit {process.ExitCode}): {error.Split('\n')[0]}");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  {program} 启动失败: {ex.Message}");
            return false;
        }
    }
}

/// <summary>AudioPackBuilder 命令行解析结果。</summary>
internal sealed record AudioPackOptions(
    string? Dictionary,
    string? OutDirectory,
    string ZipPath,
    bool MakeZip,
    int Top,
    int Workers,
    string Piper,
    string Ffmpeg,
    string? ModelUk,
    string? ModelUs)
{
    /// <summary>解析命令行；失败返回 null 并给出 error（调用方直接报错退出）。</summary>
    public static AudioPackOptions? Parse(IReadOnlyList<string> args, out string? error)
    {
        string? dictionary = null, outDirectory = null, zipPath = "audio-pack.zip", piper = "piper", ffmpeg = "ffmpeg";
        string? modelUk = null, modelUs = null;
        var top = 80_000;
        var workers = Math.Max(1, Environment.ProcessorCount / 2);
        var makeZip = true;

        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--out" when i + 1 < args.Count: outDirectory = Path.GetFullPath(args[++i]); break;
                case "--zip" when i + 1 < args.Count: zipPath = Path.GetFullPath(args[++i]); break;
                case "--no-zip": makeZip = false; break;

                // --top 的值解析失败必须显式报错退出：旧写法 `when i + 1 < Length &&
                // int.TryParse(...)` 失败时落 default，把值当词典路径，最后报出误导性
                // 的「词典不存在： abc」（kanban Q-003-3）。三个 case 依次覆盖：
                // 缺值、值可解析、值不可解析。
                case "--top" when i + 1 >= args.Count:
                    error = "--top 需要一个整数参数（例如 --top 80000）";
                    return null;
                case "--top" when int.TryParse(args[i + 1], out var t):
                    top = t;
                    i++;
                    break;
                case "--top":
                    error = $"--top 需要整数，收到: \"{args[i + 1]}\"";
                    return null;

                case "--workers" when i + 1 < args.Count && int.TryParse(args[++i], out var w): workers = Math.Max(1, w); break;
                case "--piper" when i + 1 < args.Count: piper = args[++i]; break;
                case "--ffmpeg" when i + 1 < args.Count: ffmpeg = args[++i]; break;
                case "--model-uk" when i + 1 < args.Count: modelUk = Path.GetFullPath(args[++i]); break;
                case "--model-us" when i + 1 < args.Count: modelUs = Path.GetFullPath(args[++i]); break;
                default: dictionary = Path.GetFullPath(args[i]); break;
            }
        }

        error = null;
        return new AudioPackOptions(dictionary, outDirectory, zipPath, makeZip, top, workers, piper, ffmpeg, modelUk, modelUs);
    }
}
