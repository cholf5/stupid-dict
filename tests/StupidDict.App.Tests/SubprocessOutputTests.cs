using System.Diagnostics;
using StupidDict.App;
using Xunit;

namespace StupidDict.App.Tests;

/// <summary>
/// Process-hygiene tests for the shared probe reader (B-011): a one-shot
/// probe whose stdout never closes must be killed at the deadline instead of
/// hanging the caller forever, and whatever it buffered before the kill stays
/// parseable. Uses /bin/sleep and /bin/sh as stand-in probes — the mechanics
/// under test are pipe reads and kills, not `say` or `scutil` themselves;
/// Windows has neither binary, so those cases yield there.
/// </summary>
public sealed class SubprocessOutputTests
{
    private static ProcessStartInfo Probe(string shellCommand) => new()
    {
        FileName = "/bin/sh",
        ArgumentList = { "-c", shellCommand },
        UseShellExecute = false,
        RedirectStandardOutput = true,
        CreateNoWindow = true,
    };

    /// <summary>TC-001: a hung probe does not hang the reader.</summary>
    [Fact]
    public void HungProbeIsKilledAtTheDeadlineAndTheCallSettles()
    {
        if (OperatingSystem.IsWindows()) return; // no /bin/sleep there

        using var process = Process.Start(Probe("exec sleep 10"))!;
        var watch = Stopwatch.StartNew();

        var output = SubprocessOutput.ReadWithTimeout(process, timeoutMs: 300);

        watch.Stop();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(8), "a 10s probe must be killed near its 300ms deadline");
        Assert.Equal(string.Empty, output);
        Assert.True(process.HasExited, "TC-002: the hung probe must be killed, not left as an orphan");
    }

    /// <summary>A probe that writes and exits reads back normally.</summary>
    [Fact]
    public void ExitingProbeReturnsItsOutput()
    {
        if (OperatingSystem.IsWindows()) return;

        using var process = Process.Start(Probe("printf hello"))!;

        var output = SubprocessOutput.ReadWithTimeout(process, timeoutMs: 5000);

        Assert.Equal("hello", output);
        Assert.True(process.HasExited);
    }

    /// <summary>The kill keeps output buffered before the deadline parseable.</summary>
    [Fact]
    public void KilledProbeStillYieldsItsPartialOutput()
    {
        if (OperatingSystem.IsWindows()) return;

        using var process = Process.Start(Probe("printf partial; sleep 10"))!;

        var output = SubprocessOutput.ReadWithTimeout(process, timeoutMs: 300);

        Assert.Equal("partial", output);
        Assert.True(process.HasExited, "the probe must be killed after the deadline");
    }

    /// <summary>A process that starts but never writes and never exits is the hang shape: kill, empty result.</summary>
    [Fact]
    public void SilentHungProbeKillsCleanly()
    {
        if (OperatingSystem.IsWindows()) return;

        var info = new ProcessStartInfo
        {
            FileName = "/bin/sleep",
            ArgumentList = { "10" },
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        using var process = Process.Start(info)!;

        var output = SubprocessOutput.ReadWithTimeout(process, timeoutMs: 300);

        Assert.Equal(string.Empty, output);
        Assert.True(process.HasExited);
    }
}
