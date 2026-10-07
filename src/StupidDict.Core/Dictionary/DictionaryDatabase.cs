using Microsoft.Data.Sqlite;

namespace StupidDict.Core.Dictionary;

/// <summary>
/// Owns the SQLite connection to a dictionary file: schema creation and the
/// statements used by <see cref="DictionaryStore"/> and the data builder.
/// </summary>
public sealed class DictionaryDatabase : IDisposable
{
    private readonly SqliteConnection _connection;
    private SqliteTransaction? _transaction;

    private DictionaryDatabase(SqliteConnection connection) => _connection = connection;

    /// <summary>Opens an existing dictionary file for reading.</summary>
    public static DictionaryDatabase OpenRead(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString());
        connection.Open();
        return new DictionaryDatabase(connection);
    }

    /// <summary>Creates (replacing) a dictionary file for the data builder.</summary>
    public static DictionaryDatabase Create(string path)
    {
        path = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        if (File.Exists(path)) File.Delete(path);

        var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=OFF; PRAGMA synchronous=OFF;";
            pragma.ExecuteNonQuery();
        }

        var db = new DictionaryDatabase(connection);
        using var schema = db.CreateCommand("""
            CREATE TABLE word (
                id          INTEGER PRIMARY KEY,
                word        TEXT NOT NULL UNIQUE,
                word_lower  TEXT NOT NULL UNIQUE,
                phonetic    TEXT NOT NULL DEFAULT '',
                pos         TEXT NOT NULL DEFAULT '',
                translation TEXT NOT NULL DEFAULT '',
                definition  TEXT NOT NULL DEFAULT '',
                freq        INTEGER NOT NULL DEFAULT 0,
                bnc         INTEGER NOT NULL DEFAULT 0,
                tag         TEXT NOT NULL DEFAULT ''
            );
            CREATE INDEX idx_word_lower ON word (word_lower);

            -- Chinese term → English headword (reverse lookup 中文 → 英文)
            CREATE TABLE zh_index (
                term    TEXT NOT NULL,
                word_id INTEGER NOT NULL
            );
            CREATE INDEX idx_zh_term ON zh_index (term);

            -- inflected form → base headword ("cats" → "cat")
            CREATE TABLE word_form (
                inflected TEXT NOT NULL,
                word_id   INTEGER NOT NULL
            );
            CREATE INDEX idx_word_form ON word_form (inflected);

            -- thesaurus lines from WordNet ("近义词 / 反义词"), one row per POS line
            CREATE TABLE syn_group (
                word_id INTEGER NOT NULL,
                kind    TEXT NOT NULL,
                pos     TEXT NOT NULL,
                words   TEXT NOT NULL
            );
            CREATE INDEX idx_syn_group_word ON syn_group (word_id);

            CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            """);
        schema.ExecuteNonQuery();
        return db;
    }

    internal SqliteCommand CreateCommand(string text)
    {
        var command = _connection.CreateCommand();
        command.CommandText = text;
        command.Transaction = _transaction;
        return command;
    }

    public SqliteTransaction BeginTransaction()
    {
        _transaction = _connection.BeginTransaction();
        return _transaction;
    }

    public void CommitTransaction()
    {
        _transaction?.Commit();
        _transaction = null;
    }

    /// <summary>Inserts a headword. Returns its id, or -1 if the normalized headword already exists.</summary>
    public long InsertWord(string word, string phonetic, string pos, string translation, string definition, int freq, int bnc, string tag)
    {
        using var cmd = CreateCommand("""
            INSERT INTO word (word, word_lower, phonetic, pos, translation, definition, freq, bnc, tag)
            VALUES ($word, $word_lower, $phonetic, $pos, $translation, $definition, $freq, $bnc, $tag)
            ON CONFLICT (word_lower) DO NOTHING
            """);
        cmd.Parameters.AddWithValue("$word", word);
        cmd.Parameters.AddWithValue("$word_lower", word.ToLowerInvariant());
        cmd.Parameters.AddWithValue("$phonetic", phonetic);
        cmd.Parameters.AddWithValue("$pos", pos);
        cmd.Parameters.AddWithValue("$translation", translation);
        cmd.Parameters.AddWithValue("$definition", definition);
        cmd.Parameters.AddWithValue("$freq", freq);
        cmd.Parameters.AddWithValue("$bnc", bnc);
        cmd.Parameters.AddWithValue("$tag", tag);
        return cmd.ExecuteNonQuery() == 0 ? -1 : LastInsertRowId();
    }

    public void InsertZhTerm(string term, long wordId)
    {
        using var cmd = CreateCommand("INSERT INTO zh_index (term, word_id) VALUES ($term, $word_id)");
        cmd.Parameters.AddWithValue("$term", term);
        cmd.Parameters.AddWithValue("$word_id", wordId);
        cmd.ExecuteNonQuery();
    }

    public void InsertWordForm(string inflected, long wordId)
    {
        using var cmd = CreateCommand("INSERT INTO word_form (inflected, word_id) VALUES ($inflected, $word_id)");
        cmd.Parameters.AddWithValue("$inflected", inflected);
        cmd.Parameters.AddWithValue("$word_id", wordId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>One POS line of the thesaurus ("syn" or "ant"), e.g. ("syn", "adj.", "great, nice").</summary>
    public void InsertSynGroup(long wordId, string kind, string pos, string words)
    {
        using var cmd = CreateCommand("""
            INSERT INTO syn_group (word_id, kind, pos, words) VALUES ($word_id, $kind, $pos, $words)
            """);
        cmd.Parameters.AddWithValue("$word_id", wordId);
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$pos", pos);
        cmd.Parameters.AddWithValue("$words", words);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Returns the headword id for a normalized (lowercase) word, or null.</summary>
    public long? FindWordId(string lower)
    {
        using var cmd = CreateCommand("SELECT id FROM word WHERE word_lower = $lower LIMIT 1");
        cmd.Parameters.AddWithValue("$lower", lower);
        return cmd.ExecuteScalar() as long?;
    }

    public void SetMeta(string key, string value)
    {
        using var cmd = CreateCommand("""
            INSERT INTO meta (key, value) VALUES ($key, $value)
            ON CONFLICT (key) DO UPDATE SET value = $value
            """);
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", value);
        cmd.ExecuteNonQuery();
    }

    private long LastInsertRowId()
    {
        using var cmd = CreateCommand("SELECT last_insert_rowid()");
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    public void Dispose() => _connection.Dispose();
}
