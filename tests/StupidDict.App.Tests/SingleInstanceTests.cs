using System.IO.Pipes;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using StupidDict.App;
using Xunit;

namespace StupidDict.App.Tests;

public class SingleInstanceTests
{
    [Fact]
    public void SecondAcquireIsRejectedWhileFirstHolds()
    {
        var lockPath = UniqueLockPath();
        var pipeName = UniqueName();
        using var first = SingleInstanceGuard.TryAcquire(lockPath, pipeName);
        Assert.NotNull(first);
        Assert.Null(SingleInstanceGuard.TryAcquire(lockPath, pipeName));
    }

    [Fact]
    public void LockIsReleasedOnDispose()
    {
        var lockPath = UniqueLockPath();
        var pipeName = UniqueName();
        using (SingleInstanceGuard.TryAcquire(lockPath, pipeName))
        {
        }
        using var again = SingleInstanceGuard.TryAcquire(lockPath, pipeName);
        Assert.NotNull(again);
    }

    [Fact]
    public void DirectorySquattingOnLockPathFailsOpenInsteadOfExiting()
    {
        // A null return means "another live instance" and the caller exits,
        // so a directory squatting on the lock path (shared /tmp, computable
        // name) must fail open to an unguarded instance instead.
        var lockPath = UniqueLockPath();
        Directory.CreateDirectory(lockPath);
        using var guard = SingleInstanceGuard.TryAcquire(lockPath, UniqueName());
        Assert.NotNull(guard);
    }

    [Fact]
    public void NotifyRunningInstanceRaisesActivation()
    {
        var mutexName = UniqueName();
        var pipeName = UniqueName();
        using var guard = SingleInstanceGuard.TryAcquire(mutexName, pipeName);
        Assert.NotNull(guard);
        var activated = false;
        guard!.ActivationRequested += () => activated = true;

        SingleInstanceGuard.NotifyRunningInstance(pipeName);

        WaitUntil(() => activated);
    }

    [Fact]
    public void SilentClientConnectionDoesNotPoisonTheActivationChannel()
    {
        // A local process that connects and then hangs without writing must be
        // dropped by the listener on its own; without the read timeout the
        // listener parks on that connection forever and every later second
        // launch in the session silently loses its activate signal.
        var lockPath = UniqueLockPath();
        var pipeName = UniqueName();
        using var guard = SingleInstanceGuard.TryAcquire(lockPath, pipeName);
        Assert.NotNull(guard);
        guard!.ReadLineTimeout = TimeSpan.FromMilliseconds(300);
        var activated = false;
        guard.ActivationRequested += () => activated = true;

        using (var silent = new NamedPipeClientStream(".", pipeName, PipeDirection.Out))
        {
            silent.Connect(2000);
            // Hold the floor silent well past the 300ms timeout: the listener
            // parks in the timed read within microseconds of Connect. The
            // fixed dwell is the point — the connection must stay mute across
            // the deadline, not wait on a condition.
            Thread.Sleep(500);
        }

        // The channel still serves the next client after the timed-out one.
        SingleInstanceGuard.NotifyRunningInstance(pipeName);
        WaitUntil(() => activated);
    }

    [AvaloniaFact]
    public void BringToFrontRestoresMinimizedWindow()
    {
        var window = new Window();
        window.Show();
        window.WindowState = WindowState.Minimized;
        WaitUntilUi(() => window.WindowState == WindowState.Minimized);

        SingleInstanceGuard.BringToFront(window);

        WaitUntilUi(() => window.WindowState == WindowState.Normal);
    }

    private static string UniqueLockPath() =>
        Path.Combine(Path.GetTempPath(), "stupiddict-test-" + Guid.NewGuid().ToString("N") + ".lock");

    // Pipe names become $TMPDIR/CoreFxPipe_<name> unix sockets on macOS, which
    // are capped at 104 path bytes (~60 for the name) — keep test names short.
    private static string UniqueName() => "sdict-t-" + Guid.NewGuid().ToString("N").Substring(0, 8);

    private static void WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 300; i++)
        {
            if (condition()) return;
            Thread.Sleep(10);
        }
        throw new TimeoutException("Condition not reached within 3s.");
    }

    private static void WaitUntilUi(Func<bool> condition)
    {
        for (var i = 0; i < 300; i++)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition()) return;
            Thread.Sleep(10);
        }
        throw new TimeoutException("Condition not reached within 3s.");
    }
}
