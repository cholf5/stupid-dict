using System.IO.Compression;
using Microsoft.Data.Sqlite;
using StupidDict.App.Localization;

namespace StupidDict.App.Assets;

/// <summary>
/// Builds the single-file pronunciation pack database from an audio-pack zip
/// (uk/us per-word MP3 entries — the data-2 asset and manual browser
/// downloads). Used by the download flow after checksum verification and by
/// manual zip imports. Entries stream into one transaction with a per-entry
/// CRC check (ZipArchive does not verify while reading — B-008), and the
/// database lands atomically: it is written to a staging file next to the
/// target and moved into place only when complete, so a cancelled or damaged
/// conversion never leaves a pack that looks installed. Progress receives
/// (entries done, entries total), reported every 256 entries — the same
/// shape the old entry-by-entry extraction reported.
/// </summary>
internal static class AudioPackConverter
{
    public static void ConvertZipToDatabase(string zipPath, string databasePath,
        Action<int, int>? progress = null, CancellationToken cancellation = default)
    {
        using var archive = ZipFile.OpenRead(zipPath);

        // Same gate the old import ran through ExtractZip: a rooted entry or
        // a ".." segment refuses the whole archive. The conversion would not
        // be fooled by it (names become text keys, never paths), but a zip
        // that tries is hostile and must not install.
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.Length == 0) continue;
            if (ZipSafety.EntryEscapesDestination(entry.FullName))
                throw new InvalidOperationException(string.Format(
                    Translations.Instance.ZipSlipFormat, entry.FullName));
        }

        // Parse the whole entry list up front: the pack-shape gate must fire
        // before anything is written, and the count feeds the progress bar.
        var packEntries = new List<(ZipArchiveEntry Entry, string Word, string Column)>();
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.Length == 0 || entry.FullName.EndsWith('/')) continue;
            if (!TryParseEntryName(entry.FullName, out var word, out var column)) continue;
            packEntries.Add((entry, word, column));
        }
        if (packEntries.Count == 0)
            throw new InvalidOperationException(Translations.Instance.ImportMissingPack);

        // Same staging discipline as dictionary extraction: a crashed or
        // cancelled run sweeps its own leftovers, the destination only ever
        // sees a complete file.
        var staging = databasePath + ".building";
        if (File.Exists(staging)) File.Delete(staging);
        var sqlite = new SqliteConnectionStringBuilder
        {
            DataSource = staging,
            // Pooling keys connections by string, not by file identity: a
            // pooled handle from a previous conversion would hand back the
            // OLD database file under the same staging path (and on Windows
            // its open handle would block replacing the target too — the
            // Core/SqliteConnections pitfall, met from the write side).
            Pooling = false,
        }.ToString();
        try
        {
            using (var connection = new SqliteConnection(sqlite))
            {
                connection.Open();
                using (var create = connection.CreateCommand())
                {
                    // WITHOUT ROWID: the word is the primary key and the only
                    // access path, so the table IS the index — no duplicate
                    // storage, lookups are a single B-tree descent.
                    // Page size deliberately left at the 4KB default: with
                    // this pack's uneven blob sizes, every alternative
                    // measured WORSE on real data (4KB 715MB, 8KB 800MB,
                    // 16KB 1.11GB, 32KB 2.07GB, 64KB 834MB) — the overflow
                    // remainder arithmetic punishes mid-size pages and
                    // uneven row sizes fragment large pages. Don't "fix"
                    // this without re-measuring.
                    create.CommandText =
                        "CREATE TABLE audio(word TEXT PRIMARY KEY, uk BLOB, us BLOB) WITHOUT ROWID";
                    create.ExecuteNonQuery();
                }

                using var transaction = connection.BeginTransaction();
                var insertUk = PrepareInsert(connection, transaction, "uk");
                var insertUs = PrepareInsert(connection, transaction, "us");
                insertUk.Prepare();
                insertUs.Prepare();

                progress?.Invoke(0, packEntries.Count);
                const int progressStride = 256;
                var done = 0;
                var buffer = new byte[1 << 16];
                foreach (var (entry, word, column) in packEntries)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var bytes = ReadVerified(entry, buffer);
                    var insert = column == "uk" ? insertUk : insertUs;
                    insert.Parameters["$word"].Value = word;
                    insert.Parameters["$blob"].Value = bytes;
                    insert.ExecuteNonQuery();
                    done++;
                    if (done == packEntries.Count || done % progressStride == 0)
                        progress?.Invoke(done, packEntries.Count);
                }
                transaction.Commit();
            }

            if (File.Exists(databasePath)) File.Delete(databasePath);
            File.Move(staging, databasePath);
        }
        finally
        {
            if (File.Exists(staging)) File.Delete(staging);
        }
    }

    // Entries are uk/{word}.mp3 or us/{word}.mp3; anything else (stray files,
    // nested directories, other extensions) is skipped, matching the old
    // import's leniency — the pack-shape gate above only needs one real
    // entry to accept the archive.
    private static bool TryParseEntryName(string fullName, out string word, out string column)
    {
        word = "";
        column = "";
        if (fullName.StartsWith("uk/", StringComparison.Ordinal)) column = "uk";
        else if (fullName.StartsWith("us/", StringComparison.Ordinal)) column = "us";
        else return false;
        var stem = fullName[3..];
        if (!stem.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)) return false;
        stem = stem[..^4];
        if (stem.Length == 0) return false;
        word = stem.ToLowerInvariant();
        return true;
    }

    private static SqliteCommand PrepareInsert(SqliteConnection connection, SqliteTransaction transaction, string column)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        // A word can appear in both accents; the second accent updates the
        // row the first created.
        command.CommandText =
            $"INSERT INTO audio(word, {column}) VALUES($word, $blob) " +
            $"ON CONFLICT(word) DO UPDATE SET {column} = $blob";
        command.Parameters.Add("$word", SqliteType.Text);
        command.Parameters.Add("$blob", SqliteType.Blob);
        return command;
    }

    // The B-008 pattern from MainWindow.ExtractEntry: ZipArchive streams
    // entries without verifying the CRC the central directory publishes, so
    // the bytes are checksummed as they pass and the mismatch surfaces as an
    // InvalidDataException — the signal the download flow's damaged-archive
    // purge stands on.
    private static byte[] ReadVerified(ZipArchiveEntry entry, byte[] buffer)
    {
        using var source = entry.Open();
        using var output = new MemoryStream();
        var crc = Crc32.InitialState;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            output.Write(buffer, 0, read);
            crc = Crc32.Update(crc, buffer.AsSpan(0, read));
        }
        if (Crc32.Value(crc) != entry.Crc32)
            throw new InvalidDataException(string.Format(
                Translations.Instance.ZipCrcMismatchFormat, entry.FullName));
        return output.ToArray();
    }
}
