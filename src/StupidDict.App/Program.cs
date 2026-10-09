using Avalonia;

namespace StupidDict.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
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
