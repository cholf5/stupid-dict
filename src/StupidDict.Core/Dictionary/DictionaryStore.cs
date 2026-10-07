using Microsoft.Data.Sqlite;
using System.Text.RegularExpressions;

namespace StupidDict.Core.Dictionary;

/// <summary>
/// All SQL for reading the dictionary. The per-direction dictionaries
/// (English-Chinese, English-English, Chinese-English) are thin facades over this.
/// </summary>
public sealed class DictionaryStore
{
    private const int RelatedWordLimit = 10;

    private static readonly string CommonalitySql =
        "CASE WHEN freq > 0 THEN freq WHEN bnc > 0 THEN bnc ELSE 999999 END";

    private static readonly Regex PosPrefixRegex = new(@"^[a-zA-Z]+\.\s*", RegexOptions.Compiled);

    private readonly Lazy<DictionaryDatabase> _database;
    private readonly Lazy<bool> _hasThesaurus;
    private readonly Lazy<bool> _hasUsPhonetic;

    internal DictionaryStore(Lazy<DictionaryDatabase> database)
    {
        _database = database;
        // Dictionaries built before the thesaurus data existed lack the table;
        // the sections then stay hidden instead of failing every lookup.
        _hasThesaurus = new Lazy<bool>(() =>
        {
            using var cmd = Command("SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'syn_group'");
            return cmd.ExecuteScalar() is not null;
        });
        // Same for the US-phonetic column: old databases read as if every
        // entry had none, so the US pronunciation line just stays hidden.
        _hasUsPhonetic = new Lazy<bool>(() =>
        {
            using var cmd = Command("SELECT 1 FROM pragma_table_info('word') WHERE name = 'phonetic_us'");
            return cmd.ExecuteScalar() is not null;
        });
    }

    /// <summary>
    /// The word-table projection for a <see cref="DictionaryEntry"/>. Without
    /// phonetic_us a NULL is selected in its place so column positions — and
    /// therefore MapEntry — stay identical for both schema generations.
    /// </summary>
    private string EntryColumns => _hasUsPhonetic.Value
        ? "word, phonetic, phonetic_us, pos, translation, definition, freq, bnc, tag"
        : "word, phonetic, NULL, pos, translation, definition, freq, bnc, tag";

    private string WordColumns(string alias) => _hasUsPhonetic.Value
        ? $"{alias}.word, {alias}.phonetic, {alias}.phonetic_us, {alias}.pos, {alias}.translation, {alias}.definition, {alias}.freq, {alias}.bnc, {alias}.tag"
        : $"{alias}.word, {alias}.phonetic, NULL, {alias}.pos, {alias}.translation, {alias}.definition, {alias}.freq, {alias}.bnc, {alias}.tag";

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
            SELECT {WordColumns("w")}
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
            SELECT DISTINCT z.term, {WordColumns("w")}
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
            SELECT DISTINCT z.term, {WordColumns("w")}
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

    /// <summary>
    /// 近义词 / 反义词 / 联想词 of a headword (matched by word_lower), or null when
    /// the dictionary carries no thesaurus data or the word has none. Related
    /// words are the synonym/antonym pool ranked by corpus frequency, glossed
    /// from the word table.
    /// </summary>
    public Thesaurus? GetThesaurus(string lower)
    {
        if (!_hasThesaurus.Value) return null;

        using var cmd = Command("""
            SELECT g.kind, g.pos, g.words
            FROM syn_group g JOIN word w ON w.id = g.word_id
            WHERE w.word_lower = $lower
            ORDER BY g.rowid
            """);
        cmd.Parameters.AddWithValue("$lower", lower);

        List<SynonymLine> synonyms = [];
        List<SynonymLine> antonyms = [];
        var pool = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                var words = reader.GetString(2)
                    .Split(", ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var word in words) pool.Add(word);
                var line = new SynonymLine(reader.GetString(1), words);
                if (reader.GetString(0) == "syn") synonyms.Add(line);
                else antonyms.Add(line);
            }
        }

        if (synonyms.Count == 0 && antonyms.Count == 0) return null;
        return new Thesaurus(synonyms, antonyms, FindRelatedWords(pool));
    }

    private List<RelatedWord> FindRelatedWords(HashSet<string> candidates)
    {
        if (candidates.Count == 0) return [];

        using var cmd = Command($"""
            SELECT word, translation, {CommonalitySql} AS commonality
            FROM word WHERE word_lower IN ({string.Join(",", candidates.Select((_, i) => $"$c{i}"))})
            """);
        var i = 0;
        foreach (var candidate in candidates)
            cmd.Parameters.AddWithValue($"$c{i++}", candidate.ToLowerInvariant());

        List<(string Word, string Gloss, int Commonality)> matches = [];
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
                matches.Add((reader.GetString(0), FirstGloss(reader.IsDBNull(1) ? "" : reader.GetString(1)),
                    reader.GetInt32(2)));
        }

        return matches
            .OrderBy(m => m.Commonality)
            .ThenBy(m => m.Word, StringComparer.Ordinal)
            .Take(RelatedWordLimit)
            .Select(m => new RelatedWord(m.Word, m.Gloss))
            .ToList();
    }

    /// <summary>The short gloss shown after a related word: first sense line, POS prefix stripped.</summary>
    internal static string FirstGloss(string translation)
    {
        var line = translation.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "";
        line = PosPrefixRegex.Replace(line, "").Split('；')[0].TrimEnd('。', '，', '…', ';', ' ');
        // ECDICT glosses separate senses with both full- and half-width commas.
        var chunks = line.Split([',', '，'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join("，", chunks.Take(2));
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
        reader.IsDBNull(offset + 5) ? "" : reader.GetString(offset + 5),
        reader.IsDBNull(offset + 8) ? "" : reader.GetString(offset + 8),
        reader.IsDBNull(offset + 6) ? 0 : reader.GetInt32(offset + 6),
        reader.IsDBNull(offset + 7) ? 0 : reader.GetInt32(offset + 7));
}
