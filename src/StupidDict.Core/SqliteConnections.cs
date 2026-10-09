using Microsoft.Data.Sqlite;

namespace StupidDict.Core;

/// <summary>
/// Opens the app's local SQLite files (dictionary + history). Pooling is
/// deliberately off: Microsoft.Data.Sqlite otherwise keeps the file handle
/// alive after the connection is disposed, and Windows refuses to delete,
/// replace or read-for-zipping a file that is still open — it reports "being
/// used by another process" where Unix quietly allows all three. That showed
/// up as seven failing tests locally, and the app does replace and re-read
/// these files (dictionary download, manual import).
/// </summary>
internal static class SqliteConnections
{
    public static SqliteConnection Open(string path, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }
}
