using System.Net;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StupidDict.App;
using StupidDict.App.Localization;
using StupidDict.App.Settings;
using StupidDict.App.Tray;
using StupidDict.Core.Application;
using StupidDict.Core.Dictionary;
using Xunit;

namespace StupidDict.App.Tests;

/// <summary>
/// 常驻托盘（左键迷你查词 / 右键四项菜单）与迷你查词窗本身。托盘经
/// <see cref="ITrayHost"/> 注入 recorder，查询经 <c>LookupOverride</c> 注入，
/// 更新检查走假 HTTP handler——零真实托盘、零真实数据、零网络。
/// </summary>
public class TrayTests
{
    public TrayTests() => Translations.Instance.SetLanguage(AppLanguage.SimplifiedChinese);

    // ---- 托盘接线 ----

    [AvaloniaFact]
    public void StartupCreatesTheTrayIconWithEveryAction()
    {
        var tray = new FakeTrayHost();
        var window = NewWindow(new AppSettings { Language = AppLanguage.SimplifiedChinese }, tray);
        window.Show();
        WaitUntil(() => tray.Actions is not null);

        var actions = tray.Actions!;
        Assert.True(tray.Shown);
        Assert.Equal(1, tray.ShowCalls);
        Assert.NotNull(actions.QuickLookup);
        Assert.NotNull(actions.ShowWindow);
        Assert.NotNull(actions.OpenSettings);
        Assert.NotNull(actions.CheckUpdate);
        Assert.NotNull(actions.Exit);
    }

    [AvaloniaFact]
    public void TrayIsDisposedWhenTheWindowCloses()
    {
        var tray = new FakeTrayHost();
        var settings = new AppSettings
        {
            Language = AppLanguage.SimplifiedChinese,
            CloseToTrayAsked = true,
        };
        var window = NewWindow(settings, tray);
        window.Show();
        WaitUntil(() => tray.Shown);

        window.Close();
        WaitUntil(() => tray.Disposed);
    }

    [AvaloniaFact]
    public void LeftClickOpensAndClosesTheQuickLookupCard()
    {
        var tray = new FakeTrayHost();
        var service = CreateService();
        var window = NewWindow(new AppSettings { Language = AppLanguage.SimplifiedChinese }, tray, service);
        QuickLookupWindow? quick = null;
        EditQuickLookup(window, service, w => quick = w);
        window.Show();
        WaitUntil(() => tray.Actions is not null);

        tray.Actions!.QuickLookup();
        Assert.True(quick!.IsVisible);

        tray.Actions!.QuickLookup();
        Assert.False(quick!.IsVisible);
    }

    [AvaloniaFact]
    public void AFocusLossSelfHideSwallowsTheClickThatCausedIt()
    {
        // Windows: the taskbar takes focus on mouse-down (the card hides itself)
        // and Clicked arrives on mouse-up. That click must leave the card
        // closed, and the next click must open it again.
        var tray = new FakeTrayHost();
        var service = CreateService();
        var window = NewWindow(new AppSettings { Language = AppLanguage.SimplifiedChinese }, tray, service);
        QuickLookupWindow? quick = null;
        EditQuickLookup(window, service, w => quick = w);
        window.Show();
        WaitUntil(() => tray.Actions is not null);

        // Open it, then replay what this click already did on Windows: the
        // taskbar took focus and the card hid itself before Clicked arrived.
        tray.Actions!.QuickLookup();
        var card = quick!;
        Assert.True(card.IsVisible);
        card.LastAutoHiddenUtc = DateTimeOffset.UtcNow;
        card.HideQuickLookup();

        tray.Actions!.QuickLookup();
        Assert.False(card.IsVisible);

        // The guard is consumed by that one click: the next click opens again.
        card.LastAutoHiddenUtc = default;
        tray.Actions!.QuickLookup();
        Assert.True(card.IsVisible);
    }

    [AvaloniaFact]
    public void WithoutDictionaryDataTheLeftClickOpensTheMainWindow()
    {
        // The card could only report "not found" before the first download, so
        // the click goes where the download panel lives.
        var tray = new FakeTrayHost();
        var window = new MainWindow(
            new DictionaryService(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.db"),
                Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "history.db")),
            autoDownload: false, settings: new AppSettings { Language = AppLanguage.SimplifiedChinese });
        window.TrayHost = tray;
        var cardCreated = false;
        window.QuickLookupFactoryOverride = () =>
        {
            cardCreated = true;
            return new QuickLookupWindow(() => throw new InvalidOperationException("not created"));
        };
        window.Show();
        WaitUntil(() => tray.Actions is not null);

