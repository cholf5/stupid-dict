using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace StupidDict.App.Update;

/// <summary>How this update lands. The only difference is who replaces the files.</summary>
internal enum UpdateApplyMode
{
    /// <summary>Windows installed copy → the Inno installer upgrades silently (it knows lock retries and rollback).</summary>
    Installer,

    /// <summary>Portable copy / macOS / Linux → the app swaps the shipped files itself, then restarts.</summary>
    PortableSwap,
}

/// <summary>
/// A fully downloaded, extracted and verified update, ready to apply.
/// <see cref="TargetRoot"/> is the directory the payload is overlaid onto (the
/// install folder, or the <c>.app</c> bundle on macOS); <see cref="BackupPath"/>
/// keeps every replaced file until the restarted process confirms the version.
/// </summary>
internal sealed record PreparedUpdate(
    string Tag,
    UpdateApplyMode Mode,
    string AssetPath,
    string TargetRoot,
    bool TargetIsBundle,
    string ExecutableName,
    string PayloadRoot,
    string BackupPath,
    string StagingDirectory);

/// <summary>
/// The portable self-update (P2): extract the new version into a staging
/// folder, then put the shipped files in place, keeping what was replaced in a
/// backup folder until the restarted process confirms the version.
///
/// Two shapes, because the NativeAOT payload is never a single file:
/// <list type="bullet">
/// <item>a macOS <c>.app</c> is swapped <b>whole</b> (stale binaries left by a
/// per-file overlay would break the code seal), carrying over the data files
/// the zip does not ship;</item>
/// <item>a plain install folder (Windows portable / Linux) gets a per-file
/// <b>overlay</b>, which leaves <c>dictionary.db</c>, the legacy <c>audio/</c>
/// tree and anything else the zip does not ship untouched.</item>
/// </list>
/// Every replaced file is restored from the backup if any step fails, and the
/// update journal lets the restarted process roll back after a crash mid-swap
/// (see <see cref="UpdateService"/>).
///
/// No relay script on purpose: Windows batch encoding (non-ASCII user names)
/// and its "wait for the process to exit" polling, macOS shebangs and xattrs —
/// each can silently break an upgrade. The hand-over is a command-line flag on
/// the new process (<see cref="UpdateRelaunchHandshake"/>) instead.
/// </summary>
internal class UpdateStager
{
    /// <summary>Staging folder name prefix (a hidden sibling; leftovers are cleaned at startup).</summary>
    internal const string StagingPrefix = ".stupiddict-update-";

    private readonly string _os;
    private readonly int _processId;
    private readonly Func<PreparedUpdate, bool>? _relaunchOverride;

    /// <summary>
    /// os injects "Windows" / "OSX" / "Linux" (tests pin the platform branch);
    /// processId only goes into the restart command line; relaunchOverride
    /// takes over the restart so a test never spawns a process.
    /// </summary>
    internal UpdateStager(string? os = null, int? processId = null,
        Func<PreparedUpdate, bool>? relaunchOverride = null)
    {
        _os = os ?? (OperatingSystem.IsWindows() ? "Windows"
            : OperatingSystem.IsMacOS() ? "OSX"
            : "Linux");
        _processId = processId ?? Environment.ProcessId;
        _relaunchOverride = relaunchOverride;
    }

    /// <summary>
    /// Extracts and locates the payload. Runs while the app is still up (writes
    /// only the staging folder). Thrown exceptions are presentable: the target
    /// is not writable (installed to a read-only location) or the archive does
    /// not contain this platform's product.
    /// </summary>
    internal virtual PreparedUpdate PreparePortable(string tag, string archivePath, string processPath)
    {
        var targetRoot = ResolveTargetRoot(processPath);
        var targetIsBundle = IsBundlePath(processPath);
        var staging = CreateStaging(tag, targetRoot, requireSibling: targetIsBundle);
        var executableName = Path.GetFileName(processPath);

        try
        {
            var extracted = Path.Combine(staging, "payload");
            ZipFile.ExtractToDirectory(archivePath, extracted, overwriteFiles: true);

            var payload = LocatePayload(extracted, targetIsBundle, executableName);
            return new PreparedUpdate(tag, UpdateApplyMode.PortableSwap, archivePath, targetRoot,
                targetIsBundle, executableName, payload, Path.Combine(staging, "backup"), staging);
        }
        catch
        {
            DeleteTree(staging);
            throw;
        }
    }

