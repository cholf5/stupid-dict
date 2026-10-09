using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SkiaSharp;
using StupidDict.App;
using StupidDict.App.Assets;
using StupidDict.App.Localization;
using StupidDict.App.Settings;
using StupidDict.App.Speech;
using StupidDict.Core.Application;
using StupidDict.Core.Dictionary;
using Xunit;
using System.ComponentModel;

[assembly: AvaloniaTestApplication(typeof(StupidDict.App.Tests.TestAppBuilder))]

// Translations is a process-level singleton that language tests mutate;
// xunit parallelizes test classes by default, which would race on it.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

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
    public HeadlessWindowTests()
    {
        // Translations is a process-level singleton; fix it to Chinese so
        // string assertions are deterministic regardless of the machine's UI
        // culture (the language live-switch test restores it afterwards).
        Translations.Instance.SetLanguage(AppLanguage.SimplifiedChinese);
    }

    [AvaloniaFact]
    public void SearchBoxIsFocusedOnStartup()
    {
        using var service = CreateService();
        var window = new MainWindow(service, autoDownload: false);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.True(window.FindControl<TextBox>("SearchBox")!.IsFocused);
    }

    [AvaloniaFact]
    public void WindowsCarryAppIconFromEmbeddedAssets()
    {
        // The avares prefix is the assembly name (StupidDict, not the project
        // name); a wrong URI throws while loading the XAML, so constructing the
        // windows pins the wiring. The png is what MacDockIcon hands to
        // NSApplication on macOS dev runs (the icns travels via package.sh only).
        using var service = CreateService();
        var window = new MainWindow(service, autoDownload: false);
        Assert.NotNull(window.Icon);
        var settingsWindow = new SettingsWindow(new AppSettings());
        Assert.NotNull(settingsWindow.Icon);
        using var icon = AssetLoader.Open(new Uri("avares://StupidDict/Assets/app-icon.png"));
        Assert.True(icon.Length > 0);
    }

    [AvaloniaFact]
    public void WindowsTaskbarIconArtworkIsFullBleed()
    {
        // Windows renders exe/taskbar icons at the tile's native size with
        // no system mask: Apple-grid margins baked into the artwork shrink
        // it a step below every full-bleed neighbour on the taskbar
        // (2026-10-09 user report). The ico must fill its canvas edge to
        // edge, only the baked corner arcs stay transparent. The png/icns
        // keep the Apple margins on purpose (macOS Dock semantics), so this
        // pins the ico's geometry, not the master's.
        using var stream = AssetLoader.Open(new Uri("avares://StupidDict/Assets/app-icon.ico"));
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        var frame = ExtractIcoFrame(ms.ToArray(), 256);
        Assert.NotNull(frame);
        using var bmp = SKBitmap.Decode(frame);
        Assert.NotNull(bmp);
        Assert.Equal(256, bmp.Width);
        Assert.Equal(256, bmp.Height);
        Assert.Equal((byte)0, bmp.GetPixel(0, 0).Alpha);       // corner: arc baked in
        Assert.Equal((byte)255, bmp.GetPixel(128, 128).Alpha); // center opaque
        Assert.Equal((byte)255, bmp.GetPixel(3, 128).Alpha);   // left mid-edge: 0 in the old margined ico
        Assert.Equal((byte)255, bmp.GetPixel(252, 128).Alpha); // right mid-edge
    }

    private static byte[]? ExtractIcoFrame(byte[] ico, int size)
    {
        // ICONDIR: reserved(2) type(2) count(2); ICONDIRENTRY: width(1)
        // height(1) colors(1) reserved(1) planes(2) bpp(2) bytes(4)
        // offset(4). A 0 width/height byte means 256.
        if (ico.Length < 6) return null;
        int count = BitConverter.ToUInt16(ico, 4);
        for (int i = 0; i < count; i++)
        {
            int e = 6 + i * 16;
            if (e + 16 > ico.Length) return null;
            int w = ico[e] == 0 ? 256 : ico[e];
            int h = ico[e + 1] == 0 ? 256 : ico[e + 1];
            int bytes = BitConverter.ToInt32(ico, e + 8);
            int offset = BitConverter.ToInt32(ico, e + 12);
            if (w == size && h == size && offset >= 0 && offset + bytes <= ico.Length)
                return ico[offset..(offset + bytes)];
        }
        return null;
    }

    [AvaloniaFact]
    public void MacMenuBarAppNameFollowsUiLanguage()
    {
        // The menu-bar app title is Application.Current.Name, pushed natively by
        // AvaloniaNative (SetApplicationTitle) at platform init and replayed on
        // every language change via MacAppTitle; it must track the UI language
        // exactly like the window title binding does. The native push itself is
        // a no-op off macOS and unobservable headlessly, so pin the value wiring.
        try
        {
            Translations.Instance.SetLanguage(AppLanguage.SimplifiedChinese);
            Assert.Equal("傻瓜词典", App.Current!.Name);
            Translations.Instance.SetLanguage(AppLanguage.English);
            Assert.Equal("Stupid Dict", App.Current!.Name);
        }
        finally
        {
            Translations.Instance.SetLanguage(AppLanguage.SimplifiedChinese);
        }
    }

    [AvaloniaFact]
    public void SearchBoxLineHeightFollowsWatermarkScript()
    {
        // The CJK watermark only misaligns with the caret when a CJK fallback
        // font exists to shape it; on a system without one there is nothing to
        // correct, so both branches must be accepted here.
        var hasCjkFallback = FontManager.Current.TryMatchCharacter('输', FontStyle.Normal,
            FontWeight.Normal, FontStretch.Normal, null, null, out _);

        using var service = CreateService();
        var window = new MainWindow(service, autoDownload: false);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var searchBox = window.FindControl<TextBox>("SearchBox")!;
        var lineHeight = searchBox.LineHeight;
        if (hasCjkFallback)
            Assert.True(lineHeight > 18, $"zh LineHeight={lineHeight}, expected CJK fallback line height");
        else
            Assert.True(double.IsNaN(lineHeight), $"zh LineHeight={lineHeight}, expected NaN");

        SaveScreenshot(window, "stupiddict-caret-zh.png");

        Translations.Instance.SetLanguage(AppLanguage.English);
        try
        {
            Assert.True(double.IsNaN(searchBox.LineHeight),
                $"en LineHeight={searchBox.LineHeight}, expected NaN");
        }
        finally
        {
            Translations.Instance.SetLanguage(AppLanguage.SimplifiedChinese);
        }
    }

    [AvaloniaFact]
    public void EnterShowsEnglishResult()
    {
        using var service = CreateService();
        var window = new MainWindow(service, autoDownload: false);
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
        var window = new MainWindow(service, autoDownload: false);
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
    public void ClearSearchButtonFollowsInputAndResetsResults()
    {
        using var service = CreateService();
        var window = new MainWindow(service, autoDownload: false);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var searchBox = window.FindControl<TextBox>("SearchBox")!;
        var clearButton = window.FindControl<Button>("ClearSearchButton")!;
        var settingsButton = window.FindControl<Button>("SettingsButton")!;
        // Empty box: the gear owns the rightmost slot, ✕ is hidden.
        Assert.True(settingsButton.IsVisible);
        Assert.False(clearButton.IsVisible);

        searchBox.Text = "cat";
        // TextChanged is raised via Dispatcher.UIThread.Post in Avalonia 11,
        // so the buttons' visibility lands on the next dispatcher pass.
        WaitUntil(() => clearButton.IsVisible);
        Assert.False(settingsButton.IsVisible);

        PressEnter(searchBox);
        WaitUntil(() => window.FindControl<StackPanel>("ResultsPanel")!.IsVisible);

        RaiseClick(clearButton);
        Assert.Equal(string.Empty, searchBox.Text);
        WaitUntil(() => !clearButton.IsVisible);
        Assert.True(settingsButton.IsVisible);
        Assert.False(window.FindControl<StackPanel>("ResultsPanel")!.IsVisible);
        Assert.True(window.FindControl<StackPanel>("HintPanel")!.IsVisible);
        Assert.True(searchBox.IsFocused);
    }

    [AvaloniaFact]
    public void RecentSearchesHiddenOnResultsBackOnEmptyState()
    {
        using var service = CreateService();
        var window = new MainWindow(service, autoDownload: false);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var recentPanel = window.FindControl<StackPanel>("RecentPanel")!;
        var recentList = window.FindControl<ItemsControl>("RecentList")!;
        var searchBox = window.FindControl<TextBox>("SearchBox")!;
        // Fresh history: nothing to show even on the empty state.
        Assert.False(recentPanel.IsVisible);

        searchBox.Text = "cat";
        PressEnter(searchBox);
        WaitUntil(() => window.FindControl<StackPanel>("ResultsPanel")!.IsVisible);
        // The result page owns the window; the strip hides instantly while
        // the word still lands in the list.
        WaitUntil(() => !recentPanel.IsVisible);
        Assert.Single(recentList.ItemsSource!.Cast<object>());

        searchBox.Clear();
        PressKey(searchBox, Key.Escape);
        WaitUntil(() => recentPanel.IsVisible);
        Assert.True(window.FindControl<StackPanel>("HintPanel")!.IsVisible);

        searchBox.Text = "猫";
        PressEnter(searchBox);
        WaitUntil(() => recentList.ItemsSource!.Cast<object>().Count() == 2);
        WaitUntil(() => !recentPanel.IsVisible);
        SaveScreenshot(window, "stupiddict-recent.png");
    }

    [AvaloniaFact]
    public void TypingShowsPrefixSuggestions()
    {
        using var service = CreateService();
        var window = new MainWindow(service, autoDownload: false);
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
        var window = new MainWindow(service, autoDownload: false);
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
        var window = new MainWindow(service, autoDownload: false);
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
        var window = new MainWindow(service, autoDownload: false);
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
        var window = new MainWindow(service, autoDownload: false);
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
        var window = new MainWindow(service, autoDownload: false);
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
        var window = new MainWindow(service, autoDownload: false);
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
        var window = new MainWindow(service, autoDownload: false);
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
        var window = new MainWindow(service, autoDownload: false);
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
    public void InFlightLookupDoesNotClobberNavigatedPage()
    {
        // TC-001 (B-003): Navigate must advance _searchGeneration the way
        // ShowEmptyState does. A lookup still in flight when the user steps
        // back/forward is stale once it completes: it must neither render
        // over the navigated page, nor Push (truncating the forward history
        // the step just restored), nor leave the search box showing the
        // navigated word over a different page. The LookupOverride seam
        // parks the query mid-flight so the completion moment is
        // deterministic — the resumption rides the dispatcher exactly like
        // every async result in this harness and lands during RunJobs.
        using var service = CreateService();
        var window = new MainWindow(service, autoDownload: false);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var searchBox = window.FindControl<TextBox>("SearchBox")!;
        searchBox.Text = "cat";
        PressEnter(searchBox);
        WaitUntil(() => Headword(window) == "cat");
        searchBox.Text = "catch";
        PressEnter(searchBox);
        WaitUntil(() => Headword(window) == "catch");

        var renders = 0;
        window.ResultRendered += () => renders++;

        // Delivery control: a gated lookup completing with no navigation in
        // between lands normally, so the drop asserted further down is not
        // a vacuous pass.
        var deliveryGate = new TaskCompletionSource<LookupResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        window.LookupOverride = _ => deliveryGate.Task;
        searchBox.Text = "hello";
        PressEnter(searchBox);
        deliveryGate.SetResult(service.Lookup("tiger"));
        WaitUntil(() => Headword(window) == "tiger");
        Assert.Equal(1, renders);

        // The race: hold "hello" in flight, step back while it runs.
        var raceGate = new TaskCompletionSource<LookupResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        window.LookupOverride = _ => raceGate.Task;
        searchBox.Text = "hello";
        PressEnter(searchBox);
        window.KeyPress(Key.OemOpenBrackets, RawInputModifiers.Meta, PhysicalKey.BracketLeft, "[");
        Assert.Equal("catch", Headword(window));
        Assert.Equal("catch", searchBox.Text);
        Assert.True(window.FindControl<Button>("NavForwardButton")!.IsEnabled);
        Assert.Equal(2, renders); // the navigate itself rendered once

        // The stale completion arrives: dropped whole — no render, no push
        // (the forward branch survives), no button churn.
        raceGate.SetResult(service.Lookup("cat"));
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("catch", Headword(window));
        Assert.Equal("catch", searchBox.Text);
        Assert.True(window.FindControl<Button>("NavForwardButton")!.IsEnabled);
        Assert.Equal(2, renders);

        // Fresh lookups still deliver after the drop.
        window.LookupOverride = null;
        searchBox.Text = "cat";
        PressEnter(searchBox);
        WaitUntil(() => Headword(window) == "cat");
        Assert.Equal(3, renders);
    }

    [AvaloniaFact]
    public void SynonymSectionsRenderAndClickLooksUp()
    {
        using var service = CreateService();
        var window = new MainWindow(service, autoDownload: false);
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

        // The synonym line reads "n. tiger" — the link Border is the whole
        // hit area, so a plain click on its center looks the word up.
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

    [AvaloniaFact]
    public void SynonymLineKeepsCommaInsideWordEntry()
    {
        var directory = Path.Combine(Path.GetTempPath(), "stupiddict-uitests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var dictionaryPath = Path.Combine(directory, "dictionary.db");
        using (var db = DictionaryDatabase.Create(dictionaryPath))
        {
            db.BeginTransaction();
            var cat = db.InsertWord("cat", "kæt", "kæt", "n:100", "n. 猫", "a small animal", 1775, 0, "zk gk");
            db.InsertSynGroup(cat, "syn", "n.", "tiger, lion");
            var tiger = db.InsertWord("tiger", "", "", "", "n. 老虎", "", 900, 0, "");
            if (tiger >= 0) db.InsertZhTerm("老虎", tiger);
            db.InsertWord("lion", "", "", "", "n. 狮子", "", 890, 0, "");
            db.CommitTransaction();
        }
        using var service = new DictionaryService(dictionaryPath, Path.Combine(directory, "history.db"));
        var window = new MainWindow(service, autoDownload: false);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var searchBox = window.FindControl<TextBox>("SearchBox")!;
        searchBox.Text = "cat";
        PressEnter(searchBox);
        WaitUntil(() => Headword(window) == "cat");

        // A plain Run directly between two InlineUIContainer links keeps its
        // width but draws its glyphs about a line too low (Avalonia 11.3), so
        // the commas used to stray out of the flow; now word and comma live in
        // one atomic WrapPanel entry and never separate, on wrap or otherwise.
        var entries = window.FindControl<StackPanel>("ResultsPanel")!.GetVisualDescendants()
            .OfType<WrapPanel>()
            .Single(w => w.Classes.Contains("word-links"))
            .Children.Cast<StackPanel>().ToList();
        Assert.Equal(2, entries.Count);
        Assert.Equal("n. ", Assert.IsType<SelectableTextBlock>(entries[0].Children[0]).Text);
        Assert.Equal("tiger", Assert.IsType<TextBlock>(Assert.IsType<Border>(entries[0].Children[1]).Child!).Text);
        Assert.Equal(",", Assert.IsType<SelectableTextBlock>(entries[0].Children[2]).Text);
        Assert.True(((SelectableTextBlock)entries[0].Children[2]).Margin.Right > 0, "comma gap missing");
        Assert.Equal("lion", Assert.IsType<TextBlock>(Assert.IsType<Border>(entries[1].Children[0]).Child!).Text);
        Assert.Single(entries[1].Children);
    }

    [AvaloniaFact]
    public void RelatedWordsLineSeparatesWordGlossAndEntries()
    {
        using var service = CreateService();
        var window = new MainWindow(service, autoDownload: false);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var searchBox = window.FindControl<TextBox>("SearchBox")!;
        searchBox.Text = "cat";
        PressEnter(searchBox);
        WaitUntil(() => Headword(window) == "cat");

        // The line is a WrapPanel of whole-entry blocks so a line break can
        // never land on a link — an InlineUIContainer at the break overflows
        // the viewport and clips away instead of wrapping (Avalonia 11.3).
        // Word/gloss/entry gaps are margins: a space run beside the CJK gloss
        // shapes far wider than a Latin space.
        var entry = (StackPanel)window.FindControl<StackPanel>("ResultsPanel")!.GetVisualDescendants()
            .OfType<WrapPanel>()
            .Single(w => w.Classes.Contains("related-words"))
            .Children.Single();
        var link = Assert.IsType<Border>(entry.Children[0]);
        Assert.Equal("tiger", Assert.IsType<TextBlock>(link.Child!).Text);
        Assert.True(link.Margin.Right > 0, "word/gloss gap missing");
        Assert.Equal("老虎", Assert.IsType<SelectableTextBlock>(entry.Children[1]).Text);
        var separator = Assert.IsType<SelectableTextBlock>(entry.Children[2]);
        Assert.Equal(";", separator.Text);
        Assert.True(separator.Margin.Right > 0, "entry gap missing");
        SaveScreenshot(window, "stupiddict-related-words.png");
    }

    [AvaloniaTheory]
    [InlineData(560.0)]
    [InlineData(780.0)]
    [InlineData(1000.0)]
    public void ResultsScrollReachesBottom(double width)
    {
        // ScrollViewer.Padding is broken in Avalonia 11.3: the presenter reports
        // Viewport as its full bounds (padding included) while the extent math
        // drops the padding, so the bottom padding is unreachable however far
        // you scroll and the last result line clips away. The padding therefore
        // lives on the content Panel (Margin), which the extent accounts for;
        // this test pins that at max scroll nothing renders past the viewport.
        var directory = Path.Combine(Path.GetTempPath(), "stupiddict-uitests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var dictionaryPath = Path.Combine(directory, "dictionary.db");
        using (var db = DictionaryDatabase.Create(dictionaryPath))
        {
            db.BeginTransaction();
            var candidates = new List<string>();
            for (var i = 0; i < 20; i++)
            {
                var w = db.InsertWord($"cand{i}", "", "", "", "n. 猫科动物之一类", "", 900 - i, 0, "");
                if (w >= 0) candidates.Add($"cand{i}");
            }
            var cat = db.InsertWord("cat", "kæt", "kæt", "n:100",
                "n. 猫, 恶妇, 猫科动物, 常见的宠物\nvi. 呕吐\nvt. 使呕吐\nn. 猫科动物的统称",
                "a small animal with four legs, especially one kept as a pet and valued for its companionship, " +
                "independent character, and ability to catch mice; domesticated since ancient times", 1775, 0, "zk gk");
            if (cat >= 0)
            {
                db.InsertZhTerm("猫", cat);
                db.InsertSynGroup(cat, "syn", "n.", string.Join(", ", candidates));
                db.InsertSynGroup(cat, "ant", "adj.", "doglike");
            }
            db.CommitTransaction();
        }
        using var service = new DictionaryService(dictionaryPath, Path.Combine(directory, "history.db"));
        var window = new MainWindow(service, autoDownload: false) { Width = width, Height = 460 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var searchBox = window.FindControl<TextBox>("SearchBox")!;
        searchBox.Text = "cat";
        PressEnter(searchBox);
        WaitUntil(() => window.FindControl<StackPanel>("ResultsPanel")!.GetVisualDescendants()
            .OfType<WrapPanel>().Any(w => w.Classes.Contains("related-words")));

        var results = window.FindControl<StackPanel>("ResultsPanel")!;
        var sv = results.GetVisualAncestors().OfType<ScrollViewer>().First();
        Assert.True(sv.Extent.Height > sv.Viewport.Height,
            $"test content must overflow the viewport (extent {sv.Extent.Height}, viewport {sv.Viewport.Height})");

        // First CJK shaping can land after this point on machines without a
        // locally installed CJK font (bare CI runners): the fallback resolution
        // re-measures the thesaurus lines taller, growing Extent under the
        // already-applied offset. "Scroll to bottom" therefore means re-applying
        // the max offset until the reported extent stops moving.
        for (var i = 0; i < 3; i++)
        {
            var target = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
            if (Math.Abs(sv.Offset.Y - target) < 0.5)
            {
                break;
            }
            sv.Offset = new Vector(0, target);
            Dispatcher.UIThread.RunJobs();
        }

        // At max scroll the whole content must sit inside the viewport: any
        // visual bottom past it is unreachable, however far the user scrolls.
        foreach (var visual in new[] { results as Visual }.Concat(results.GetVisualDescendants()))
        {
            var bottom = visual.TranslatePoint(new Point(visual.Bounds.Width, visual.Bounds.Height), sv);
            Assert.True(bottom!.Value.Y <= sv.Bounds.Height + 1.0,
                $"{visual.GetType().Name} bottom {bottom.Value.Y:F1} beyond viewport {sv.Bounds.Height} at width {width}");
        }
        // Deliberately no SaveScreenshot here: a headless capture taken after a
        // scroll can lag the compositor transform and show a stale (pre-scroll)
        // frame, which reads as the bug still being there. The geometry above
        // reads live layout bounds and is the reliable signal.
    }

    [AvaloniaFact]
    public void PhoneticLineShowsBritishAndAmerican()
    {
        using var service = CreateService();
        var window = new MainWindow(service, autoDownload: false);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var searchBox = window.FindControl<TextBox>("SearchBox")!;
        searchBox.Text = "cat";
        PressEnter(searchBox);
        WaitUntil(() => Headword(window) == "cat");

        var labels = window.FindControl<StackPanel>("ResultsPanel")!.GetVisualDescendants()
            .OfType<TextBlock>().Select(b => b.Text).ToList();
        Assert.Contains(labels, t => t is not null && t.StartsWith("英 /"));
        Assert.Contains(labels, t => t is not null && t.StartsWith("美 /"));
        SaveScreenshot(window, "stupiddict-phonetics.png");
    }

    [AvaloniaFact]
    public void SpeakerButtonPlaysThroughInjectedPlayer()
    {
        using var service = CreateService();
        var player = new RecordingSpeechPlayer();
        var window = new MainWindow(service, player, autoDownload: false);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var searchBox = window.FindControl<TextBox>("SearchBox")!;
        searchBox.Text = "cat";
        PressEnter(searchBox);
        WaitUntil(() => Headword(window) == "cat");

        // Speaker buttons are icon-only; find them by the name set in SpeakerButton.
        var ukButton = window.FindControl<StackPanel>("ResultsPanel")!.GetVisualDescendants()
            .OfType<Button>().First(b => b.Name == "UkSpeakerButton");
        RaiseClick(ukButton);
        Dispatcher.UIThread.RunJobs();

        var (word, accent) = Assert.Single(player.Played);
        Assert.Equal("cat", word);
        Assert.Equal(SpeechAccent.British, accent);

        var usButton = window.FindControl<StackPanel>("ResultsPanel")!.GetVisualDescendants()
            .OfType<Button>().First(b => b.Name == "UsSpeakerButton");
        RaiseClick(usButton);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(SpeechAccent.American, player.Played.Last().Accent);
    }

    [AvaloniaFact]
    public void MissingDictionaryShowsDownloadPanelAndBlocksLookup()
    {
        var locations = NewLocations(out var dictionaryPath, out _, out _);
        using var service = new DictionaryService(dictionaryPath, locations.HistoryDatabasePath);
        var window = new MainWindow(service, locations: locations, autoDownload: false);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.True(window.FindControl<StackPanel>("DictionaryDownloadPanel")!.IsVisible);
        Assert.False(window.FindControl<StackPanel>("HintPanel")!.IsVisible);

        var searchBox = window.FindControl<TextBox>("SearchBox")!;
        searchBox.Text = "cat";
        PressEnter(searchBox);
        Dispatcher.UIThread.RunJobs();

        Assert.False(window.FindControl<StackPanel>("ResultsPanel")!.IsVisible);
        Assert.True(window.FindControl<StackPanel>("DictionaryDownloadPanel")!.IsVisible);
    }

    [AvaloniaFact]
    public void DictionaryDownloadCompletesAndEnablesLookup()
    {
        var locations = NewLocations(out var dictionaryPath, out _, out _);

        // The "released" asset: a zip holding a dictionary.db with one word.
        var releaseDirectory = Path.Combine(Path.GetTempPath(), "stupiddict-uitests", Guid.NewGuid().ToString("N"), "release");
        Directory.CreateDirectory(releaseDirectory);
        var dbPath = Path.Combine(releaseDirectory, "dictionary.db");
        using (var db = DictionaryDatabase.Create(dbPath))
            db.InsertWord("cat", "kæt", "kæt", "n:100", "n. 猫", "", 1775, 0, "");
        var zipPath = Path.Combine(Path.GetTempPath(), "stupiddict-uitests", Path.GetFileName(Path.GetDirectoryName(releaseDirectory))!, "dictionary.zip");
        System.IO.Compression.ZipFile.CreateFromDirectory(releaseDirectory, zipPath);

        var downloader = new StubDownloader(asset => asset == ReleaseAssets.DictionaryAsset ? zipPath : null);
        using var service = new DictionaryService(dictionaryPath, locations.HistoryDatabasePath);
        var window = new MainWindow(service, downloader: downloader, locations: locations, autoDownload: true);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(ReleaseAssets.DictionaryAsset, downloader.Requests);
        WaitUntil(() => !window.FindControl<StackPanel>("DictionaryDownloadPanel")!.IsVisible);
        Assert.True(File.Exists(locations.DictionaryDatabasePath));

        var searchBox = window.FindControl<TextBox>("SearchBox")!;
        searchBox.Text = "cat";
        PressEnter(searchBox);
        WaitUntil(() => Headword(window) == "cat");
    }

    [AvaloniaFact]
    public void AudioPackDownloadInstallsIntoAudioDirectory()
    {
        var locations = NewLocations(out var dictionaryPath, out _, out _);
        using (var db = DictionaryDatabase.Create(dictionaryPath))
            db.InsertWord("cat", "kæt", "kæt", "n:100", "n. 猫", "", 1775, 0, "");

        var packDirectory = Path.Combine(Path.GetTempPath(), "stupiddict-uitests", Guid.NewGuid().ToString("N"), "pack");
        Directory.CreateDirectory(Path.Combine(packDirectory, "uk"));
        File.WriteAllBytes(Path.Combine(packDirectory, "uk", "cat.mp3"), [0x49, 0x44, 0x33]);
        var zipPath = Path.Combine(Path.GetTempPath(), "stupiddict-uitests", Path.GetFileName(Path.GetDirectoryName(packDirectory))!, "audio-pack.zip");
        System.IO.Compression.ZipFile.CreateFromDirectory(packDirectory, zipPath);

        var downloader = new StubDownloader(asset => asset == ReleaseAssets.AudioPackAsset ? zipPath : null);
        using var service = new DictionaryService(dictionaryPath, locations.HistoryDatabasePath);
        var window = new MainWindow(service, downloader: downloader, locations: locations, autoDownload: true);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(ReleaseAssets.AudioPackAsset, downloader.Requests);
        WaitUntil(() => !window.FindControl<Border>("AudioPackPanel")!.IsVisible);
        Assert.True(Directory.Exists(Path.Combine(locations.AudioDirectory, "uk")));
        Assert.True(File.Exists(Path.Combine(locations.AudioDirectory, "uk", "cat.mp3")));
    }

    [AvaloniaFact]
    public void ThemeSwitchAppliesVariantAndReRendersResults()
    {
        using var service = CreateService();
        var settings = new AppSettings();
        App.ApplyTheme(AppTheme.Dark);
        try
        {
            var window = new MainWindow(service, settings: settings, autoDownload: false);
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(ThemeVariant.Dark, window.ActualThemeVariant);

            var searchBox = window.FindControl<TextBox>("SearchBox")!;
            searchBox.Text = "cat";
            PressEnter(searchBox);
            WaitUntil(() => Headword(window) == "cat");
            SaveScreenshot(window, "stupiddict-dark.png");

            App.ApplyTheme(AppTheme.Light);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(ThemeVariant.Light, window.ActualThemeVariant);

            // The result page is built in code, so the variant switch must have
            // replayed its render: same content, no stale or missing children.
            Assert.Equal("cat", Headword(window));
            SaveScreenshot(window, "stupiddict-light.png");
        }
        finally
        {
            App.ApplyTheme(AppTheme.System);
        }
    }

    [AvaloniaFact]
    public void SettingsButtonOpensWindowAndThemeChoiceAppliesAndPersists()
    {
        using var service = CreateService();
        // Pin Chinese so the window title assertion holds on any UI culture
        // (System would resolve via the machine's culture).
        var settings = new AppSettings { Language = AppLanguage.SimplifiedChinese };
        var savePath = Path.Combine(Path.GetTempPath(), "stupiddict-uitests",
            Guid.NewGuid().ToString("N"), "settings.json");
        App.WireSettings(settings, savePath);
        try
        {
            var window = new MainWindow(service, settings: settings, autoDownload: false);
            window.Show();
            Dispatcher.UIThread.RunJobs();

            RaiseClick(window.FindControl<Button>("SettingsButton")!);
            Dispatcher.UIThread.RunJobs();
            var settingsWindow = Assert.IsType<SettingsWindow>(Assert.Single(window.OwnedWindows));
            Assert.Equal("设置", settingsWindow.Title);

            var combo = settingsWindow.FindControl<ComboBox>("ThemeComboBox")!;
            Assert.Equal((int)AppTheme.System, combo.SelectedIndex);

            combo.SelectedIndex = (int)AppTheme.Dark;
            Assert.Equal(AppTheme.Dark, settings.Theme);
            Assert.Equal(ThemeVariant.Dark, Application.Current!.RequestedThemeVariant);
            Assert.Equal(AppTheme.Dark, SettingsService.Load(savePath).Theme);
            Dispatcher.UIThread.RunJobs();
            SaveScreenshot(settingsWindow, "stupiddict-settings-dark.png");

            // Reopening focuses the existing window instead of stacking a copy.
            RaiseClick(window.FindControl<Button>("SettingsButton")!);
            Dispatcher.UIThread.RunJobs();
            Assert.Single(window.OwnedWindows);
        }
        finally
        {
            App.ApplyTheme(AppTheme.System);
        }
    }

    [AvaloniaFact]
    public void SettingsWindowIsModalAndCmdCommaTogglesIt()
    {
        using var service = CreateService();
        var window = new MainWindow(service, autoDownload: false);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // Open via the shortcut. The dialog is shown with ShowDialog, so on
        // real platforms the owner is disabled while it is up — headless
        // stubs SetEnabled away, hence no IsEnabled assertion here. The
        // dialog must carry keyboard focus itself (OnOpened focuses the
        // ThemeComboBox); without that the raw key would keep routing to the
        // main window's focused SearchBox, since Avalonia keeps one global
        // focused element across windows.
        window.KeyPress(Key.OemComma, RawInputModifiers.Control, PhysicalKey.Comma, null);
        Dispatcher.UIThread.RunJobs();
        var settingsWindow = Assert.IsType<SettingsWindow>(Assert.Single(window.OwnedWindows));

        // A second ⌘, lands in the dialog and closes it; the shortcut then
        // opens it again.
        settingsWindow.KeyPress(Key.OemComma, RawInputModifiers.Control, PhysicalKey.Comma, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(window.OwnedWindows);

        window.KeyPress(Key.OemComma, RawInputModifiers.Control, PhysicalKey.Comma, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Single(window.OwnedWindows);
    }

    [AvaloniaFact]
    public void EscapeClosesSettingsUnlessComboBoxDropdownOpen()
    {
        using var service = CreateService();
        var window = new MainWindow(service, autoDownload: false);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        window.KeyPress(Key.OemComma, RawInputModifiers.Control, PhysicalKey.Comma, null);
        Dispatcher.UIThread.RunJobs();
        var settingsWindow = Assert.IsType<SettingsWindow>(Assert.Single(window.OwnedWindows));

        // With a ComboBox popup open, Esc belongs to the dropdown: dismiss
        // the popup and leave the window alone.
        var combo = settingsWindow.FindControl<ComboBox>("ThemeComboBox")!;
        combo.Focus();
        combo.IsDropDownOpen = true;
        Dispatcher.UIThread.RunJobs();
        settingsWindow.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(combo.IsDropDownOpen);
        Assert.True(settingsWindow.IsVisible);

        // Otherwise Esc closes the dialog.
        settingsWindow.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(window.OwnedWindows);
    }

    [AvaloniaFact]
    public void CheckUpdateButtonSurfacesNewerRelease()
    {
        Translations.Instance.SetLanguage(AppLanguage.SimplifiedChinese);
        var settings = new AppSettings();
        var checker = new UpdateChecker(
            new FakeHandler(_ => UpdateCheckerTests.RedirectResponse(
                "https://github.com/cholf5/stupid-dict/releases/tag/v9.9.9")),
            currentVersion: "v0.0.1");
        var settingsWindow = new SettingsWindow(settings, checker);
        settingsWindow.Show();
        Dispatcher.UIThread.RunJobs();

        // The update controls live on the About tab (index 3, after the data
        // tab); tab content is instantiated on selection only.
        settingsWindow.FindControl<TabControl>("SettingsTabs")!.SelectedIndex = 3;
        Dispatcher.UIThread.RunJobs();

        RaiseClick(settingsWindow.FindControl<Button>("CheckUpdateButton")!);

        // The fake handler completes synchronously, so the status text has
        // already settled by the time the click returns.
        Assert.Equal("发现新版本 v9.9.9，当前 v0.0.1",
            settingsWindow.FindControl<TextBlock>("UpdateStatusText")!.Text);
        Assert.True(settingsWindow.FindControl<Button>("OpenReleaseButton")!.IsVisible);
        Assert.True(settingsWindow.FindControl<Button>("CheckUpdateButton")!.IsEnabled);
        settingsWindow.Close();
    }

    [AvaloniaFact]
    public void LanguageSwitchLiveRetitlesAndRerendersResults()
    {
        using var service = CreateService();
        var settings = new AppSettings { Language = AppLanguage.SimplifiedChinese };
        var savePath = Path.Combine(Path.GetTempPath(), "stupiddict-uitests",
            Guid.NewGuid().ToString("N"), "settings.json");
        App.WireSettings(settings, savePath);
        try
        {
            var window = new MainWindow(service, settings: settings, autoDownload: false);
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var searchBox = window.FindControl<TextBox>("SearchBox")!;
            searchBox.Text = "cat";
            PressEnter(searchBox);
            WaitUntil(() => Headword(window) == "cat");
            Assert.Contains("近义词", ResultLabels(window));
            Assert.Equal("输入单词或中文，按 Enter 查询",
                window.FindControl<TextBlock>("HintText")!.Text);
            Assert.Equal("傻瓜词典", window.Title);

            RaiseClick(window.FindControl<Button>("SettingsButton")!);
            Dispatcher.UIThread.RunJobs();
            var settingsWindow = (SettingsWindow)Assert.Single(window.OwnedWindows);
            var languageCombo = settingsWindow.FindControl<ComboBox>("LanguageComboBox")!;
            Assert.Equal((int)AppLanguage.SimplifiedChinese, languageCombo.SelectedIndex);
            Assert.Equal("设置", settingsWindow.Title);

            languageCombo.SelectedIndex = (int)AppLanguage.English;
            Dispatcher.UIThread.RunJobs();

            // Both windows retitle live through their Translations bindings and
            // the result page re-renders through the rebuild closure.
            Assert.Equal(AppLanguage.English, settings.Language);
            Assert.Equal(AppLanguage.English, Translations.Instance.CurrentLanguage);
            Assert.Equal("Settings", settingsWindow.Title);
            Assert.Equal("Stupid Dict", window.Title);
            Assert.Equal("Type an English word or Chinese, then press Enter",
                window.FindControl<TextBlock>("HintText")!.Text);
            Assert.Contains("Synonyms", ResultLabels(window));
            Assert.Equal(AppLanguage.English, SettingsService.Load(savePath).Language);
            SaveScreenshot(window, "stupiddict-english.png");

            settingsWindow.Close();
        }
        finally
        {
            App.ApplyTheme(AppTheme.System);
            Translations.Instance.SetLanguage(AppLanguage.SimplifiedChinese);
        }
    }

    [AvaloniaFact]
    public void LanguageSwitchRefreshesSettingsComboOptionsAndKeepsSelection()
    {
        var settings = new AppSettings { Language = AppLanguage.SimplifiedChinese };
        var savePath = Path.Combine(Path.GetTempPath(), "stupiddict-uitests",
            Guid.NewGuid().ToString("N"), "settings.json");
        App.WireSettings(settings, savePath);
        try
        {
            var settingsWindow = new SettingsWindow(settings);
            settingsWindow.Show();
            Dispatcher.UIThread.RunJobs();

            var themeCombo = settingsWindow.FindControl<ComboBox>("ThemeComboBox")!;
            var languageCombo = settingsWindow.FindControl<ComboBox>("LanguageComboBox")!;
            Assert.Equal(0, themeCombo.SelectedIndex);
            Assert.Equal((int)AppLanguage.SimplifiedChinese, languageCombo.SelectedIndex);
            Assert.Contains("跟随系统", ComboTexts(themeCombo));
            Assert.Contains("简体中文", ComboTexts(languageCombo));

            // 走真实链路：选择 English → 共享设置 → App.WireSettings → Translations.SetLanguage
            languageCombo.SelectedIndex = (int)AppLanguage.English;
            Dispatcher.UIThread.RunJobs();

            // 两个下拉框的显示文本都要跟着换语言；选中索引原位保持。
            // 曾有 bug：Avalonia 11.3 ComboBox 选中框对选中项 Content 做快照，
            // 语言切换后主题框仍停留在「跟随系统」。
            Assert.Equal(0, themeCombo.SelectedIndex);
            Assert.Contains("System", ComboTexts(themeCombo));
            Assert.Equal((int)AppLanguage.English, languageCombo.SelectedIndex);
            Assert.Contains("English", ComboTexts(languageCombo));

            // 下拉列表经 ItemTemplate 渲染选项实例，弹层在 PopupRoot 里进不了
            // ComboBox 视觉树，所以选项文案按 Label 断言（同 inpaint 的回归测试）
            Assert.Equal(["System", "Light", "Dark"],
                themeCombo.ItemsSource!.Cast<OptionItem>().Select(o => o.Label).ToList());

            languageCombo.SelectedIndex = (int)AppLanguage.SimplifiedChinese;
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("跟随系统", ComboTexts(themeCombo));
            Assert.Contains("简体中文", ComboTexts(languageCombo));

            settingsWindow.Close();
        }
        finally
        {
            Translations.Instance.SetLanguage(AppLanguage.SimplifiedChinese);
        }
    }

    private static List<string?> ComboTexts(ComboBox combo) =>
        combo.GetVisualDescendants().OfType<TextBlock>().Select(b => b.Text).ToList();

    [AvaloniaFact]
    public void LanguageSwitchRebuildsResultPageExactlyOnce()
    {
        // TC-001 (B-002): SetLanguage raises PropertyChanged for every
        // property (~98) in one synchronous burst; the result page must be
        // rebuilt once per switch, not once per raise. Counted through the
        // ResultRendered seam — one invocation per rebuild-closure run.
        using var service = CreateService();
        var window = new MainWindow(service, autoDownload: false);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var searchBox = window.FindControl<TextBox>("SearchBox")!;
        searchBox.Text = "cat";
        PressEnter(searchBox);
        WaitUntil(() => Headword(window) == "cat");

        var rebuilds = 0;
        window.ResultRendered += () => rebuilds++;
        try
        {
            Translations.Instance.SetLanguage(AppLanguage.English);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(1, rebuilds);
            Assert.Equal("cat", Headword(window));
            Assert.Contains("Synonyms", ResultLabels(window));

            // And once per switch on the way back too.
            Translations.Instance.SetLanguage(AppLanguage.SimplifiedChinese);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, rebuilds);
            Assert.Contains("近义词", ResultLabels(window));
        }
        finally
        {
            Translations.Instance.SetLanguage(AppLanguage.SimplifiedChinese);
        }
    }

    [AvaloniaFact]
    public void LanguageSwitchOnEmptyStateDoesNotRebuildResults()
    {
        // No result page is visible, so a language switch must not render
        // one — the XAML-bound chrome (hint text, title) still refreshes
        // through the untouched per-property raises.
        using var service = CreateService();
        var window = new MainWindow(service, autoDownload: false);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var rebuilds = 0;
        window.ResultRendered += () => rebuilds++;
        try
        {
            Translations.Instance.SetLanguage(AppLanguage.English);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(0, rebuilds);
            Assert.False(window.FindControl<StackPanel>("ResultsPanel")!.IsVisible);
            Assert.Equal("Type an English word or Chinese, then press Enter",
                window.FindControl<TextBlock>("HintText")!.Text);
        }
        finally
        {
            Translations.Instance.SetLanguage(AppLanguage.SimplifiedChinese);
        }
    }

    [AvaloniaFact]
    public void SettingsWindowLanguageSwitchConsolidatesRebindWork()
    {
        // The settings window rebinds six option labels and re-stats the disk
        // (RefreshDataStatus) per language change; all of that shares the same
        // once-per-switch gate as the result page. OptionItem.Label raises
        // INPC on every assignment, so six raises = one consolidated handler
        // run (before the fix each of the ~98 property raises re-ran it).
        var settings = new AppSettings { Language = AppLanguage.SimplifiedChinese };
        var savePath = Path.Combine(Path.GetTempPath(), "stupiddict-uitests",
            Guid.NewGuid().ToString("N"), "settings.json");
        App.WireSettings(settings, savePath);
        // Empty injected layout: the data-status lines read deterministically.
        var locations = NewLocations(out _, out _, out _);
        var settingsWindow = new SettingsWindow(settings, locations: locations);
        settingsWindow.Show();
        Dispatcher.UIThread.RunJobs();

        var labelRaises = 0;
        void CountLabelRaise(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(OptionItem.Label)) labelRaises++;
        }
        foreach (var combo in new[] { "ThemeComboBox", "LanguageComboBox" })
            foreach (var option in settingsWindow.FindControl<ComboBox>(combo)!.ItemsSource!.Cast<OptionItem>())
                option.PropertyChanged += CountLabelRaise;

        try
        {
            // Real chain: select English → shared settings → WireSettings →
            // Translations.SetLanguage.
            settingsWindow.FindControl<ComboBox>("LanguageComboBox")!.SelectedIndex = (int)AppLanguage.English;
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(6, labelRaises);
            Assert.Equal(["System", "Light", "Dark"],
                settingsWindow.FindControl<ComboBox>("ThemeComboBox")!.ItemsSource!.Cast<OptionItem>()
                    .Select(o => o.Label).ToList());
            Assert.Equal("Not installed", settingsWindow.FindControl<TextBlock>("DictionaryDataStatus")!.Text);
            Assert.Equal("Not installed", settingsWindow.FindControl<TextBlock>("AudioPackDataStatus")!.Text);

            settingsWindow.Close();
        }
        finally
        {
            Translations.Instance.SetLanguage(AppLanguage.SimplifiedChinese);
        }
    }

    private static List<string?> ResultLabels(Window window) =>
        window.FindControl<StackPanel>("ResultsPanel")!.GetVisualDescendants()
            .OfType<TextBlock>().Select(b => b.Text).ToList();

    [AvaloniaFact]
    public void SettingsTabsRenderShortcutsAndAbout()
    {
        Translations.Instance.SetLanguage(AppLanguage.SimplifiedChinese);
        var settingsWindow = new SettingsWindow(new AppSettings());
        settingsWindow.Show();
        Dispatcher.UIThread.RunJobs();

        var tabs = settingsWindow.FindControl<TabControl>("SettingsTabs")!;
        tabs.SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();
        var keycaps = settingsWindow.GetVisualDescendants().OfType<TextBlock>()
            .Where(b => b.Parent is Border).Select(b => b.Text).ToList();
        // The modifier keycap follows the platform (⌘ on macOS, Ctrl elsewhere).
        Assert.Contains(SettingsWindow.BackForwardKeycap, keycaps);
        Assert.Contains("Enter", keycaps);

        tabs.SelectedIndex = 3;
        Dispatcher.UIThread.RunJobs();
        var about = settingsWindow.GetVisualDescendants().OfType<TextBlock>()
            .Select(b => b.Text).ToList();
        Assert.Contains("周尔复", about);
        Assert.Contains(SettingsWindow.AppVersion, about);
        Assert.Equal("MIT", about[about.IndexOf("开源许可") + 1]);
        SaveScreenshot(settingsWindow, "stupiddict-settings-about.png");
        settingsWindow.Close();
    }

    [AvaloniaFact]
    public void DataTabShowsStatusAndOpensDataDirectory()
    {
        var locations = NewLocations(out var dictionaryPath, out _, out _);
        var opened = new List<string>();
        var settingsWindow = new SettingsWindow(new AppSettings(),
            locations: locations, openDataDirectory: opened.Add);
        settingsWindow.Show();
        Dispatcher.UIThread.RunJobs();

        // 空目录：两行都是未安装；打开目录按钮交给注入的缝并收到数据目录。
        settingsWindow.FindControl<TabControl>("SettingsTabs")!.SelectedIndex = 2;
        Dispatcher.UIThread.RunJobs();
        var dataTexts = settingsWindow.GetVisualDescendants().OfType<TextBlock>()
            .Select(b => b.Text).ToList();
        Assert.Contains("词典", dataTexts);
        Assert.Contains("发音包", dataTexts);
        Assert.Contains("打开数据目录", dataTexts);
        Assert.Equal("未安装", settingsWindow.FindControl<TextBlock>("DictionaryDataStatus")!.Text);
        Assert.Equal("未安装", settingsWindow.FindControl<TextBlock>("AudioPackDataStatus")!.Text);
        RaiseClick(settingsWindow.FindControl<Button>("OpenDataDirectoryButton")!);
        Assert.Equal([locations.DataDirectory], opened);

        // 装好两件资产后重开窗口：状态按构造时的磁盘求值翻转为已安装。
        using (var db = DictionaryDatabase.Create(dictionaryPath))
            db.InsertWord("cat", "kæt", "kæt", "n:100", "n. 猫", "", 1775, 0, "");
        Directory.CreateDirectory(Path.Combine(locations.AudioDirectory, "uk"));
        var reopened = new SettingsWindow(new AppSettings(),
            locations: locations, openDataDirectory: _ => { });
        reopened.Show();
        Dispatcher.UIThread.RunJobs();
        reopened.FindControl<TabControl>("SettingsTabs")!.SelectedIndex = 2;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("已安装", reopened.FindControl<TextBlock>("DictionaryDataStatus")!.Text);
        Assert.Equal("已安装", reopened.FindControl<TextBlock>("AudioPackDataStatus")!.Text);
        reopened.Close();
        settingsWindow.Close();
    }

    [AvaloniaFact]
    public void OpenDataDirectorySurfacesFailureInDataTab()
    {
        var locations = NewLocations(out _, out _, out _);
        var settingsWindow = new SettingsWindow(new AppSettings(), locations: locations,
            openDataDirectory: _ => throw new InvalidOperationException("boom"));
        settingsWindow.Show();
        Dispatcher.UIThread.RunJobs();
        settingsWindow.FindControl<TabControl>("SettingsTabs")!.SelectedIndex = 2;
        Dispatcher.UIThread.RunJobs();

        RaiseClick(settingsWindow.FindControl<Button>("OpenDataDirectoryButton")!);
        Assert.Equal("打开目录失败：boom",
            settingsWindow.FindControl<TextBlock>("DataActionStatus")!.Text);
        settingsWindow.Close();
    }

    /// <summary>An isolated data layout so asset downloads never touch the user profile.</summary>
    private static AppLocations NewLocations(out string dictionaryPath, out string historyPath, out string audioPath)
    {
        var directory = Path.Combine(Path.GetTempPath(), "stupiddict-uitests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        dictionaryPath = Path.Combine(directory, "dictionary.db");
        historyPath = Path.Combine(directory, "history.db");
        audioPath = Path.Combine(directory, "audio");
        return new AppLocations(directory, dictionaryPath, historyPath, audioPath);
    }

    private sealed class StubDownloader(Func<string, string?> assets) : IAssetDownloader
    {
        public List<string> Requests = [];

        public Task<DownloadResult> DownloadAsync(string assetName, string destinationFile,
            IProgress<DownloadProgress>? progress, CancellationToken cancellation)
        {
            Requests.Add(assetName);
            var source = assets(assetName) ?? throw new InvalidOperationException($"stub has no {assetName}");
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
            File.Copy(source, destinationFile, overwrite: true);
            return Task.FromResult(new DownloadResult(destinationFile, "stub://test"));
        }

        public Task<string?> FetchChecksumAsync(string assetName, CancellationToken cancellation) =>
            Task.FromResult<string?>(null);
    }

    private sealed class RecordingSpeechPlayer : ISpeechPlayer
    {
        public List<(string Word, SpeechAccent Accent)> Played = [];

        public bool Play(string word, SpeechAccent accent)
        {
            Played.Add((word, accent));
            return true;
        }
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
            var cat = db.InsertWord("cat", "kæt", "kæt", "n:100", "n. 猫, 恶妇\nvi. 呕吐",
                "a small animal with four legs, especially one kept as a pet", 1775, 0, "zk gk");
            if (cat >= 0)
            {
                db.InsertZhTerm("猫", cat);
                db.InsertWordForm("cats", cat);
                db.InsertSynGroup(cat, "syn", "n.", "tiger");
                db.InsertSynGroup(cat, "ant", "adj.", "doglike");
            }
            var tiger = db.InsertWord("tiger", "ˈtaɪɡər", "ˈtaɪɡɚ", "n:80", "n. 老虎", "", 900, 0, "zk gk");
            if (tiger >= 0) db.InsertZhTerm("老虎", tiger);
            db.InsertWord("catch", "", "", "", "v. 抓住", "", 900, 0, "gk");
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
