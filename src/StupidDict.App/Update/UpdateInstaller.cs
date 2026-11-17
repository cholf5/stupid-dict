using System.ComponentModel;
using System.Diagnostics;

namespace StupidDict.App.Update;

/// <summary>
/// The Windows silent-upgrade executor: starts the Inno Setup installer with
/// <c>/SILENT</c>; it replaces the running files, re-launches the app at the
/// end of its <c>[Run]</c> section, and this process exits so the file locks
/// are released.
///
/// Why this does not count as self-replacing: the installer does the file
/// work — it knows about lock retries, half-copy rollback and old-version
/// cleanup. Replacing a running exe plus its native libraries by hand is a
/// minefield (locks, seals, Gatekeeper); the app only downloads, starts the
/// installer and exits. Portable copies and macOS/Linux use
/// <see cref="UpdateStager"/> instead.
/// </summary>
internal sealed class UpdateInstaller
{
    /// <summary>
    /// Silent-upgrade switches: no wizard, no confirmation; <c>/CLOSEAPPLICATIONS</c>
    /// lets Restart Manager close instances still holding files (the app exits
    /// itself too — belt and braces); <c>/NORESTART</c> keeps the machine up —
    /// the installer script re-launches the app itself.
    /// </summary>
    internal const string SilentArguments = "/SILENT /CLOSEAPPLICATIONS /NORESTART /SUPPRESSMSGBOXES";

    private readonly Func<string, string, bool>? _startOverride;

    /// <summary>startOverride lets a test take over "what was started, with which arguments" (true = started) without a real process.</summary>
    internal UpdateInstaller(Func<string, string, bool>? startOverride = null) => _startOverride = startOverride;

    /// <summary>
    /// Starts the silent install. true = the installer process started (the
    /// caller then exits); false = it could not start (blocked file, permissions,
    /// missing path), in which case the caller falls back to revealing the
    /// downloaded file.
    /// </summary>
    internal bool TryStartSilent(string installerPath)
    {
        if (_startOverride is { } start)
        {
            return start(installerPath, SilentArguments);
        }

        if (!File.Exists(installerPath))
        {
            Console.Error.WriteLine($"[StupidDict] 安装器不存在：{installerPath}");
            return false;
        }

        try
        {
            // UseShellExecute=false + CreateNoWindow: no console flash and no
            // file-association lookup (a hijacked "open .exe with" handler must
            // not get a say). The installer shows its own progress window.
            using var process = Process.Start(new ProcessStartInfo(installerPath, SilentArguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(installerPath) ?? Environment.CurrentDirectory,
            });
            if (process is not null)
            {
                return true;
            }

            Console.Error.WriteLine($"[StupidDict] 安装器未能启动：{installerPath}");
            return false;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException
                                       or InvalidOperationException)
        {
            Console.Error.WriteLine($"[StupidDict] 启动安装器失败（{installerPath}）：{ex.Message}");
            return false;
        }
    }
}
