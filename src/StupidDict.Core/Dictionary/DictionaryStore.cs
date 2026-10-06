using Microsoft.Data.Sqlite;

namespace StupidDict.Core.Dictionary;

/// <summary>
/// All SQL for reading the dictionary. The per-direction dictionaries
/// (English-Chinese, English-English, Chinese-English) are thin facades over this.
/// </summary>
public sealed class DictionaryStore
{
    private const string EntryColumns = "word, phonetic, pos, translation, definition, freq, bnc, tag";

    private static readonly string CommonalitySql =
        "CASE WHEN freq > 0 THEN freq WHEN bnc > 0 THEN bnc ELSE 999999 END";

    private readonly Lazy<DictionaryDatabase> _database;

    internal DictionaryStore(Lazy<DictionaryDatabase> database) => _database = database;

    public DictionaryEntry? FindWord(string word)
    {
        using var cmd = Command($"SELECT {EntryColumns} FROM word WHERE word = $word LIMIT 1");
        cmd.Parameters.AddWithValue("$word", word);
        return ReadOne(cmd);
    }

    public DictionaryEntry? FindNormalized(string lower)
    {
        using var cmd = Command($"SELECT {EntryColumns} FROM word WHERE word_lower = $lower LIMIT 1");
        cmd.Parameters.AddWithValue("$lower", lower);
        return ReadOne(cmd);
    }

    public DictionaryEntry? FindByWordForm(string lower)
    {
        using var cmd = Command($"""
            SELECT w.word, w.phonetic, w.pos, w.translation, w.definition, w.freq, w.bnc, w.tag
            FROM word_form f JOIN word w ON w.id = f.word_id
            WHERE f.inflected = $lower
            ORDER BY {CommonalitySql.Replace("freq", "w.freq").Replace("bnc", "w.bnc")}, w.word
            LIMIT 1
            """);
        cmd.Parameters.AddWithValue("$lower", lower);
        return ReadOne(cmd);
    }

    /// <summary>Headwords starting with <paramref name="lower"/>, common words first.</summary>
    public List<DictionaryEntry> FindPrefix(string lower, int limit)
    {
        using var cmd = Command($"""
            SELECT {EntryColumns} FROM word
            WHERE word_lower >= $lower AND word_lower < $end
            ORDER BY {CommonalitySql}, word_lower
            LIMIT $limit
            """);
        cmd.Parameters.AddWithValue("$lower", lower);
        // Any string starting with $lower sorts before this: the largest valid
        // UTF-8 sequence appended to the prefix.
        cmd.Parameters.AddWithValue("$end", lower + "\U0010FFFF");
        cmd.Parameters.AddWithValue("$limit", limit);
        return ReadList(cmd);
    }

    public List<ChineseMatch> FindTerm(string term, int limit)
    {
        using var cmd = Command($"""
            SELECT DISTINCT z.term, w.word, w.phonetic, w.pos, w.translation, w.definition, w.freq, w.bnc, w.tag
            FROM zh_index z JOIN word w ON w.id = z.word_id
            WHERE z.term = $term
            ORDER BY {CommonalitySql.Replace("freq", "w.freq").Replace("bnc", "w.bnc")}, w.word
            LIMIT $limit
            """);
        cmd.Parameters.AddWithValue("$term", term);
        cmd.Parameters.AddWithValue("$limit", limit);
        return ReadChineseMatches(cmd);
    }

    public List<ChineseMatch> FindTermPrefix(string term, int limit)
    {
        using var cmd = Command($"""
            SELECT DISTINCT z.term, w.word, w.phonetic, w.pos, w.translation, w.definition, w.freq, w.bnc, w.tag
            FROM zh_index z JOIN word w ON w.id = z.word_id
            WHERE z.term >= $term AND z.term < $end
            ORDER BY {CommonalitySql.Replace("freq", "w.freq").Replace("bnc", "w.bnc")}, w.word
            LIMIT $limit
            """);
        cmd.Parameters.AddWithValue("$term", term);
        cmd.Parameters.AddWithValue("$end", term + "\U0010FFFF");
        cmd.Parameters.AddWithValue("$limit", limit);
        return ReadChineseMatches(cmd);
    }

    /// <summary>All headwords with a corpus frequency rank — the fuzzy matcher's candidate set.</summary>
    public List<CommonWord> GetCommonWords()
    {
        using var cmd = Command("SELECT word_lower, freq FROM word WHERE freq > 0 OR bnc > 0");
        using var reader = cmd.ExecuteReader();
        List<CommonWord> words = [];
        while (reader.Read())
            words.Add(new CommonWord(reader.GetString(0), reader.GetInt32(1)));
        return words;
    }

    private SqliteCommand Command(string text) => _database.Value.CreateCommand(text);

    private static DictionaryEntry? ReadOne(SqliteCommand cmd)
    {
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? MapEntry(reader) : null;
    }

    private static List<DictionaryEntry> ReadList(SqliteCommand cmd)
    {
        using var reader = cmd.ExecuteReader();
        List<DictionaryEntry> entries = [];
        while (reader.Read()) entries.Add(MapEntry(reader));
        return entries;
    }

    private static List<ChineseMatch> ReadChineseMatches(SqliteCommand cmd)
    {
        using var reader = cmd.ExecuteReader();
        List<ChineseMatch> matches = [];
        while (reader.Read())
            matches.Add(new ChineseMatch(reader.GetString(0), MapEntry(reader, offset: 1)));
        return matches;
    }

    private static DictionaryEntry MapEntry(SqliteDataReader reader, int offset = 0) => new(
        reader.GetString(offset),
        reader.IsDBNull(offset + 1) ? "" : reader.GetString(offset + 1),
        reader.IsDBNull(offset + 2) ? "" : reader.GetString(offset + 2),
        reader.IsDBNull(offset + 3) ? "" : reader.GetString(offset + 3),
        reader.IsDBNull(offset + 4) ? "" : reader.GetString(offset + 4),
        reader.IsDBNull(offset + 7) ? "" : reader.GetString(offset + 7),
        reader.IsDBNull(offset + 5) ? 0 : reader.GetInt32(offset + 5),
        reader.IsDBNull(offset + 6) ? 0 : reader.GetInt32(offset + 6));
}
