using System.Diagnostics;
using StupidDict.App.Settings;

namespace StupidDict.App.Update;

/// <summary>The update state machine. Checking and downloading/applying share one main line: the prompt, the tray and the settings dialog all read it.</summary>
public enum UpdateState
{
    /// <summary>No check has run in this process.</summary>
    Idle,

    Checking,

    UpToDate,

    /// <summary>A newer release exists (LastResult.LatestVersion / ReleaseUrl).</summary>
    Available,

    /// <summary>Downloading / verifying / extracting (progress goes to the caller's IProgress).</summary>
    Downloading,

    /// <summary>The package is ready; waiting for "restart and update".</summary>
    ReadyToRestart,

    /// <summary>Replacing files and restarting (this process then exits).</summary>
    Restarting,

    Failed,
}

/// <summary>What the previous update left behind, as read at startup.</summary>
public enum UpdateStartupOutcome
{
    /// <summary>No pending update record.</summary>
    None,

    /// <summary>The version matches: the update succeeded (the backup was cleaned up).</summary>
    Upgraded,

    /// <summary>The record promised vX but the app still runs an older version: the swap did not take; the old files were restored from the backup when possible.</summary>
    ApplyFailed,
}

/// <summary>The download succeeded but the install root is not writable → fell back to "downloaded + revealed in the file manager"; the path is <see cref="ArchivePath"/>.</summary>
internal sealed class UpdateStageUnavailableException(string archivePath, Exception inner)
    : Exception(inner.Message, inner)
{
    internal string ArchivePath { get; } = archivePath;
}

/// <summary>The startup interpretation of the journal, for the "已更新到 vX" / "上次更新未生效" prompt.</summary>
public sealed record UpdateStartupNotice(
    UpdateStartupOutcome Outcome,
    string? Version,
    string? BackupPath,
    bool RolledBack);

/// <summary>
/// The process-wide update service: the settings dialog, the tray menu and the
/// update prompt share one instance, so whichever starts a check or a download
/// the others follow.
/// <para>
/// Responsibilities: checking (including the startup check's throttle and its
/// timestamp on disk), downloading + SHA-256 verification + extraction,
/// applying and restarting, and interpreting the previous update's journal at
/// startup (success notice / failed-swap rollback notice).
/// </para>
/// <para>
/// How it lands depends on the shape: a Windows installed copy hands over to
/// the Inno installer (<see cref="UpdateInstaller"/>, which knows lock retries
/// and half-copy rollback); portable copies, macOS and Linux overlay the
/// shipped files themselves (<see cref="UpdateStager"/>), then start the new
/// version and exit.
/// </para>
/// </summary>
public sealed class UpdateService
{
    private readonly UpdateChecker _checker;
    private readonly AppSettings _settings;
    private readonly Action _saveSettings;
    private readonly UpdateDownloadService _downloader;
    private readonly UpdateInstaller _installer;
    private readonly UpdateStager _stager;
    private readonly UpdateJournal _journal;
    private readonly string _processPath;
    private readonly Action<string> _openUrl;
    private readonly Action<string> _revealFile;
    private readonly bool _windowsInstalledCopy;

