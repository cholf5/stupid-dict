using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using StupidDict.App;
using StupidDict.Core.Application;
using StupidDict.Core.Dictionary;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(StupidDict.App.Tests.TestAppBuilder))]

namespace StupidDict.App.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public class HeadlessWindowTests
{
    [AvaloniaFact]
    public void SearchBoxIsFocusedOnStartup()
    {
        using var service = CreateService();
        var window = new MainWindow(service);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.True(window.FindControl<TextBox>("SearchBox")!.IsFocused);
    }

    [AvaloniaFact]
    public void EnterShowsEnglishResult()
    {
        using var service = CreateService();
        var window = new MainWindow(service);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var searchBox = window.FindControl<TextBox>("SearchBox")!;
        searchBox.Text = "cat";
        PressEnter(searchBox);
        WaitUntil(() => window.FindControl<StackPanel>("ResultsPanel")!.IsVisible);

        Assert.False(window.FindControl<StackPanel>("HintPanel")!.IsVisible);
        SaveScreenshot(window, "stupiddict-en.png");
        Assert.True(File.Exists(Path.Combine(Path.GetTempPath(), "stupiddict-en.png")));
    }

    [AvaloniaFact]
    public void EnterShowsChineseResult()
    {
        using var service = CreateService();
        var window = new MainWindow(service);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var searchBox = window.FindControl<TextBox>("SearchBox")!;
        searchBox.Text = "猫";
        PressEnter(searchBox);
        WaitUntil(() => window.FindControl<StackPanel>("ResultsPanel")!.IsVisible);

        SaveScreenshot(window, "stupiddict-zh.png");
        Assert.True(File.Exists(Path.Combine(Path.GetTempPath(), "stupiddict-zh.png")));
    }

    [AvaloniaFact]
    public void RecentSearchesAppearAfterLookup()
    {
        using var service = CreateService();
        var window = new MainWindow(service);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var searchBox = window.FindControl<TextBox>("SearchBox")!;
        searchBox.Text = "cat";
        PressEnter(searchBox);
        WaitUntil(() => window.FindControl<StackPanel>("ResultsPanel")!.IsVisible);

        searchBox.Text = "猫";
        PressEnter(searchBox);
        WaitUntil(() =>
        {
            var recent = window.FindControl<ItemsControl>("RecentList")!.ItemsSource;
            return recent is not null && recent.Cast<object>().Count() == 2;
        });

        var recentPanel = window.FindControl<StackPanel>("RecentPanel")!;
        Assert.True(recentPanel.IsVisible);
        SaveScreenshot(window, "stupiddict-recent.png");
    }

    private static DictionaryService CreateService()
    {
        var directory = Path.Combine(Path.GetTempPath(), "stupiddict-uitests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var dictionaryPath = Path.Combine(directory, "dictionary.db");
        using (var db = DictionaryDatabase.Create(dictionaryPath))
        {
            db.BeginTransaction();
            var cat = db.InsertWord("cat", "kæt", "n:100", "n. 猫, 恶妇\nvi. 呕吐",
                "a small animal with four legs, especially one kept as a pet", 1775, 0, "zk gk");
            if (cat >= 0)
            {
                db.InsertZhTerm("猫", cat);
                db.InsertWordForm("cats", cat);
            }
            db.InsertWord("catch", "", "", "v. 抓住", "", 900, 0, "gk");
            db.CommitTransaction();
        }
        return new DictionaryService(dictionaryPath, Path.Combine(directory, "history.db"));
    }

    private static void PressEnter(TextBox searchBox) =>
        searchBox.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });

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

    private static void SaveScreenshot(Window window, string fileName)
    {
        window.CaptureRenderedFrame()?.Save(Path.Combine(Path.GetTempPath(), fileName));
    }
}
