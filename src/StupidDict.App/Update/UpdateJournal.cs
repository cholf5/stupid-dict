using System.Text.Json;
using System.Text.Json.Serialization;

namespace StupidDict.App.Update;

/// <summary>
/// The on-disk credential that "an update is being applied". The old process
/// writes it before replacing files; the restarted process reads it: a
/// matching version means the update succeeded (clean up the backup, say "已更新
/// 到 vX"), a mismatch means the swap did not take effect (the app is still the
/// old version, the backup is intact). It must be on disk rather than an
/// in-memory flag because the process dies in the middle of an update, and any
/// in-memory promise dies with it.
/// </summary>
internal sealed class UpdateJournalRecord
{
    /// <summary>The version this update was about to install ("vX.Y.Z").</summary>
    public string? PendingVersion { get; set; }

    /// <summary>Where the replaced files were moved (<c>&lt;staging&gt;/backup</c>); deleted on success.</summary>
    public string? BackupPath { get; set; }

    /// <summary>The install root that was being replaced (shown in the failure notice).</summary>
    public string? TargetPath { get; set; }

    /// <summary>The staging folder that holds the backup (deleted once the restarted process confirms the new version).</summary>
    public string? StagingPath { get; set; }

    /// <summary>When the update started (diagnostics only; not part of the decision).</summary>
    public DateTimeOffset? StagedUtc { get; set; }
}

/// <summary>update-state.json's JSON source generation context (AOT rule: no reflection serialization).</summary>
[JsonSerializable(typeof(UpdateJournalRecord))]
internal sealed partial class UpdateJournalJsonContext : JsonSerializerContext;

/// <summary>
/// Reads and writes the update journal (<c>&lt;data&gt;/update-state.json</c>).
/// Everything is best-effort: losing this file at worst hides the "update
/// succeeded" notice — it must never block startup or an update.
/// </summary>
internal sealed class UpdateJournal
{
    private static readonly UpdateJournalJsonContext Context = new(new JsonSerializerOptions
    {
        WriteIndented = true,
    });

    private readonly string _path;

    internal UpdateJournal(string path) => _path = path;

    internal string FilePath => _path;

    /// <summary>The record left by the previous update; null when there is none, or it is unreadable.</summary>
    internal UpdateJournalRecord? Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            using var stream = File.OpenRead(_path);
            return JsonSerializer.Deserialize(stream, Context.UpdateJournalRecord);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[StupidDict] 更新日志读取失败（{_path}）：{ex.Message}");
            return null;
        }
    }

    /// <summary>Writes the record (called before replacing files: if the process dies mid-swap, the restarted instance learns what happened).</summary>
    internal void Save(UpdateJournalRecord record)
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Temp file then rename: half a JSON document is as unreadable as
            // "no record", but would still poison the next decision.
            var temporary = _path + ".tmp";
            using (var stream = File.Create(temporary))
            {
                JsonSerializer.Serialize(stream, record, Context.UpdateJournalRecord);
            }

            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Failing to record must not block the update: worst case the
            // success notice does not appear.
            Console.Error.WriteLine($"[StupidDict] 更新日志写入失败（{_path}）：{ex.Message}");
        }
    }

    internal void Clear()
    {
        try
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[StupidDict] 更新日志清理失败（{_path}）：{ex.Message}");
        }
    }
}