        tray.Actions!.QuickLookup();
        Dispatcher.UIThread.RunJobs();

        Assert.True(window.IsVisible);
        Assert.False(cardCreated);
    }

    [AvaloniaFact]
    public void MenuShowWindowRestoresTheHiddenWindow()
    {
        var tray = new FakeTrayHost();
        var settings = new AppSettings
        {
            Language = AppLanguage.SimplifiedChinese,
            CloseToTrayAsked = true,
            MinimizeToTrayOnClose = true,
        };
        var window = NewWindow(settings, tray);
        window.Show();
        WaitUntil(() => tray.Actions is not null);

        window.Close();
        WaitUntil(() => !window.IsVisible);

        tray.Actions!.ShowWindow();
        WaitUntil(() => window.IsVisible);
        Assert.True(tray.Shown);
    }

    [AvaloniaFact]
    public void MenuSettingsRestoresTheWindowAndOpensTheDialog()
    {
        var tray = new FakeTrayHost();
        var settings = new AppSettings
        {
            Language = AppLanguage.SimplifiedChinese,
            CloseToTrayAsked = true,
            MinimizeToTrayOnClose = true,
        };
        var window = NewWindow(settings, tray);
        window.Show();
        WaitUntil(() => tray.Actions is not null);
        window.Close();
        WaitUntil(() => !window.IsVisible);

        tray.Actions!.OpenSettings();
        WaitUntil(() => window.IsVisible && window._settingsWindow is not null);

        var dialog = window._settingsWindow!;
        Assert.Equal(0, dialog.FindControl<TabControl>("SettingsTabs")!.SelectedIndex);
        dialog.Close();
    }

    [AvaloniaFact]
    public void MenuCheckUpdateOpensTheAboutTabAndRunsTheCheck()
    {
        // The status line only exists in the settings dialog, so the tray entry
        // opens it on the About tab and starts the check there.
        var tray = new FakeTrayHost();
        var window = NewWindow(new AppSettings { Language = AppLanguage.SimplifiedChinese }, tray);
        window.UpdateCheckerOverride = new UpdateChecker(
            new FakeHandler(_ => Redirect("https://github.com/cholf5/stupid-dict/releases/tag/v9.9.9")),
            "v1.0.0");
        window.Show();
        WaitUntil(() => tray.Actions is not null);

        tray.Actions!.CheckUpdate();
        WaitUntil(() => window._settingsWindow?.FindControl<TabControl>("SettingsTabs")?.SelectedIndex
            == SettingsWindow.AboutTabIndex);

        var dialog = window._settingsWindow!;
        // The constant must name the About tab, not merely a valid index.
        var tabs = dialog.FindControl<TabControl>("SettingsTabs")!;
        var about = tabs.Items[tabs.SelectedIndex] as TabItem;
        Assert.Equal(Translations.Instance.SectionAbout, about?.Header);

        var status = dialog.FindControl<TextBlock>("UpdateStatusText")!;
        WaitUntil(() => status.Text == string.Format(Translations.Instance.UpdateAvailableStatus, "v9.9.9", "v1.0.0"));
        dialog.Close();
    }

    [AvaloniaFact]
    public void MenuQuitClosesTheWindowAndRemovesTheIcon()
    {
        var tray = new FakeTrayHost();
        var settings = new AppSettings
        {
            Language = AppLanguage.SimplifiedChinese,
            CloseToTrayAsked = true,
            MinimizeToTrayOnClose = true,
        };
        var service = CreateService();
        var window = NewWindow(settings, tray, service);
        QuickLookupWindow? quick = null;
        EditQuickLookup(window, service, w => quick = w);
        window.Show();
        WaitUntil(() => tray.Actions is not null);
        window.Close();
        WaitUntil(() => !window.IsVisible);

        // The card is a second top-level window: quitting must take it with the
        // main window, otherwise OnLastWindowClose would never reach zero.
        tray.Actions!.QuickLookup();
        var card = quick!;
        Assert.True(card.IsVisible);

        tray.Actions!.Exit();
        WaitUntil(() => tray.Disposed);

        Assert.False(card.IsVisible);
    }

    // ---- 迷你查词窗 ----

    [AvaloniaFact]
    public void TypingRendersTheHeadwordSensesAndPhonetics()
    {
        var service = CreateService();
        var quick = NewQuickLookup(service, _ => Task.FromResult(EnglishResult("cat")));
        quick.ShowQuickLookup(new PixelPoint(10, 10));
        var box = quick.FindControl<TextBox>("QueryBox")!;

        box.Text = "cat";
        WaitUntil(() => FindText(quick, "cat") && FindText(quick, "猫"));

        Assert.True(quick.FindControl<ScrollViewer>("ResultScroll")!.IsVisible);
        // One speaker button per accent.
        Assert.Equal(2, quick.GetVisualDescendants().OfType<Button>().Count(b => b.Classes.Contains("spk")));
        quick.Close();
    }

    [AvaloniaFact]
    public void UnknownWordOffersSuggestionsAndNoSenses()
    {
        var service = CreateService();
        var result = new LookupResult
        {
            Query = "catt",
            IsChineseQuery = false,
            Tier = MatchTier.Fuzzy,
            WordSuggestions =
            [
                new DictionaryEntry("cat", "", "", "n.", "猫", "", "", 0, 0),
                new DictionaryEntry("cats", "", "", "n.", "猫（复数）", "", "", 0, 0),
            ],
        };
        var quick = NewQuickLookup(service, _ => Task.FromResult(result));
        quick.ShowQuickLookup(new PixelPoint(10, 10));
        var box = quick.FindControl<TextBox>("QueryBox")!;

        box.Text = "catt";
        WaitUntil(() => FindText(quick, Translations.Instance.DidYouMean));

        // Clicking a suggestion refills the box instead of opening the entry.
        var chips = quick.GetVisualDescendants().OfType<Button>()
            .Where(b => b.Classes.Contains("chip")).ToList();
        Assert.Equal(2, chips.Count);
        chips[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("cat", box.Text);
        quick.Close();
    }

    [AvaloniaFact]
    public void EscapeHidesTheCard()
    {
        var service = CreateService();
        var quick = NewQuickLookup(service, _ => Task.FromResult(EnglishResult("cat")));
        quick.ShowQuickLookup(new PixelPoint(10, 10));

        quick.FindControl<TextBox>("QueryBox")!.RaiseEvent(
            new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });

        Assert.False(quick.IsVisible);
        // Our own hide is not a focus loss: the next tray click must open again.
        Assert.Equal(default, quick.LastAutoHiddenUtc);
    }

    [AvaloniaFact]
    public void EnterOpensTheEntryInTheMainWindow()
    {
        var tray = new FakeTrayHost();
        var service = CreateService();
        var window = NewWindow(new AppSettings { Language = AppLanguage.SimplifiedChinese }, tray, service);
        window.LookupOverride = _ => Task.FromResult(EnglishResult("cat"));
        QuickLookupWindow? quick = null;
        EditQuickLookup(window, service, w => quick = w,
            _ => Task.FromResult(EnglishResult("cat")));
        window.Show();
        WaitUntil(() => tray.Actions is not null);

        tray.Actions!.QuickLookup();
        var card = quick!;
        card.FindControl<TextBox>("QueryBox")!.Text = "ca";
        WaitUntil(() => FindText(card, "cat"));

        card.FindControl<TextBox>("QueryBox")!.RaiseEvent(
            new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });

        var searchBox = window.FindControl<TextBox>("SearchBox")!;
        WaitUntil(() => searchBox.Text == "cat");
        Assert.False(card.IsVisible);
        Assert.True(window.IsVisible);
    }

    // ---- helpers ----

    private static LookupResult EnglishResult(string word) => new()
    {
        Query = word,
        IsChineseQuery = false,
        Tier = MatchTier.Exact,
        Primary = new DictionaryEntry(word, "kæt", "kæt", "n.", "猫\n猫科动物", "a small animal", "", 0, 0),
    };

    private static void EditQuickLookup(MainWindow window, DictionaryService service,
        Action<QuickLookupWindow> capture, Func<string, Task<LookupResult>>? lookup = null)
    {
        window.QuickLookupFactoryOverride = () =>
        {
            var quick = new QuickLookupWindow(() => service) { LookupOverride = lookup };
            capture(quick);
            return quick;
        };
    }

    private static QuickLookupWindow NewQuickLookup(DictionaryService service,
        Func<string, Task<LookupResult>>? lookup)
    {
        var quick = new QuickLookupWindow(() => service) { LookupOverride = lookup };
        return quick;
    }

    private static HttpResponseMessage Redirect(string location) => new(HttpStatusCode.Found)
    {
        Headers = { Location = new Uri(location) },
    };

    private static bool FindText(Visual root, string needle) =>
        root.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains(needle, StringComparison.Ordinal) == true);

    private static MainWindow NewWindow(AppSettings settings, ITrayHost tray,
        DictionaryService? service = null)
    {
        var window = new MainWindow(service ?? CreateService(), autoDownload: false, settings: settings);
        window.TrayHost = tray;
        return window;
    }

    private static DictionaryService CreateService()
    {
        var directory = Path.Combine(Path.GetTempPath(), "stupiddict-traytests", Guid.NewGuid().ToString("N"));
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

    private sealed class FakeTrayHost : ITrayHost
    {
        public bool Shown { get; private set; }
        public int ShowCalls { get; private set; }
        public TrayActions? Actions { get; private set; }
        public bool Disposed { get; private set; }

        public bool Show(TrayActions actions)
        {
            ShowCalls++;
            Shown = true;
            Actions = actions;
            return true;
        }

        public void Dispose()
        {
            Disposed = true;
            Shown = false;
        }
    }
}

