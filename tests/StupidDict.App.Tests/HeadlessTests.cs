using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
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

    [AvaloniaFact]
    public void TypingShowsPrefixSuggestions()
    {
        using var service = CreateService();
        var window = new MainWindow(service);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var searchBox = window.FindControl<TextBox>("SearchBox")!;
        searchBox.Text = "ca";
        WaitUntil(() => window.FindControl<Border>("SuggestPanel")!.IsVisible);

        var words = window.FindControl<ItemsControl>("SuggestList")!.ItemsSource!.Cast<string>().ToList();
        Assert.Contains("cat", words);
        Assert.Contains("catch", words);
        SaveScreenshot(window, "stupiddict-suggest.png");
    }

    [AvaloniaFact]
    public void EnterWithSuggestionsOpenStillQueriesTypedText()
    {
        using var service = CreateService();
        var window = new MainWindow(service);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var searchBox = window.FindControl<TextBox>("SearchBox")!;
        searchBox.Text = "ca";
        WaitUntil(() => window.FindControl<Border>("SuggestPanel")!.IsVisible);

        PressEnter(searchBox);
        WaitUntil(() => window.FindControl<StackPanel>("ResultsPanel")!.IsVisible);

        // Enter means "look up what I typed" — the box keeps "ca", the
        // suggestion list closes, and the result view replaces the list.
        Assert.Equal("ca", searchBox.Text);
        Assert.False(window.FindControl<Border>("SuggestPanel")!.IsVisible);
    }

    [AvaloniaFact]
    public void EscapeClosesSuggestionsBeforeClearing()
    {
        using var service = CreateService();
        var window = new MainWindow(service);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var searchBox = window.FindControl<TextBox>("SearchBox")!;
        searchBox.Text = "ca";
        WaitUntil(() => window.FindControl<Border>("SuggestPanel")!.IsVisible);

        PressKey(searchBox, Key.Escape);
        Assert.False(window.FindControl<Border>("SuggestPanel")!.IsVisible);
        Assert.Equal("ca", searchBox.Text);

        PressKey(searchBox, Key.Escape);
        Assert.True(string.IsNullOrEmpty(searchBox.Text));
    }

    [AvaloniaFact]
    public void ArrowDownSelectsSuggestionAndEnterSearchesIt()
    {
        using var service = CreateService();
        var window = new MainWindow(service);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var searchBox = window.FindControl<TextBox>("SearchBox")!;
        searchBox.Text = "ca";
        WaitUntil(() => window.FindControl<Border>("SuggestPanel")!.IsVisible);

        var firstSuggestion = window.FindControl<ItemsControl>("SuggestList")!.ItemsSource!.Cast<string>().First();
        PressKey(searchBox, Key.Down);
        PressKey(searchBox, Key.Enter);
        WaitUntil(() => window.FindControl<StackPanel>("ResultsPanel")!.IsVisible);

        // An explicit selection replaces the query; the first suggestion wins.
        Assert.Equal(firstSuggestion, searchBox.Text);
        Assert.False(window.FindControl<Border>("SuggestPanel")!.IsVisible);
    }

    [AvaloniaFact]
    public void DoubleClickWordInResultLooksItUp()
    {
        using var service = CreateService();
        var window = new MainWindow(service);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var searchBox = window.FindControl<TextBox>("SearchBox")!;
        searchBox.Text = "猫";
        PressEnter(searchBox);
        WaitUntil(() => window.FindControl<StackPanel>("ResultsPanel")!.IsVisible);

        // The Chinese result lists the English headword "cat"; double-clicking
        // it must run the English lookup, mirror the word into the box, and
        // leave the previous page one step back in history.
        var catLine = window.FindControl<StackPanel>("ResultsPanel")!.GetVisualDescendants()
            .OfType<SelectableTextBlock>().First(b => b.Text == "cat");
        var point = catLine.TranslatePoint(new Point(6, catLine.Bounds.Center.Y), window)!.Value;
        DoubleClick(window, point);
        WaitUntil(() => Headword(window) == "cat");

        Assert.Equal("cat", searchBox.Text);
        Assert.True(window.FindControl<Button>("NavBackButton")!.IsEnabled);
        SaveScreenshot(window, "stupiddict-doubleclick.png");
    }

    [AvaloniaFact]
    public void DoubleClickingHeadwordDoesNotDuplicateHistory()
    {
        using var service = CreateService();
        var window = new MainWindow(service);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var searchBox = window.FindControl<TextBox>("SearchBox")!;
        searchBox.Text = "cat";
        PressEnter(searchBox);
        WaitUntil(() => Headword(window) == "cat");

        var title = (SelectableTextBlock)window.FindControl<StackPanel>("ResultsPanel")!.Children[0];
        var point = title.TranslatePoint(new Point(8, title.Bounds.Center.Y), window)!.Value;
        DoubleClick(window, point);
        PumpJobs(TimeSpan.FromMilliseconds(500));

        // The re-lookup of the page already on top must collapse into it.
        Assert.False(window.FindControl<Button>("NavBackButton")!.IsEnabled);
        Assert.Equal("cat", Headword(window));
    }

    [AvaloniaFact]
    public void BackAndForwardButtonsNavigateHistory()
    {
        using var service = CreateService();
        var window = new MainWindow(service);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var searchBox = window.FindControl<TextBox>("SearchBox")!;
        searchBox.Text = "cat";
        PressEnter(searchBox);
        WaitUntil(() => Headword(window) == "cat");
        searchBox.Text = "catch";
        PressEnter(searchBox);
        WaitUntil(() => Headword(window) == "catch");

        var back = window.FindControl<Button>("NavBackButton")!;
        var forward = window.FindControl<Button>("NavForwardButton")!;
        Assert.True(back.IsEnabled);
        Assert.False(forward.IsEnabled);

        RaiseClick(back);
        WaitUntil(() => Headword(window) == "cat");
        Assert.Equal("cat", searchBox.Text);
        Assert.True(forward.IsEnabled);

        RaiseClick(forward);
        WaitUntil(() => Headword(window) == "catch");
        SaveScreenshot(window, "stupiddict-nav.png");
    }

    [AvaloniaFact]
    public void EscapeClearedResultsComeBackOnBack()
    {
        using var service = CreateService();
        var window = new MainWindow(service);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var searchBox = window.FindControl<TextBox>("SearchBox")!;
        searchBox.Text = "cat";
        PressEnter(searchBox);
        WaitUntil(() => Headword(window) == "cat");
        searchBox.Text = "catch";
        PressEnter(searchBox);
        WaitUntil(() => Headword(window) == "catch");

        searchBox.Clear();
        PressKey(searchBox, Key.Escape);
        Assert.False(window.FindControl<StackPanel>("ResultsPanel")!.IsVisible);

        RaiseClick(window.FindControl<Button>("NavBackButton")!);
        Assert.True(window.FindControl<StackPanel>("ResultsPanel")!.IsVisible);
        Assert.Equal("cat", Headword(window));
    }

    [AvaloniaFact]
    public void CmdBracketsNavigateBackAndForward()
    {
        using var service = CreateService();
        var window = new MainWindow(service);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var searchBox = window.FindControl<TextBox>("SearchBox")!;
        searchBox.Text = "cat";
        PressEnter(searchBox);
        WaitUntil(() => Headword(window) == "cat");
        searchBox.Text = "catch";
        PressEnter(searchBox);
        WaitUntil(() => Headword(window) == "catch");

        window.KeyPress(Key.OemOpenBrackets, RawInputModifiers.Meta, PhysicalKey.BracketLeft, "[");
        WaitUntil(() => Headword(window) == "cat");

        window.KeyPress(Key.OemCloseBrackets, RawInputModifiers.Meta, PhysicalKey.BracketRight, "]");
        WaitUntil(() => Headword(window) == "catch");
    }

    [AvaloniaFact]
    public void SynonymSectionsRenderAndClickLooksUp()
    {
        using var service = CreateService();
        var window = new MainWindow(service);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var searchBox = window.FindControl<TextBox>("SearchBox")!;
        searchBox.Text = "cat";
        // Wait for the deferred TextChanged to schedule completion, then close
        // it with Enter — otherwise the suggest panel pops over the results and
        // swallows the link click below.
        WaitUntil(() => window.FindControl<Border>("SuggestPanel")!.IsVisible);
        PressEnter(searchBox);
        WaitUntil(() => Headword(window) == "cat");

        var labels = window.FindControl<StackPanel>("ResultsPanel")!.GetVisualDescendants()
            .OfType<TextBlock>().Select(b => b.Text).ToList();
        Assert.Contains("近义词", labels);
        Assert.Contains("反义词", labels);
        Assert.Contains("联想词", labels);

        // The synonym line reads "n. tiger" — links are inline controls, so the
        // whole word is the hit area and a plain click on its center looks it up.
        var link = window.FindControl<StackPanel>("ResultsPanel")!.GetVisualDescendants()
            .OfType<TextBlock>()
            .First(b => b.Classes.Contains("wordlink"));
        var point = link.TranslatePoint(new Point(link.Bounds.Center.X, link.Bounds.Center.Y), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);

        WaitUntil(() => Headword(window) == link.Text);
        Assert.Equal(link.Text, searchBox.Text);
        Assert.True(window.FindControl<Button>("NavBackButton")!.IsEnabled);
        SaveScreenshot(window, "stupiddict-thesaurus.png");
    }

    private static string? Headword(Window window) =>
        (window.FindControl<StackPanel>("ResultsPanel")!.Children.FirstOrDefault() as TextBlock)?.Text;

    private static void RaiseClick(Button button) =>
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    /// <summary>
    /// Two full press/release cycles at one point; the second press resolves to
    /// ClickCount=2 through the real mouse-device click counting.
    /// </summary>
    private static void DoubleClick(TopLevel window, Point point)
    {
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
    }

    /// <summary>Pumps the dispatcher long enough for a pending async lookup to settle.</summary>
    private static void PumpJobs(TimeSpan duration)
    {
        var remaining = duration;
        while (remaining > TimeSpan.Zero)
        {
            Dispatcher.UIThread.RunJobs();
            var slice = TimeSpan.FromMilliseconds(50);
            Thread.Sleep(slice);
            remaining -= slice;
        }
        Dispatcher.UIThread.RunJobs();
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
                db.InsertSynGroup(cat, "syn", "n.", "tiger");
                db.InsertSynGroup(cat, "ant", "adj.", "doglike");
            }
            var tiger = db.InsertWord("tiger", "ˈtaɪɡər", "n:80", "n. 老虎", "", 900, 0, "zk gk");
            if (tiger >= 0) db.InsertZhTerm("老虎", tiger);
            db.InsertWord("catch", "", "", "v. 抓住", "", 900, 0, "gk");
            db.CommitTransaction();
        }
        return new DictionaryService(dictionaryPath, Path.Combine(directory, "history.db"));
    }

    private static void PressEnter(TextBox searchBox) => PressKey(searchBox, Key.Enter);

    private static void PressKey(TextBox searchBox, Key key) =>
        searchBox.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key });

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