    /// <summary>Windows installed copy: nothing to extract, the installer does the work.</summary>
    internal virtual PreparedUpdate PrepareInstaller(string tag, string installerPath, string processPath) =>
        new(tag, UpdateApplyMode.Installer, installerPath, processPath, TargetIsBundle: false,
            Path.GetFileName(processPath), PayloadRoot: string.Empty, BackupPath: string.Empty,
            StagingDirectory: string.Empty);

    /// <summary>
    /// Applies the update. A macOS .app is swapped whole (a per-file overlay
    /// would leave stale binaries inside the bundle and break the code seal);
    /// a plain install folder gets a per-file overlay so everything the zip
    /// does not ship (dictionary.db, the legacy audio/ tree) survives. On
    /// failure it throws and guarantees the install root still holds the old
    /// version (the caller stays in ReadyToRestart and shows the reason).
    /// </summary>
    internal virtual void Apply(PreparedUpdate update)
    {
        if (update.Mode != UpdateApplyMode.PortableSwap)
        {
            throw new InvalidOperationException("Apply 只处理便携形态；已安装副本交给安装器");
        }

        if (update.TargetIsBundle)
        {
            ApplyBundle(update);
            return;
        }

        // Running executable last: until then the old app is still the version
        // that is running, and a failure anywhere rolls the overlay back.
        var files = EnumeratePayloadFiles(update.PayloadRoot)
            .OrderBy(f => string.Equals(f, update.ExecutableName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var applied = new List<(string Destination, string? Backup)>();
        try
        {
            foreach (var relative in files)
            {
                var source = Path.Combine(update.PayloadRoot, relative);
                var destination = Path.Combine(update.TargetRoot, relative);
                string? backup = Path.Combine(update.BackupPath, relative);
                if (File.Exists(destination))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    MoveOrCopy(destination, backup);
                }
                else
                {
                    backup = null;
                }

                applied.Add((destination, backup));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                MoveOrCopy(source, destination);
            }
        }
        catch
        {
            Rollback(applied);
            throw;
        }

        if (!OperatingSystem.IsWindows() && _os != "Windows")
        {
            EnsureUserExecute(Path.Combine(update.TargetRoot, update.ExecutableName));
        }
    }

    /// <summary>
    /// macOS bundle swap: move the old bundle into the backup folder, move the
    /// new one into place, put back the data files the app zip does not ship
    /// (a bundled dictionary.db), and re-seal when anything was added — that is
    /// exactly what scripts/package.sh does for the with-dictionary variant, and
    /// a bundle whose seal does not match its contents is what Gatekeeper calls
    /// "damaged". Running the app lives in the *old* bundle (already moved), so
    /// the new one is not in use at any point.
    /// </summary>
    private void ApplyBundle(PreparedUpdate update)
    {
        var name = Path.GetFileName(update.TargetRoot);
        Directory.CreateDirectory(update.BackupPath);
        var backupBundle = Path.Combine(update.BackupPath, name);
        DeleteTree(backupBundle);

        Directory.Move(update.TargetRoot, backupBundle);
        try
        {
            MoveDirectory(update.PayloadRoot, update.TargetRoot);
        }
        catch
        {
            // The new bundle did not land → put the old one back.
            MoveDirectory(backupBundle, update.TargetRoot);
            throw;
        }

        // The extraction is not guaranteed to restore the Unix mode; a bundle
        // whose payload lost its executable bit would not launch.
        EnsureBundleExecutables(update.TargetRoot);

        if (RestoreBundledUserData(backupBundle, update.TargetRoot))
        {
            ResignBundle(update.TargetRoot);
        }
    }

    /// <summary>Names of data files a with-dictionary package puts inside the bundle (see AppPaths).</summary>
    private static readonly string[] BundledUserData = ["dictionary.db", "audio"];

    /// <summary>
    /// Copies data the app zip does not carry back into the new bundle
    /// (Contents/Resources, where the seal allows data files). Returns whether
    /// anything was copied — only then does the shipped seal need redoing.
    /// </summary>
    private static bool RestoreBundledUserData(string oldBundle, string newBundle)
    {
        var restored = false;
        var oldResources = Path.Combine(oldBundle, "Contents", "Resources");
        var newResources = Path.Combine(newBundle, "Contents", "Resources");
        Directory.CreateDirectory(newResources);
        foreach (var name in BundledUserData)
        {
            var source = Path.Combine(oldResources, name);
            var destination = Path.Combine(newResources, name);
            try
            {
                if (File.Exists(source) && !File.Exists(destination))
                {
                    File.Copy(source, destination);
                    restored = true;
                }
                else if (Directory.Exists(source) && !Directory.Exists(destination))
                {
                    CopyDirectory(source, destination);
                    restored = true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Losing the bundled copy only means a later data download;
                // it must never fail the update.
                Console.Error.WriteLine($"[StupidDict] 恢复包内数据失败（{source}）：{ex.Message}");
            }
        }

        return restored;
    }

    /// <summary>Best-effort ad-hoc re-seal after adding files to a bundle (same command package.sh uses).</summary>
    private static void ResignBundle(string bundle)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo("codesign", $"--force -s - \"{bundle}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            process?.WaitForExit(30_000);
        }
        catch (Exception ex)
        {
            // Resealing is a nicety: without it the app still starts locally
            // (Gatekeeper only re-checks a quarantined bundle).
            Console.Error.WriteLine($"[StupidDict] 重新封签失败（{bundle}）：{ex.Message}");
        }
    }

    /// <summary>
    /// Starts the new version and lets the caller exit. The new process carries
    /// <c>--wait-for-exit &lt;pid&gt;</c>: it waits for this process to really
    /// exit before claiming the single-instance lock — otherwise it would be
    /// turned away and the app would look like it disappeared.
    /// </summary>
    internal virtual bool Relaunch(PreparedUpdate update)
    {
        if (_relaunchOverride is { } relaunch)
        {
            return relaunch(update);
        }

        if (update.Mode != UpdateApplyMode.PortableSwap)
        {
            return false;
        }

        try
        {
            ProcessStartInfo startInfo;
            if (update.TargetIsBundle)
            {
                // macOS: the "executable" is the .app; `open` restores Dock icon
                // and activation semantics, and passes our flag through --args.
                startInfo = new ProcessStartInfo("open",
                    $"-n \"{update.TargetRoot}\" --args {UpdateRelaunchHandshake.WaitForExitOption} {_processId}")
                {
                    UseShellExecute = false,
                };
            }
            else
            {
                startInfo = new ProcessStartInfo(Path.Combine(update.TargetRoot, update.ExecutableName))
                {
                    Arguments = $"{UpdateRelaunchHandshake.WaitForExitOption} {_processId}",
                    WorkingDirectory = update.TargetRoot,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
            }

            return Process.Start(startInfo) is not null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception
                                       or IOException)
        {
            Console.Error.WriteLine($"[StupidDict] 启动新版本失败（{update.TargetRoot}）：{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Restores every file the journal's backup folder holds and moves the
    /// half-applied new files aside. Used by <see cref="UpdateService"/> when
    /// the restarted process still reports the old version. Best-effort:
    /// returns false when the backup is missing or restoring failed.
    /// </summary>
    internal static bool TryRollback(string backupPath, string targetRoot)
    {
        if (string.IsNullOrEmpty(backupPath) || !Directory.Exists(backupPath))
        {
            return false;
        }

        try
        {
            // The running executable cannot be deleted on Windows; park the
            // half-applied files under a discard folder instead of deleting.
            var discard = Path.Combine(backupPath + "-discard", Guid.NewGuid().ToString("N"));

            // Bundle swap: the backup holds the whole .app, so rollback is one
            // directory move each way (a per-file walk would restore the right
            // files at the wrong paths).
            if (targetRoot.EndsWith(".app", StringComparison.Ordinal))
            {
                var bundle = Directory.EnumerateDirectories(backupPath)
                    .FirstOrDefault(d => d.EndsWith(".app", StringComparison.Ordinal));
                if (bundle is not null)
                {
                    if (Directory.Exists(targetRoot))
                    {
                        Directory.CreateDirectory(discard);
                        MoveDirectory(targetRoot, Path.Combine(discard, Path.GetFileName(targetRoot)));
                    }

                    MoveDirectory(bundle, targetRoot);
                    return true;
                }
            }

            foreach (var source in Directory.EnumerateFiles(backupPath, "*", SearchOption.AllDirectories).ToList())
            {
                var relative = Path.GetRelativePath(backupPath, source);
                var destination = Path.Combine(targetRoot, relative);
                if (File.Exists(destination))
                {
                    var parked = Path.Combine(discard, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(parked)!);
                    MoveOrCopy(destination, parked);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                MoveOrCopy(source, destination);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[StupidDict] 回滚更新失败（{backupPath}）：{ex.Message}");
            return false;
        }
    }

    /// <summary>Deletes the staging/backup folder (best-effort; the caller ignores leftover disk).</summary>
    internal static void DeleteStaging(string? stagingDirectory)
    {
        if (string.IsNullOrEmpty(stagingDirectory))
        {
            return;
        }

        DeleteTree(stagingDirectory);
    }

    /// <summary>Removes staging folders a previous run left behind (startup; failures only log).</summary>
    internal static void CleanStaleStaging(string processPath)
    {
        var directory = Path.GetDirectoryName(ResolveTargetRoot(processPath));
        if (directory is not null)
        {
            DeleteStagingFolders(directory);
        }

        DeleteStagingFolders(Path.GetTempPath());
    }

    private static void DeleteStagingFolders(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            foreach (var stale in Directory.EnumerateDirectories(directory, StagingPrefix + "*"))
            {
                DeleteTree(stale);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable directory counts as "nothing to clean".
        }
    }

    /// <summary>
    /// The install root that gets overlaid: on macOS the .app bundle (the
    /// process path lives inside it), everywhere else the executable's folder.
    /// </summary>
    internal static string ResolveTargetRoot(string processPath)
    {
        if (!IsBundlePath(processPath))
        {
            return Path.GetDirectoryName(processPath) ?? processPath;
        }

        var directory = new DirectoryInfo(Path.GetDirectoryName(processPath)!);
        while (directory is not null && !directory.Name.EndsWith(".app", StringComparison.Ordinal))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? processPath;
    }

    internal static bool IsBundlePath(string processPath) =>
        processPath.Contains(".app" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
        || processPath.Contains(".app/", StringComparison.Ordinal);

    /// <summary>
    /// Payload layout check: a macOS target needs exactly one <c>.app</c>
    /// directory, a plain target needs the running executable's file name at the
    /// archive root. No guessing and no "find something runnable" — a mismatch
    /// means the wrong asset was picked (e.g. a macOS zip on Linux).
    /// </summary>
    internal static string LocatePayload(string extractedDirectory, bool targetIsBundle, string executableName)
    {
        if (targetIsBundle)
        {
            var bundles = Directory.EnumerateDirectories(extractedDirectory)
                .Where(d => d.EndsWith(".app", StringComparison.Ordinal))
                .ToArray();
            if (bundles.Length != 1)
            {
                throw new InvalidDataException(
                    $"expected exactly one .app bundle in the archive, found {bundles.Length}");
            }

            return bundles[0];
        }

        var executable = Path.Combine(extractedDirectory, executableName);
        if (!File.Exists(executable))
        {
            throw new InvalidDataException($"archive does not contain {executableName} at its root");
        }

        EnsureExecutablePayload(executable);
        return extractedDirectory;
    }

    /// <summary>
    /// Payload magic: Windows is PE (MZ), Linux is ELF. The downloader only
    /// proved "this is a zip"; this layer proves "the zip contains this
    /// platform's program".
    /// </summary>
    private static void EnsureExecutablePayload(string path)
    {
        Span<byte> magic = stackalloc byte[4];
        int read;
        using (var stream = File.OpenRead(path))
        {
            read = stream.ReadAtLeast(magic, magic.Length, throwOnEndOfStream: false);
        }

        var isPe = read >= 2 && magic[0] == 0x4D && magic[1] == 0x5A;
        var isElf = read >= 4 && magic[0] == 0x7F && magic[1] == 0x45 && magic[2] == 0x4C && magic[3] == 0x46;
        if (!isPe && !isElf)
        {
            throw new InvalidDataException("archive payload is not a Windows PE or Linux ELF executable");
        }
    }

    /// <summary>
    /// Only fixes "the extraction dropped the executable bit". The zip carries
    /// the Unix mode in its external attributes and re-setting permissions
    /// unnecessarily changes file metadata — macOS's ad-hoc code seal covers
    /// the attributes it lists, and a churned one can read as "damaged".
    /// </summary>
    private static void EnsureBundleExecutables(string bundle)
    {
        var macOs = Path.Combine(bundle, "Contents", "MacOS");
        if (!Directory.Exists(macOs))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(macOs, "*", SearchOption.AllDirectories))
        {
            try
            {
                EnsureUserExecute(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A mode bit cannot fail the update: the app may still start
                // (the zip usually carries the mode anyway).
                Console.Error.WriteLine($"[StupidDict] 设置可执行位失败（{file}）：{ex.Message}");
            }
        }
    }

    private static void EnsureUserExecute(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var mode = File.GetUnixFileMode(path);
        if ((mode & UnixFileMode.UserExecute) != 0)
        {
            return;
        }

        File.SetUnixFileMode(path, mode | UnixFileMode.UserRead | UnixFileMode.UserExecute);
    }

    private static IEnumerable<string> EnumeratePayloadFiles(string payloadRoot) =>
        Directory.EnumerateFiles(payloadRoot, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(payloadRoot, f));

    private static string CreateStaging(string tag, string targetRoot, bool requireSibling)
    {
        // Sibling of the install root first: same volume, so the moves are
        // metadata operations. An unwritable location (an app in /Applications
        // owned by root, a read-only mount) falls back to the temp directory
        // and the moves degrade to copy+delete.
        var candidates = new List<string>();
        var parent = Path.GetDirectoryName(targetRoot.TrimEnd(Path.DirectorySeparatorChar));
        if (!string.IsNullOrEmpty(parent))
        {
            candidates.Add(Path.Combine(parent, StagingPrefix + Sanitize(tag)));
        }

        // A bundle is swapped whole, and the running process lives inside the
        // old one: falling back to a cross-volume copy would delete the files
        // the running app still maps. Refusing instead lands in the caller's
        // "download + reveal, replace by hand" path.
        if (!requireSibling)
        {
            candidates.Add(Path.Combine(Path.GetTempPath(), $"{StagingPrefix}{Sanitize(tag)}-{Environment.ProcessId}"));
        }

        foreach (var candidate in candidates)
        {
            try
            {
                DeleteTree(candidate);
                Directory.CreateDirectory(candidate);
                return candidate;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Try the next candidate.
            }
        }

        throw new IOException($"staging directory is not writable: {string.Join(", ", candidates)}");
    }

    private static void Rollback(List<(string Destination, string? Backup)> applied)
    {
        // Reverse order, so a partially written file is undone before the one
        // it came from. The new file is parked (not deleted): on Windows the
        // running executable cannot be deleted.
        for (var i = applied.Count - 1; i >= 0; i--)
        {
            var (destination, backup) = applied[i];
            try
            {
                if (File.Exists(destination))
                {
                    var parked = destination + ".new";
                    TryDelete(parked);
                    File.Move(destination, parked);
                }

                if (backup is not null && File.Exists(backup))
                {
                    File.Move(backup, destination);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"[StupidDict] 回滚 {destination} 失败：{ex.Message}");
            }
        }
    }

    private static void MoveOrCopy(string source, string destination)
    {
        try
        {
            File.Move(source, destination, overwrite: false);
        }
        catch (IOException) when (!File.Exists(destination))
        {
            // Cross-volume move (staging fell back to temp): copy then delete.
            File.Copy(source, destination, overwrite: false);
            TryDelete(source);
        }
    }

    /// <summary>Moves a directory, degrading to copy+delete across volumes (the temp-staging fallback).</summary>
    private static void MoveDirectory(string source, string destination)
    {
        try
        {
            Directory.Move(source, destination);
        }
        catch (IOException) when (!Directory.Exists(destination))
        {
            CopyDirectory(source, destination);
            DeleteTree(source);
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(target, File.GetUnixFileMode(file));
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover .new file is only disk use.
        }
    }

    private static void DeleteTree(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Stale staging is only disk use; the next update overwrites it.
        }
    }

    private static string Sanitize(string tag) =>
        new(tag.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_').ToArray());
}
