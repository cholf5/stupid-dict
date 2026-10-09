using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Controls;

namespace StupidDict.App;

/// <summary>
/// Single-instance guard. The first process holds an exclusive (FileShare.None)
/// lock file and listens on a named pipe; any later process fails to open the
/// file, signals "activate" on that pipe and exits — the running instance brings
/// its window to the foreground so the launch never looks like a silent no-op.
/// </summary>
/// <remarks>
/// Deliberately cross-platform with no #if, and deliberately a lock file rather
/// than a named Mutex: .NET named mutexes do not actually exclude on macOS (two
/// waiters on the same name both acquire — caught by SingleInstanceTests against
/// the real API), while an exclusive-open file is honored on Windows, macOS and
/// Linux alike, and the OS releases it when the process dies, which is exactly
/// the crash semantics we want. The macOS bundle never gets a second launch
/// through Finder (LaunchServices refuses), so the guard there only covers
/// raw-binary runs; Windows and Linux get the full behavior. Failures on the
/// guard's own plumbing fail open — a missing guard must never cost us a startup
/// — but once the lock is held the later launch always exits, signal or no signal.
/// </remarks>
public sealed class SingleInstanceGuard : IDisposable
{
    private const string ActivateCommand = "activate";
    private const int SignalAttempts = 10;

    /// <summary>
    /// Stable per-user identity: a fixed product prefix plus a hash of the user
    /// name. Windows/macOS temp directories are already per-user, but Linux shares
    /// one global /tmp, so the user hash keeps different users' instances
    /// independent there — and keeps characters that are illegal in pipe names,
    /// plus the raw account name, out of the identity altogether.
    /// Length matters: on Unix the pipe name becomes $TMPDIR/CoreFxPipe_<name>,
    /// a unix socket path capped at 104 bytes (~44 for the name on macOS, where
    /// $TMPDIR alone runs ~50) — keep Id at its current 23 characters.
    /// </summary>
    public static string Id { get; } = ComputeId();

    public static string LockFilePath { get; } = Path.Combine(Path.GetTempPath(), Id + ".lock");

    public static string PipeName => Id;

    /// <summary>The guard this process acquired, null when unguarded (fail-open).</summary>
    public static SingleInstanceGuard? Current { get; private set; }

    /// <summary>
    /// Raised on the listener thread whenever a second launch asks the running
    /// window to come to the foreground; subscribers must marshal to the UI thread.
    /// </summary>
    public event Action? ActivationRequested;

    private readonly FileStream? _lockFile;
    private readonly string? _pipeName;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Thread? _listener;
    private NamedPipeServerStream? _connected;

    private SingleInstanceGuard(FileStream? lockFile, string? pipeName)
    {
        _lockFile = lockFile;
        _pipeName = pipeName;
        if (pipeName is not null)
            _listener = new Thread(Listen) { IsBackground = true, Name = "SingleInstanceListener" };
    }

    /// <summary>
    /// Claims single-instance ownership. Returns null when another live instance
    /// already holds the lock file — the caller should notify it and exit.
    /// </summary>
    public static SingleInstanceGuard? TryAcquire()
    {
        var guard = TryAcquire(LockFilePath, PipeName);
        if (guard is not null)
            Current = guard;
        return guard;
    }

    internal static SingleInstanceGuard? TryAcquire(string lockPath, string pipeName)
    {
        FileStream? lockFile;
        try
        {
            // Exclusive open is the whole lock: while we hold this handle every
            // other open (any process) fails, and the OS closes it for us on
            // death — no cleanup logic, no abandoned state to adopt.
            lockFile = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            // Held by another live instance.
            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Unwritable temp dir / exotic sandbox: run unguarded rather than not at all.
            return new SingleInstanceGuard(lockFile: null, pipeName: null);
        }

        var guard = new SingleInstanceGuard(lockFile, pipeName);
        guard._listener!.Start();
        return guard;
    }

    /// <summary>
    /// Asks the running instance to foreground its window. Best effort with a
    /// short retry window — the double-launch race means the other process's
    /// listener may still be starting up — but the caller exits either way:
    /// never starting a second instance is the invariant, activation is the
    /// courtesy on top.
    /// </summary>
    public static void NotifyRunningInstance() => NotifyRunningInstance(PipeName);

    internal static void NotifyRunningInstance(string pipeName)
    {
        for (var attempt = 0; attempt < SignalAttempts; attempt++)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
                client.Connect(attempt == SignalAttempts - 1 ? 150 : 100);
                // StreamWriter owns the client stream; disposing it closes both.
                using var writer = new StreamWriter(client) { AutoFlush = true };
                writer.WriteLine(ActivateCommand);
                return;
            }
            catch (Exception)
            {
                if (attempt == SignalAttempts - 1)
                    return;
                Thread.Sleep(50);
            }
        }
    }

    /// <summary>
    /// Raises the running window for a second-launch activation: un-minimize when
    /// needed, then Activate. No Show() — the app has no hide-to-tray state, the
    /// window is always visible while the process lives. (Windows' foreground lock
    /// may downscale this to a taskbar flash; that is the standard, acceptable
    /// outcome for a background process demanding focus.)
    /// </summary>
    public static void BringToFront(Window window)
    {
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Activate();
    }

    private void Listen()
    {
        var pipeName = _pipeName!;
        while (!_cancellation.IsCancellationRequested)
        {
            NamedPipeServerStream? server = TryCreateServer(pipeName);
            if (server is null)
                return;
            try
            {
                _connected = server;
                server.WaitForConnectionAsync(_cancellation.Token).GetAwaiter().GetResult();
                string? command;
                using (var reader = new StreamReader(server))
                    command = reader.ReadLine();
                if (command == ActivateCommand)
                    ActivationRequested?.Invoke();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                // Broken client connection or a platform pipe hiccup: keep serving.
                if (_cancellation.IsCancellationRequested)
                    return;
            }
            finally
            {
                _connected = null;
                server.Dispose();
            }
        }
    }

    private static NamedPipeServerStream? TryCreateServer(string pipeName)
    {
        // A few bounded tries, then give up: a name we can never bind (socket
        // left behind by a SIGKILLed instance, over-long temp path, sandbox)
        // must not turn the listener into a hot spin. Single-instance still
        // holds via the lock file; only the activation courtesy is lost.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                return new NamedPipeServerStream(pipeName, PipeDirection.In, maxNumberOfServerInstances: 1);
            }
            catch (Exception)
            {
                if (attempt == 2)
                    return null;
                Thread.Sleep(200);
            }
        }
        return null;
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        // Disposing the in-flight server unblocks WaitForConnectionAsync where
        // token cancellation alone may not (platform-dependent). The listener may
        // rotate to a fresh server between our cancel and the dispose, so after
        // the first join, dispose whatever it landed on and wait once more.
        _connected?.Dispose();
        _listener?.Join(TimeSpan.FromSeconds(1));
        _connected?.Dispose();
        _listener?.Join(TimeSpan.FromSeconds(1));
        _cancellation.Dispose();
        // Releasing the file lock last: until then a concurrent launch still sees
        // the instance as live, which is the correct order during teardown.
        _lockFile?.Dispose();
        if (Current == this)
            Current = null;
    }

    private static string ComputeId()
    {
        var seed = Encoding.UTF8.GetBytes("cholf5/stupid-dict:" + Environment.UserName);
        var hash = SHA256.HashData(seed);
        return "stupiddict-" + Convert.ToHexString(hash.AsSpan(0, 6)).ToLowerInvariant();
    }
}
