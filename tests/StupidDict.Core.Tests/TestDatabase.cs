using System.Text.RegularExpressions;
using StupidDict.Core.Dictionary;

namespace StupidDict.Core.Tests;

internal static class TestDatabase
{
    public sealed record Row(
        string Word,
        string Phonetic = "",
        string Pos = "",
        string Translation = "",
        string Definition = "",
        int Freq = 0,
        string Exchange = "");

    public static (string DictionaryPath, string HistoryPath) Create(params Row[] rows)
    {
        var directory = Path.Combine(Path.GetTempPath(), "stupiddict-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var dictionaryPath = Path.Combine(directory, "dictionary.db");
        using (var db = DictionaryDatabase.Create(dictionaryPath))
        {
            db.BeginTransaction();
            foreach (var row in rows)
            {
                var wordId = db.InsertWord(row.Word, row.Phonetic, row.Pos, row.Translation, row.Definition, row.Freq, 0, "");
                if (wordId < 0) continue;
                foreach (Match match in Regex.Matches(row.Translation, @"[\u3400-\u9FFF]+"))
                    if (match.Value.Length <= 12)
                        db.InsertZhTerm(match.Value, wordId);
                foreach (var pair in row.Exchange.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    if (pair.Length > 2 && pair[1] == ':')
                        db.InsertWordForm(pair[2..].ToLowerInvariant(), wordId);
            }
            db.CommitTransaction();
        }
        return (dictionaryPath, Path.Combine(directory, "history.db"));
    }
}