/// <summary>
/// Card anchoring and the Cocoa→Avalonia coordinate flip: pure functions, so
/// plain [Fact] — and deliberately no Translations setup in a constructor, that
/// would run off the UI thread while MainWindow instances from other tests are
/// still subscribed to language changes.
/// </summary>
public class PopoverPositionerTests
{
[Fact]
public void AboveCursorSitsOverTheIconAndClampsIntoTheWorkArea()
{
    var work = new PixelRect(0, 0, 1920, 1040);
    var card = new PixelSize(440, 320);

    var overIcon = PopoverPositioner.AboveCursor(work, card, new PixelPoint(1800, 1050), 8, 0);
    Assert.Equal(1800 - 440, overIcon.X);
    // 1050-320-8 = 722 would overflow the work area (bottom 1040): clamped to 720.
    Assert.Equal(1040 - 320, overIcon.Y);

    // Off the right/top edge: clamped, never partly outside.
    var clamped = PopoverPositioner.AboveCursor(work, card, new PixelPoint(2000, 20), 8, 0);
    Assert.Equal(1920 - 440, clamped.X);
    Assert.Equal(0, clamped.Y);
}

[Fact]
public void BelowAnchorHangsUnderTheStatusItem()
{
    var work = new PixelRect(0, 24, 1920, 1040);
    var card = new PixelSize(440, 320);
    var icon = new PixelRect(1200, 0, 24, 24);

    var point = PopoverPositioner.BelowAnchor(work, card, icon, 6);
    Assert.Equal(1200, point.X);
    Assert.Equal(24 + 6, point.Y);

    // A status item near the right edge pulls the card back inside.
    var edge = PopoverPositioner.BelowAnchor(work, card, new PixelRect(1900, 0, 24, 24), 6);
    Assert.Equal(1920 - 440, edge.X);
}

[Fact]
public void CornerFallbackFollowsThePanelEdge()
{
    var card = new PixelSize(440, 320);

    // Panel 40px at the bottom: the card parks above it, bottom-right.
    var bottomPanel = PopoverPositioner.InCorner(
        new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), card, 8);
    Assert.Equal(1920 - 440 - 8, bottomPanel.X);
    Assert.Equal(1040 - 320 - 8, bottomPanel.Y);

    // Panel 40px at the top: top-right instead.
    var topPanel = PopoverPositioner.InCorner(
        new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 40, 1920, 1040), card, 8);
    Assert.Equal(40 + 8, topPanel.Y);
}

[Fact]
public void CocoaFramesFlipOntoAvaloniaCoordinates()
{
    // A 1920x1080 main screen (Cocoa maxY = 1080). A status item occupying
    // cocoa y 1056..1080 is the top 24 points of the Avalonia screen (y 0..24).
    var rect = MacStatusItem.ConvertCocoaFrameToAvaloniaCoords(1600, 1056, 24, 24, 1080);
    Assert.Equal(new PixelRect(1600, 0, 24, 24), rect);
}
}
