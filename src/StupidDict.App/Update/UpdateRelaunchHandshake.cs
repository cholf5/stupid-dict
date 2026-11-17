using System.ComponentModel;
using System.Diagnostics;

namespace StupidDict.App.Update;

/// <summary>
/// The self-update hand-over: after swapping the files, the old process starts
/// the new version with <c>--wait-for-exit &lt;pid&gt;</c> and exits. The new
/// process must wait for the old one to really die before claiming the
/// single-instance lock — otherwise it cannot claim it, only signals the old
/// instance and quits, and the user sees "the app vanished after updating".
/// The wait is bounded: if the old process hangs in its shutdown, the new one
/// starts anyway and at worst is turned away by the single-instance guard (the
/// new files are already on disk, so the next launch uses them).
/// </summary>
internal static class UpdateRelaunchHandshake
{
    internal const string WaitForExitOption = "--wait-for-exit";

    /// <summary>Upper bound for the wait. A normal exit is far below this.</summary>
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Reads the process id to wait for from the command line. Missing/non-positive/not this option → false (a normal start).</summary>
    internal static bool TryParse(string[]? args, out int processId)
    {
        processId = 0;
        if (args is null)
        {
            return false;
        }

        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], WaitForExitOption, StringComparison.Ordinal)
                && int.TryParse(args[i + 1], out var pid) && pid > 0)
            {
                processId = pid;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Waits for the process to exit (a missing/already-exited process returns
    /// immediately). Every exception is swallowed — this is a step on the
    /// startup path, and not waiting only means one extra instance is turned
    /// away by the guard, never that the app fails to start.
    /// </summary>
    internal static void WaitForExit(int processId, TimeSpan? timeout = null)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            process.WaitForExit((int)(timeout ?? DefaultTimeout).TotalMilliseconds);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            // The process is already gone (the normal path) or not accessible:
            // just continue starting.
        }
    }

    /// <summary>Startup helper: waits for the old process when the command line asks for it.</summary>
    internal static void WaitIfRequested(string[] args)
    {
        if (TryParse(args, out var processId))
        {
            WaitForExit(processId);
        }
    }
}
