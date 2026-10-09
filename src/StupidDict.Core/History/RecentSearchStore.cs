using Microsoft.Data.Sqlite;

namespace StupidDict.Core.History;

/// <summary>
/// Recent searches, persisted locally. Fixed capacity of 30 — deliberately not
/// configurable, and there is no management UI.
/// </summary>
public sealed class RecentSearchStore : IDisposable
{
    public const int MaxEntries = 30;

    private readonly SqliteConnection _connection;
    private readonly object _writeGate = new();
    private long _lastTimestamp;

    public RecentSearchStore(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        _connection = SqliteConnections.Open(path, SqliteOpenMode.ReadWriteCreate);
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS recent_search (
                query_norm  TEXT PRIMARY KEY,
                query       TEXT NOT NULL,
                queried_at  INTEGER NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Records a query: deduplicated case-insensitively, moved to the front, trimmed to 30.</summary>
    public void Add(string query)
    {
        var trimmed = query.Trim();
        if (trimmed.Length == 0) return;
        // Monotonic timestamps keep "most recent first" stable even within the same millisecond.
        lock (_writeGate)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (now <= _lastTimestamp) now = _lastTimestamp + 1;
            _lastTimestamp = now;

            // INSERT and DELETE commit together: a reader can never observe the
            // un-trimmed table between the two statements, and a crash mid-write
            // rolls back to the pre-Add state instead of leaving 30+ rows.
            using var transaction = _connection.BeginTransaction();
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = """
                INSERT INTO recent_search (query_norm, query, queried_at)
                VALUES ($norm, $query, $now)
                ON CONFLICT (query_norm) DO UPDATE SET query = $query, queried_at = $now;

                DELETE FROM recent_search WHERE rowid NOT IN (
                    SELECT rowid FROM recent_search
                    ORDER BY queried_at DESC, rowid DESC
                    LIMIT $max
                );
                """;
            cmd.Parameters.AddWithValue("$norm", trimmed.ToLowerInvariant());
            cmd.Parameters.AddWithValue("$query", trimmed);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.Parameters.AddWithValue("$max", MaxEntries);
            cmd.ExecuteNonQuery();
            transaction.Commit();
        }
    }

    public IReadOnlyList<RecentSearch> GetRecent()
    {
        // Same gate as Add: reads and writes share one connection, and
        // Microsoft.Data.Sqlite allows only one thread in it at a time. The
        // read rides the UI's RefreshRecents while writes land on the
        // thread-pool lookup path, so without this lock the two interleave.
        lock (_writeGate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT query, queried_at FROM recent_search ORDER BY queried_at DESC, rowid DESC";
            using var reader = cmd.ExecuteReader();
            List<RecentSearch> recent = [];
            while (reader.Read())
                recent.Add(new RecentSearch(
                    reader.GetString(0),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1))));
            return recent;
        }
    }

    public void Dispose() => _connection.Dispose();
}
