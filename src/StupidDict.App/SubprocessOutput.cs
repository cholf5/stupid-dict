using System.Diagnostics;

namespace StupidDict.App;

/// <summary>
/// One-shot probe processes (macOS `say -v ?` voice enumeration, `scutil --proxy`)
/// read their stdout to the end — a process that hangs (not merely slow) would
/// hang the reader forever and survive as an orphan. ReadWithTimeout bounds the
/// wait and kills on the deadline so the pipe closes, the read completes and no
/// child outlives the call. Parsing stays with the caller: whatever was buffered
/// before the kill is still handed back.
/// </summary>
internal static class SubprocessOutput
{
    public static string? ReadWithTimeout(Process process, int timeoutMs)
    {
        var read = process.StandardOutput.ReadToEndAsync();
        try
        {
            if (!read.Wait(timeoutMs))
            {
                // Past the deadline the probe is treated as hung: kill the whole
                // tree (a shell probe's grandchildren keep the pipe open) so the
                // read completes with the buffered bytes and no orphan survives.
                try { process.Kill(entireProcessTree: true); }
                catch
                {
                    // already gone
                }

                try { read.Wait(2000); }
                catch
                {
                    // a read that will not settle leaves nothing to parse
                }
            }
        }
        catch
        {
            // a faulted read (broken pipe) parses as nothing; the process still
            // must not outlive this call
            try { process.Kill(entireProcessTree: true); }
            catch
            {
                // already gone
            }
        }

        if (!process.WaitForExit(2000))
        {
            try { process.Kill(entireProcessTree: true); }
            catch
            {
                // already gone
            }

            process.WaitForExit(2000);
        }

        return read.Status == TaskStatus.RanToCompletion ? read.Result : null;
    }
}