    internal UpdateService(UpdateChecker checker, AppSettings settings, Action? saveSettings = null,
        UpdateDownloadService? downloader = null, Action<string>? openUrl = null,
        UpdateInstaller? installer = null, bool? windowsInstalledCopy = null,
        UpdateStager? stager = null, UpdateJournal? journal = null, string? processPath = null,
        Action<string>? revealFile = null)
    {
        _checker = checker;
        _settings = settings;
        // The timestamp is written through the same atomic settings save the
        // rest of the app uses; tests inject a recorder instead of disk.
        _saveSettings = saveSettings ?? (() => SettingsService.Save(settings));
        _downloader = downloader ?? new UpdateDownloadService();
        _installer = installer ?? new UpdateInstaller();
        _stager = stager ?? new UpdateStager();
        _journal = journal ?? new UpdateJournal(Path.Combine(AppPaths.DataDirectory, "update-state.json"));
        _processPath = processPath ?? Environment.ProcessPath ?? string.Empty;
        // The upgrade route is fixed once per process: the executable's
        // location never moves during a run, so there is no point recomputing
        // it per download. Tests inject it, or they would follow whether the
        // build machine happens to have the app installed.
        _windowsInstalledCopy = windowsInstalledCopy ?? UpdateAssets.IsInstalledCopy();
        // The default opens the URL through the shell (Process.Start +
        // UseShellExecute, cross-platform); tests inject a recorder.
        _openUrl = openUrl ?? (url =>
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[StupidDict] 打开发布页失败（{url}）：{ex.Message}");
            }
        });
        _revealFile = revealFile ?? RevealInFileManager;

        StartupNotice = ReconcileStartup();
    }

    internal UpdateState State { get; private set; } = UpdateState.Idle;

    /// <summary>This service's comparison base (v prefix + three segments) — the assembly version, injectable in tests.</summary>
    internal string CurrentVersion => _checker.Version;

    /// <summary>The last check's raw result (null while Idle; LatestVersion/ReleaseUrl are set on Available).</summary>
    internal UpdateCheckResult? LastResult { get; private set; }

    /// <summary>The prepared update (non-null while ReadyToRestart / Restarting).</summary>
    internal PreparedUpdate? Prepared { get; private set; }

    /// <summary>The last download/verify/apply failure, ready to display (a check failure lives in <see cref="LastResult"/>).</summary>
    internal string? LastError { get; private set; }

    /// <summary>What the previous update left behind: shows "已更新到 vX" or "上次更新未生效".</summary>
    internal UpdateStartupNotice StartupNotice { get; }

    /// <summary>State change notification (may fire on any thread — subscribers marshal to the UI thread themselves).</summary>
    internal event Action? Changed;

    /// <summary>This process is the copy in the installer's directory (not an extracted portable folder) — upgrades go through the installer.</summary>
    internal bool CanSilentUpgrade => _windowsInstalledCopy;

    /// <summary>The files are replaced and a restart is due. Subscribers exit through the normal lifetime (skipping it makes the new instance fight the guard).</summary>
    internal event Action? RestartRequested;

    /// <summary>The page a browser should open for "there is an update": this check's URL first, the release list otherwise.</summary>
    internal void OpenReleasePage() => _openUrl(
        State == UpdateState.Available && LastResult?.ReleaseUrl is { } url
            ? url
            : UpdateAssets.ReleasesPageUrl);

    /// <summary>
    /// Downloads the current new version's platform asset → SHA-256 verify →
    /// extract into a staging folder next to the install root. Success moves to
    /// ReadyToRestart; failure rethrows for the caller to display and falls
    /// back to Available (the button still works).
    /// </summary>
    internal async Task DownloadAsync(IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var tag = AvailableTag() ?? throw new InvalidOperationException("no update available");
        var assetName = UpdateAssets.PickAssetName(tag, _windowsInstalledCopy);
        LastError = null;
        SetState(UpdateState.Downloading, LastResult);
        try
        {
            var path = await _downloader.DownloadVerifiedAsync(tag, assetName, progress, ct);
            Prepared = _windowsInstalledCopy
                ? _stager.PrepareInstaller(tag, path, _processPath)
                : PreparePortableOrReveal(tag, path);
            SetState(UpdateState.ReadyToRestart, LastResult);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Prepared = null;
            SetState(UpdateState.Available, LastResult);
            throw;
        }
    }

    /// <summary>
    /// Extracts into the staging folder; when the install root is not writable
    /// (/Applications without rights, enterprise policy, a read-only mount) it
    /// falls back to the older behaviour: reveal the downloaded package in the
    /// file manager so the user can replace it by hand. Self-update capability
    /// must not turn "cannot install here" into "cannot update at all".
    /// </summary>
    private PreparedUpdate PreparePortableOrReveal(string tag, string archivePath)
    {
        try
        {
            return _stager.PreparePortable(tag, archivePath, _processPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The package exists but cannot be installed here: show it so the
            // user can replace the files by hand (the manual route is the
            // documented fallback, same as the data downloads).
            try
            {
                _revealFile(archivePath);
            }
            catch (Exception revealError)
            {
                Console.Error.WriteLine($"[StupidDict] 打开更新包所在文件夹失败：{revealError.Message}");
            }

            throw new UpdateStageUnavailableException(archivePath, ex);
        }
    }

    /// <summary>Shows the downloaded file in the platform file manager (best-effort: a failure only logs).</summary>
    private static void RevealInFileManager(string path)
    {
        if (OperatingSystem.IsMacOS())
        {
            Process.Start(new ProcessStartInfo("open", $"-R \"{path}\"") { UseShellExecute = false });
            return;
        }

        // Windows and Linux have no portable "select this file" call: open the
        // containing folder and let the user pick it out.
        var folder = Path.GetDirectoryName(path) ?? path;
        Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
    }

    /// <summary>
    /// Applies and restarts: write the journal → (installer / file overlay) →
    /// start the new version → raise <see cref="RestartRequested"/>. A failed
    /// swap stays in ReadyToRestart and rethrows (the old version is still
    /// running, the user can retry).
    /// </summary>
    internal void RestartAndApply()
    {
        if (Prepared is not { } prepared || State != UpdateState.ReadyToRestart)
        {
            throw new InvalidOperationException("no prepared update");
        }

        LastError = null;
        SetState(UpdateState.Restarting, LastResult);
        _journal.Save(new UpdateJournalRecord
        {
            PendingVersion = prepared.Tag,
            BackupPath = prepared.BackupPath,
            TargetPath = prepared.TargetRoot,
            StagingPath = prepared.StagingDirectory,
            StagedUtc = DateTimeOffset.UtcNow,
        });

        if (prepared.Mode == UpdateApplyMode.Installer)
        {
            try
            {
                if (!_installer.TryStartSilent(prepared.AssetPath))
                {
                    throw new InvalidOperationException("installer did not start");
                }
            }
            catch (Exception ex)
            {
                _journal.Clear();
                LastError = ex.Message;
                SetState(UpdateState.ReadyToRestart, LastResult);
                throw;
            }
        }
        else
        {
            try
            {
                _stager.Apply(prepared);
            }
            catch (Exception ex)
            {
                // Nothing was swapped (or it was rolled back): the disk still
                // holds the old version, so clear the journal — otherwise the
                // next start would report a failure that did not happen.
                _journal.Clear();
                LastError = ex.Message;
                SetState(UpdateState.ReadyToRestart, LastResult);
                throw;
            }

            if (!_stager.Relaunch(prepared))
            {
                // The files are the new ones, only the launch failed. Keep the
                // journal: a manual restart will match the version and count as
                // success.
                LastError = "updated files are in place but the new version did not start";
                SetState(UpdateState.ReadyToRestart, LastResult);
                throw new InvalidOperationException(LastError);
            }
        }

        RestartRequested?.Invoke();
    }

    private string? AvailableTag() => LastResult?.LatestVersion;

    /// <summary>
    /// Checks right now. Both success and failure stamp
    /// <c>LastUpdateCheckUtc</c>: the throttle means "a check has been made",
    /// and retrying an unreachable network on every start only wastes time (the
    /// next scheduled/manual check still happens).
    /// </summary>
    internal async Task<UpdateCheckResult> CheckNowAsync(CancellationToken ct = default)
    {
        SetState(UpdateState.Checking, null);
        var result = await _checker.CheckAsync(ct);
        SetState(result.Outcome switch
        {
            UpdateCheckOutcome.UpdateAvailable => UpdateState.Available,
            UpdateCheckOutcome.UpToDate => UpdateState.UpToDate,
            _ => UpdateState.Failed,
        }, result);

        try
        {
            _settings.LastUpdateCheckUtc = DateTimeOffset.UtcNow;
            _saveSettings();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[StupidDict] 更新检查时间戳落盘失败：{ex.Message}");
        }

        return result;
    }

    /// <summary>The startup check is allowed only when the last one is at least <paramref name="minimumInterval"/> old (or never ran); a manual check ignores this.</summary>
    internal bool ShouldAutoCheck(TimeSpan minimumInterval) =>
        _settings.LastUpdateCheckUtc is not { } last || DateTimeOffset.UtcNow - last >= minimumInterval;

    /// <summary>
    /// Interprets the journal the previous update left: matching version →
    /// success (clear the journal, delete the backup); mismatch → the swap did
    /// not take, restore the backup and keep the path for the notice. No
    /// journal / unreadable journal → nothing happened.
    /// </summary>
    private UpdateStartupNotice ReconcileStartup()
    {
        if (_journal.Load() is not { PendingVersion: { } pending } record)
        {
            // No journal means no update was ever applied: any staging folder
            // is an orphan from a cancelled download/restart. Clearing it here
            // (rather than before construction) keeps a live journal's backup
            // alive for the rollback below.
            UpdateStager.CleanStaleStaging(_processPath);
            return new UpdateStartupNotice(UpdateStartupOutcome.None, null, null, false);
        }

        if (string.Equals(Normalize(pending), Normalize(CurrentVersion), StringComparison.OrdinalIgnoreCase))
        {
            _journal.Clear();
            UpdateStager.DeleteStaging(record.StagingPath ?? ParentOfBackup(record.BackupPath));
            return new UpdateStartupNotice(UpdateStartupOutcome.Upgraded, pending, null, false);
        }

        var rolledBack = record.TargetPath is { } target
                         && UpdateStager.TryRollback(record.BackupPath ?? string.Empty, target);
        _journal.Clear();
        if (rolledBack)
        {
            UpdateStager.DeleteStaging(record.StagingPath ?? ParentOfBackup(record.BackupPath));
        }
        else
        {
            // Keep the backup so the notice's path is real; everything else
            // (orphans) can go.
            Console.Error.WriteLine($"[StupidDict] 更新未生效且无法自动回滚，保留备份：{record.BackupPath}");
        }

        return new UpdateStartupNotice(UpdateStartupOutcome.ApplyFailed, pending, record.BackupPath, rolledBack);
    }

    /// <summary>Old journal files recorded only the backup; the staging folder is its parent (<c>&lt;staging&gt;/backup</c>).</summary>
    private static string? ParentOfBackup(string? backupPath) =>
        string.IsNullOrEmpty(backupPath) ? null : Path.GetDirectoryName(backupPath);

    private static string Normalize(string version) => version.TrimStart('v', 'V');

    private void SetState(UpdateState state, UpdateCheckResult? result)
    {
        State = state;
        LastResult = result;
        Changed?.Invoke();
    }
}
