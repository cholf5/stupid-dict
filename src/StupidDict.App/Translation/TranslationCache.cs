using System.Text;
using Microsoft.Data.Sqlite;

namespace StupidDict.App.Translation;

/// <summary>
/// Byte-bounded LRU cache in its own SQLite file. It stores only a key hash,
/// the translated text and metadata — never the source text. All failures are
/// best-effort: a broken cache must never break translation.
/// </summary>
public sealed class TranslationCache : IDisposable
{
    private const long SchemaVersion = 1;
    private const int RowOverheadBytes = 256;

    private readonly object _gate = new();
    private readonly long _maxBytes;
    private readonly Func<long> _clock;
    private SqliteConnection? _connection;
    private bool _disabled;

    public TranslationCache(string path, long maxBytes = 20L * 1024 * 1024, Func<long>? clock = null)
    {
        _maxBytes = maxBytes;
        _clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        try
        {
            Open(path);
        }
        catch
        {
            // 设计：损坏/打不开 → 删文件重建（缓存可丢），仍失败才禁用。只
            // 禁用会让坏文件永久占位，缓存从此无声地死掉。
            try
            {
                _connection?.Dispose();
                _connection = null;
                File.Delete(path);
                Open(path);
            }
            catch
            {
                _disabled = true;
                _connection?.Dispose();
                _connection = null;
            }
        }
    }

    public bool TryGet(string key, out string translation)
    {
        translation = "";
        lock (_gate)
        {
            if (_disabled || _connection is null) return false;
            try
            {
                using var transaction = _connection.BeginTransaction();
                using var select = _connection.CreateCommand();
                select.Transaction = transaction;
                select.CommandText = "SELECT translation FROM translation_cache WHERE cache_key = $key";
                select.Parameters.AddWithValue("$key", key);
                if (select.ExecuteScalar() is not string value) return false;

                using var touch = _connection.CreateCommand();
                touch.Transaction = transaction;
                touch.CommandText = """
                    UPDATE translation_cache
                    SET last_used_at = $now, hit_count = hit_count + 1
                    WHERE cache_key = $key
                    """;
                touch.Parameters.AddWithValue("$key", key);
                touch.Parameters.AddWithValue("$now", _clock());
                touch.ExecuteNonQuery();
                transaction.Commit();

                translation = value;
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public void Put(string key, string translation)
    {
        var size = Encoding.UTF8.GetByteCount(translation) + RowOverheadBytes;
        if (size > _maxBytes) return;

        lock (_gate)
        {
            if (_disabled || _connection is null) return;
            try
            {
                using var transaction = _connection.BeginTransaction();
                var now = _clock();

                using (var upsert = _connection.CreateCommand())
                {
                    upsert.Transaction = transaction;
                    upsert.CommandText = """
                        INSERT INTO translation_cache
                            (cache_key, translation, size_bytes, created_at, last_used_at)
                        VALUES ($key, $translation, $size, $now, $now)
                        ON CONFLICT(cache_key) DO UPDATE SET
                            translation = $translation,
                            size_bytes = $size,
                            last_used_at = $now
                        """;
                    upsert.Parameters.AddWithValue("$key", key);
                    upsert.Parameters.AddWithValue("$translation", translation);
                    upsert.Parameters.AddWithValue("$size", size);
                    upsert.Parameters.AddWithValue("$now", now);
                    upsert.ExecuteNonQuery();
                }

                while (TotalBytes(transaction) > _maxBytes)
                {
                    using var oldest = _connection.CreateCommand();
                    oldest.Transaction = transaction;
                    oldest.CommandText = """
                        SELECT cache_key FROM translation_cache
                        ORDER BY last_used_at ASC, cache_key ASC LIMIT 1
                        """;
                    if (oldest.ExecuteScalar() is not string victim) break;

                    using var delete = _connection.CreateCommand();
                    delete.Transaction = transaction;
                    delete.CommandText = "DELETE FROM translation_cache WHERE cache_key = $key";
                    delete.Parameters.AddWithValue("$key", victim);
                    delete.ExecuteNonQuery();
                }

                transaction.Commit();
            }
            catch
            {
                // Cache writes are best-effort.
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            if (_disabled || _connection is null) return;
            try
            {
                using var command = _connection.CreateCommand();
                command.CommandText = "DELETE FROM translation_cache; PRAGMA incremental_vacuum;";
                command.ExecuteNonQuery();
            }
            catch
            {
            }
        }
    }

    public long SizeBytes()
    {
        lock (_gate)
        {
            if (_disabled || _connection is null) return 0;
            try
            {
                using var command = _connection.CreateCommand();
                command.CommandText = "SELECT COALESCE(SUM(size_bytes), 0) FROM translation_cache";
                return Convert.ToInt64(command.ExecuteScalar() ?? 0L);
            }
            catch
            {
                return 0;
            }
        }
    }

    private long TotalBytes(SqliteTransaction transaction)
    {
        using var command = _connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COALESCE(SUM(size_bytes), 0) FROM translation_cache";
        return Convert.ToInt64(command.ExecuteScalar() ?? 0L);
    }

    private void Open(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        _connection.Open();

        using (var pragma = _connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA auto_vacuum=INCREMENTAL; PRAGMA busy_timeout=1000;";
            pragma.ExecuteNonQuery();
        }

        long version;
        using (var read = _connection.CreateCommand())
        {
            read.CommandText = "PRAGMA user_version";
            version = Convert.ToInt64(read.ExecuteScalar() ?? 0L);
        }

        if (version != SchemaVersion)
        {
            using var reset = _connection.CreateCommand();
            reset.CommandText = "DROP TABLE IF EXISTS translation_cache; PRAGMA user_version = 1;";
            reset.ExecuteNonQuery();
        }

        using var schema = _connection.CreateCommand();
        schema.CommandText = """
            CREATE TABLE IF NOT EXISTS translation_cache (
                cache_key    TEXT PRIMARY KEY,
                translation  TEXT NOT NULL,
                size_bytes   INTEGER NOT NULL,
                created_at   INTEGER NOT NULL,
                last_used_at INTEGER NOT NULL,
                hit_count    INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_translation_cache_lru
                ON translation_cache(last_used_at);
            """;
        schema.ExecuteNonQuery();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _connection?.Dispose();
            _connection = null;
        }
    }
}
