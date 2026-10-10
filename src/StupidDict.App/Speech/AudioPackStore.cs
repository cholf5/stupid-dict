using Microsoft.Data.Sqlite;
using StupidDict.App.Assets;
using StupidDict.App.Localization;

namespace StupidDict.App.Speech;

/// <summary>
/// Resolves a headword to a playable MP3 file path. The pronunciation pack
/// lives either as a single SQLite database (word → MP3 blobs, the layout
/// Assets/AudioPackConverter produces) or as the legacy loose uk/us
/// directories of per-word files; the database wins so a manual import takes
/// effect even when loose files are still around. Database hits materialize
/// the blob into a uniquely named temp file — the platform players need a
/// real path, and the name never contains a headword, so Windows reserved
/// device names cannot occur on the play path. Every miss — no pack, missing
/// word, broken database — is a plain false for the TTS fallback; a faulting
/// store must never throw into the play path.
/// </summary>
public interface IAudioPackStore
{
    bool TryGetAudioFile(string word, SpeechAccent accent, out string file);

    /// <summary>
    /// Best-effort deletion of the temp files this store handed out. Called
    /// when playback stops: the players hold the file until they end (the
    /// MCI alias closes synchronously, the killed process releases the handle
    /// a beat later), so a delete that fails here just leaves a small stale
    /// file for the OS temp directory to sweep.
    /// </summary>
    void Cleanup();
}

public sealed class AudioPackStore(string looseDirectory, string databasePath) : IAudioPackStore
{
    // One playback at a time is the composite chain's invariant (a single MCI
    // alias, a single player process), so a single current temp file is the
    // whole story. Plays and stops arrive on the UI thread; no locking.
    private string? _currentTemp;
    private int _tempCounter;

    public bool TryGetAudioFile(string word, SpeechAccent accent, out string file) =>
        TryGetFromDatabase(word, accent, out file) ||
        TryGetFromLooseDirectory(word, accent, out file);

    public void Cleanup()
    {
        DeleteTemp(_currentTemp);
        _currentTemp = null;
    }

    private bool TryGetFromDatabase(string word, SpeechAccent accent, out string file)
    {
        file = "";
        try
        {
            if (!File.Exists(databasePath)) return false;
            var column = accent == SpeechAccent.British ? "uk" : "us";
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                // Pooling would keep the file handle open after Dispose and
                // block replacing the database later (see Core/SqliteConnections).
                Pooling = false,
            }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {column} FROM audio WHERE word = $word";
            command.Parameters.AddWithValue("$word", word.ToLowerInvariant());
            var blob = command.ExecuteScalar() as byte[];
            if (blob is null || blob.Length == 0) return false;
            file = WriteTempFile(blob);
            return true;
        }
        catch
        {
            // A database the app cannot read is a pack miss like any other:
            // system TTS takes over, same as an uninstalled pack.
            return false;
        }
    }

    private string WriteTempFile(byte[] bytes)
    {
        // The previous play has been stopped by now (each player's Play stops
        // its predecessor first), so the old temp file is free — and if its
        // handle lingers anyway, the delete just fails and the file waits for
        // the OS temp sweeper.
        DeleteTemp(_currentTemp);
        var temp = Path.Combine(Path.GetTempPath(),
            $"stupiddict-audio-{Environment.ProcessId}-{_tempCounter++}.mp3");
        File.WriteAllBytes(temp, bytes);
        return _currentTemp = temp;
    }

    private static void DeleteTemp(string? path)
    {
        try
        {
            if (path is not null) File.Delete(path);
        }
        catch
        {
            // best-effort by contract
        }
    }

    // Legacy layout support (packs extracted by app versions before the
    // database store): mapped reserved-device-name first, then the raw name,
    // which stays reachable on Unix and Windows 11.
    private bool TryGetFromLooseDirectory(string word, SpeechAccent accent, out string file)
    {
        file = "";
        var subdirectory = accent == SpeechAccent.British ? "uk" : "us";
        var stem = word.ToLowerInvariant() + ".mp3";
        var mappedName = ReservedDeviceNames.MapSegment(stem);
        var mapped = Path.Combine(looseDirectory, subdirectory, mappedName);
        if (File.Exists(mapped))
        {
            file = mapped;
            return true;
        }
        if (mappedName == stem) return false; // not a reserved stem: no raw-name fallback to probe
        var raw = Path.Combine(looseDirectory, subdirectory, stem);
        if (!File.Exists(raw)) return false;
        file = raw;
        return true;
    }

    /// <summary>
    /// Import-time gate for a user-provided database: must be readable as
    /// SQLite, structurally intact, and carry a non-empty audio table.
    /// Throws InvalidOperationException with a user-facing message otherwise.
    /// </summary>
    public static void ValidateDatabase(string path)
    {
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            connection.Open();
            using (var integrity = connection.CreateCommand())
            {
                integrity.CommandText = "PRAGMA integrity_check";
                if (integrity.ExecuteScalar() as string != "ok")
                    throw new InvalidOperationException(Translations.Instance.ImportInvalidDatabase);
            }
            using var count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM audio";
            if ((long)count.ExecuteScalar()! == 0)
                throw new InvalidOperationException(Translations.Instance.ImportInvalidDatabase);
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception)
        {
            // Not SQLite at all, unreadable, truncated — one user-facing line.
            throw new InvalidOperationException(Translations.Instance.ImportInvalidDatabase);
        }
    }
}
