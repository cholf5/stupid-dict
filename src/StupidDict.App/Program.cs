using Avalonia;
using StupidDict.App.Update;

namespace StupidDict.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Self-update hand-over: the replaced process starts us with
        // "--wait-for-exit <pid>" so the single-instance guard below is
        // claimed only after the old instance has really exited. Without the
        // wait the new process would be turned away and the app would look
        // like it vanished after updating. Bounded, never throws.
        UpdateRelaunchHandshake.WaitIfRequested(args);
        // Single instance: a later launch signals the running window to the
        // foreground and exits instead of starting a duplicate process (see
        // SingleInstanceGuard — its own plumbing fails open, so this can never
        // block a first startup).
        using var guard = SingleInstanceGuard.TryAcquire();
        if (guard is null)
        {
            SingleInstanceGuard.NotifyRunningInstance();
            return;
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
