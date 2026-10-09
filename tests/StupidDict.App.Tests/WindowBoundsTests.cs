using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using StupidDict.App;
using StupidDict.App.Localization;
using StupidDict.App.Settings;
using StupidDict.Core.Application;
using StupidDict.Core.Dictionary;
using Xunit;

namespace StupidDict.App.Tests;

public class WindowBoundsTests
{
    public WindowBoundsTests()
    {
        // Same pin as HeadlessWindowTests: instance initial language resolves
        // from machine culture, assertions must be deterministic. Deliberately
        // NOT deriving from HeadlessWindowTests — inherited facts would re-run
        // the whole base suite under this class.
        Translations.Instance.SetLanguage(AppLanguage.SimplifiedChinese);
    }
    [AvaloniaFact]
    public void KeepsXamlDefaultSizeWithoutPersistedBounds()
    {
        using var service = CreateService();
        var window = new MainWindow(service, autoDownload: false, settings: new AppSettings());
        window.Show();

        Assert.Equal(780, window.Width);
        Assert.Equal(560, window.Height);
    }

    [AvaloniaFact]
    public void RestoresPersistedSize()
    {
        using var service = CreateService();
        var settings = new AppSettings { WindowWidth = 900, WindowHeight = 640 };
        var window = new MainWindow(service, autoDownload: false, settings: settings);
        window.Show();

        Assert.Equal(900, window.Width);
        Assert.Equal(640, window.Height);
    }

    [AvaloniaFact]
    public void RestoresMaximizedState()
    {
        using var service = CreateService();
        var settings = new AppSettings { WindowWidth = 800, WindowHeight = 600, WindowMaximized = true };
        var window = new MainWindow(service, autoDownload: false, settings: settings);

        Assert.Equal(WindowState.Maximized, window.WindowState);
        // The stored size must survive: un-maximizing lands on it.
        Assert.Equal(800, window.Width);
        Assert.Equal(600, window.Height);
    }

    [AvaloniaFact]
    public void ClosingSavesResizedNormalSize()
    {
        using var service = CreateService();
        var settings = new AppSettings();
        var window = new MainWindow(service, autoDownload: false, settings: settings);
        window.Show();

        window.Width = 900;
        window.Height = 640;
        WaitUntil(() => Math.Abs(window.Bounds.Width - 900) < 0.5 && Math.Abs(window.Bounds.Height - 640) < 0.5);
        window.Close();
        WaitUntil(() => settings.WindowWidth is not null);

        Assert.Equal(900, settings.WindowWidth);
        Assert.Equal(640, settings.WindowHeight);
        Assert.False(settings.WindowMaximized);
    }

    [AvaloniaFact]
    public void ClosingWhileMaximizedSavesMaximizedAndLastNormalSize()
    {
        using var service = CreateService();
        var settings = new AppSettings();
        var window = new MainWindow(service, autoDownload: false, settings: settings);
        window.Show();

        window.Width = 900;
        window.Height = 640;
        WaitUntil(() => Math.Abs(window.Bounds.Width - 900) < 0.5 && Math.Abs(window.Bounds.Height - 640) < 0.5);
        window.WindowState = WindowState.Maximized;
        WaitUntil(() => window.WindowState == WindowState.Maximized);
        window.Close();
        WaitUntil(() => settings.WindowWidth is not null);

        // The maximized bounds belong to the platform; the recorded size is
        // the last one the user actually chose, so un-maximizing next run
        // lands where they left it.
        Assert.True(settings.WindowMaximized);
        Assert.Equal(900, settings.WindowWidth);
        Assert.Equal(640, settings.WindowHeight);
    }

    [AvaloniaFact]
    public void ClosingWritesPersistedBoundsThroughWiring()
    {
        var settings = new AppSettings { Language = AppLanguage.SimplifiedChinese };
        var path = Path.Combine(Path.GetTempPath(), "stupiddict-uitests",
            Guid.NewGuid().ToString("N") + "-settings.json");
        App.WireSettings(settings, path);
        try
        {
            using var service = CreateService();
            var window = new MainWindow(service, autoDownload: false, settings: settings);
            window.Show();

            window.Width = 900;
            window.Height = 640;
            WaitUntil(() => Math.Abs(window.Bounds.Width - 900) < 0.5 && Math.Abs(window.Bounds.Height - 640) < 0.5);
            window.Close();

            Assert.Equal(900, SettingsService.Load(path).WindowWidth);
            Assert.Equal(640, SettingsService.Load(path).WindowHeight);
        }
        finally
        {
            Translations.Instance.SetLanguage(AppLanguage.SimplifiedChinese);
        }
    }

    private static DictionaryService CreateService()
    {
        var directory = Path.Combine(Path.GetTempPath(), "stupiddict-uitests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var dictionaryPath = Path.Combine(directory, "dictionary.db");
        using (DictionaryDatabase.Create(dictionaryPath))
        {
        }
        return new DictionaryService(dictionaryPath, Path.Combine(directory, "history.db"));
    }

    private static void WaitUntil(Func<bool> condition)
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
