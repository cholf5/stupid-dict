using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StupidDict.App.Localization;
using StupidDict.App.Settings;
using StupidDict.App.Translation;
using StupidDict.Core.Application;
using StupidDict.Core.Dictionary;
using Xunit;

namespace StupidDict.App.Tests;

/// <summary>
/// Translation session semantics on the real window (design decision C):
/// dictionary first — a sentence miss auto-translates when configured and
/// auto is on, a word miss never sends; navigation and cancellation semantics
/// build on this in the C-semantics tests.
/// </summary>
public class TranslationFlowTests
{
    [AvaloniaFact]
    public void SentenceMissAutoTranslatesAndWordMissDoesNot()
    {
        using var service = CreateService();
        var settings = new AppSettings
        {
            TranslationApiUrl = "http://localhost:11434/v1",
            TranslationModel = "fake-model",
            TranslationAutoOnMiss = true,
        };
        var fake = new FakeTranslationService();
        var window = new MainWindow(service, autoDownload: false, settings: settings,
            translationService: fake);
        window.LookupOverride = q => Task.FromResult(LookupResult.NotFound(q, false));
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var searchBox = window.FindControl<TextBox>("SearchBox")!;
        searchBox.Text = "Hello world";
        PressEnter(searchBox);
        WaitUntil(() => fake.Requests.Count == 1);
        Assert.Single(fake.Requests);

        fake.Reset();
        searchBox.Text = "unfindable";
        PressEnter(searchBox);
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(fake.Requests);
        window.Close();
    }

    [AvaloniaFact]
    public void DictionaryHitNeverSendsTranslationRequest()
    {
        using var service = CreateService();
        var settings = new AppSettings
        {
            TranslationApiUrl = "http://localhost:11434/v1",
            TranslationModel = "fake-model",
            TranslationAutoOnMiss = true,
        };
        var fake = new FakeTranslationService();
        var window = new MainWindow(service, autoDownload: false, settings: settings,
            translationService: fake);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var searchBox = window.FindControl<TextBox>("SearchBox")!;
        searchBox.Text = "cat";
        PressEnter(searchBox);
        WaitUntil(() => ResultTexts(window).Contains("cat"));
        Assert.Empty(fake.Requests);
        window.Close();
    }

    [AvaloniaFact]
    public void WordMissKeepsNoResultHeadingAndManualButtonSends()
    {
        using var service = CreateService();
        var settings = ConfiguredSettings();
        var fake = new FakeTranslationService();
        var window = NewWindow(service, settings, fake);
        window.LookupOverride = q => Task.FromResult(LookupResult.NotFound(q, false));
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var searchBox = window.FindControl<TextBox>("SearchBox")!;
        searchBox.Text = "unfindable";
        PressEnter(searchBox);
        Dispatcher.UIThread.RunJobs();

        // 设计文档：单词未命中保留 NoResult + 建议，翻译按钮只是加进来；
        // 且绝不自动发请求。
        Assert.Contains(string.Format(Translations.Instance.NoResultFormat, "unfindable"),
            ResultTexts(window));
        Assert.Empty(fake.Requests);

        RaiseClick(Chip(window, Translations.Instance.TranslateWord));
        WaitUntil(() => fake.CallCount == 1);
        window.Close();
    }

    [AvaloniaFact]
    public void SentenceMissShowsCardWithoutNoResultHeading()
    {
        using var service = CreateService();
        var settings = ConfiguredSettings();
        var fake = new FakeTranslationService();
        var window = NewWindow(service, settings, fake);
        window.LookupOverride = q => Task.FromResult(LookupResult.NotFound(q, false));
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var searchBox = window.FindControl<TextBox>("SearchBox")!;
        searchBox.Text = "Hello world";
        PressEnter(searchBox);
        WaitUntil(() => ResultTexts(window).Contains("假译文：Hello world"));

        // 设计文档：句子未命中时翻译卡是主内容，"没有找到" 标题降为不渲染。
        Assert.DoesNotContain(string.Format(Translations.Instance.NoResultFormat, "Hello world"),
            ResultTexts(window));
        window.Close();
    }

    [AvaloniaFact]
    public void SameQueryEnterIsSingleFlight()
    {
        using var service = CreateService();
        var settings = ConfiguredSettings();
        var fake = new FakeTranslationService();
        var window = NewWindow(service, settings, fake);
        window.LookupOverride = q => Task.FromResult(LookupResult.NotFound(q, false));
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var searchBox = window.FindControl<TextBox>("SearchBox")!;
        fake.Gate = new TaskCompletionSource<TranslationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        searchBox.Text = "Hello world";
        PressEnter(searchBox);
        WaitUntil(() => fake.CallCount == 1);

        searchBox.Text = "Hello world";
        PressEnter(searchBox);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, fake.CallCount);

        fake.CompletePending();
        WaitUntil(() => ResultTexts(window).Contains("延迟完成的译文"));
        Assert.Equal(1, fake.CallCount);
        window.Close();
    }

    [AvaloniaFact]
    public void NewQueryCancelsInFlightTranslation()
    {
        using var service = CreateService();
        var settings = ConfiguredSettings();
        var fake = new FakeTranslationService();
        var window = NewWindow(service, settings, fake);
        window.LookupOverride = q => Task.FromResult(LookupResult.NotFound(q, false));
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var searchBox = window.FindControl<TextBox>("SearchBox")!;
        fake.Gate = new TaskCompletionSource<TranslationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        searchBox.Text = "Hello world";
        PressEnter(searchBox);
        WaitUntil(() => fake.CallCount == 1);
        Assert.False(fake.Tokens[0].IsCancellationRequested);

        // 新查询（单词，不自动发）替换会话：旧请求被取消。
        searchBox.Text = "unfindable";
        PressEnter(searchBox);
        WaitUntil(() => fake.Tokens[0].IsCancellationRequested);
        Assert.Equal(1, fake.CallCount);
        window.Close();
    }

    [AvaloniaFact]
    public void NavigateDoesNotCancelAndCacheRestoresOnReturn()
    {
        using var service = CreateService();
        var settings = ConfiguredSettings();
        var fake = new FakeTranslationService();
        var window = NewWindow(service, settings, fake);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var searchBox = window.FindControl<TextBox>("SearchBox")!;

        // 先查单词建历史，再查句子（自动翻译，gate 挂起在飞）。
        searchBox.Text = "unfindable";
        PressEnter(searchBox);
        Dispatcher.UIThread.RunJobs();
        fake.Gate = new TaskCompletionSource<TranslationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        searchBox.Text = "Hello world";
        PressEnter(searchBox);
        WaitUntil(() => fake.CallCount == 1);

        // 后退到单词页：导航不取消会话。
        RaiseClick(window.FindControl<Button>("NavBackButton")!);
        Dispatcher.UIThread.RunJobs();
        Assert.False(fake.Tokens[0].IsCancellationRequested);

        // 会话在单词页上完成：不渲染（当前页不是该 query），但写入缓存。
        fake.CompletePending();
        WaitUntil(() => ResultTexts(window).All(t => t != "延迟完成的译文"));
        Assert.Equal(1, fake.CallCount);

        // 前进回句子页：缓存命中直接恢复译文，不发新请求。
        RaiseClick(window.FindControl<Button>("NavForwardButton")!);
        WaitUntil(() => ResultTexts(window).Contains("延迟完成的译文"));
        Assert.Equal(1, fake.CallCount);
        window.Close();
    }

    [AvaloniaFact]
    public void ThemeAndLanguageSwitchReplayTranslationWithoutResending()
    {
        using var service = CreateService();
        var settings = ConfiguredSettings();
        var fake = new FakeTranslationService();
        var window = NewWindow(service, settings, fake);
        window.LookupOverride = q => Task.FromResult(LookupResult.NotFound(q, false));
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var searchBox = window.FindControl<TextBox>("SearchBox")!;
        searchBox.Text = "Hello world";
        PressEnter(searchBox);
        WaitUntil(() => ResultTexts(window).Contains("假译文：Hello world"));

        try
        {
            App.ApplyTheme(AppTheme.Dark);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("假译文：Hello world", ResultTexts(window));

            Translations.Instance.SetLanguage(AppLanguage.English);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("假译文：Hello world", ResultTexts(window));
            Assert.Equal(1, fake.CallCount);
        }
        finally
        {
            App.ApplyTheme(AppTheme.System);
            Translations.Instance.SetLanguage(AppLanguage.SimplifiedChinese);
        }
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(560.0)]
    [InlineData(1000.0)]
    public void TranslationCardScrollReachesBottom(double width)
    {
        using var service = CreateService();
        var settings = ConfiguredSettings();
        var fake = new FakeTranslationService();
        var window = new MainWindow(service, autoDownload: false, settings: settings,
            translationService: fake) { Width = width, Height = 460 };
        window.LookupOverride = q => Task.FromResult(LookupResult.NotFound(q, false));
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // 长句翻译卡（16px / 行高 24）撑出可滚动内容。
        var longSentence = string.Join(' ', Enumerable.Repeat(
            "The quick brown fox jumps over the lazy dog near the river bank", 30));
        var searchBox = window.FindControl<TextBox>("SearchBox")!;
        searchBox.Text = longSentence;
        PressEnter(searchBox);
        WaitUntil(() => ResultTexts(window).Contains($"假译文：{longSentence}"));

        var results = window.FindControl<StackPanel>("ResultsPanel")!;
        var sv = results.GetVisualAncestors().OfType<ScrollViewer>().First();
        Assert.True(sv.Extent.Height > sv.Viewport.Height,
            $"translation card must overflow the viewport (extent {sv.Extent.Height}, viewport {sv.Viewport.Height})");

        for (var i = 0; i < 3; i++)
        {
            var target = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
            if (Math.Abs(sv.Offset.Y - target) < 0.5) break;
            sv.Offset = new Vector(0, target);
            Dispatcher.UIThread.RunJobs();
        }

        // At max scroll the whole content (translation card included) must sit
        // inside the viewport: any visual bottom past it is unreachable.
        foreach (var visual in new[] { results as Visual }.Concat(results.GetVisualDescendants()))
        {
            var bottom = visual.TranslatePoint(new Point(visual.Bounds.Width, visual.Bounds.Height), sv);
            Assert.True(bottom!.Value.Y <= sv.Bounds.Height + 1.0,
                $"{visual.GetType().Name} bottom {bottom.Value.Y:F1} beyond viewport {sv.Bounds.Height} at width {width}");
        }
        window.Close();
    }

    [AvaloniaFact]
    public void AuthFailurePausesAutoTranslationUntilManualRetry()
    {
        // 设计（错误与熔断）：首次 401/403/404 后暂停自动翻译——后续句子未
        // 命中不再自动发请求；手动按钮 = 用户显式重试，ResetCircuit 恢复。
        using var service = CreateService();
        var settings = ConfiguredSettings();
        var fake = new FakeTranslationService { FailWith = TranslationFailureKind.Authentication };
        var window = NewWindow(service, settings, fake);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var searchBox = window.FindControl<TextBox>("SearchBox")!;
        searchBox.Text = "Hello world";
        PressEnter(searchBox);
        WaitUntil(() => fake.CallCount == 1);

        searchBox.Text = "How are you";
        PressEnter(searchBox);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, fake.CallCount); // 熔断中：不再自动发
        fake.FailWith = null;
        RaiseClick(Chip(window, Translations.Instance.TranslateSentence)); // 手动重试
        WaitUntil(() => fake.CallCount == 2);
        Assert.False(fake.AutoPaused); // 手动重试恢复熔断
        window.Close();
    }

    [AvaloniaFact]
    public void AutoOffSentenceMissShowsManualButtonNotRequest()
    {
        using var service = CreateService();
        var settings = ConfiguredSettings();
        settings.TranslationAutoOnMiss = false;
        var fake = new FakeTranslationService();
        var window = NewWindow(service, settings, fake);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var searchBox = window.FindControl<TextBox>("SearchBox")!;
        searchBox.Text = "Hello world";
        PressEnter(searchBox);
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(fake.Requests);
        RaiseClick(Chip(window, Translations.Instance.TranslateSentence));
        WaitUntil(() => fake.CallCount == 1);
        window.Close();
    }

    [AvaloniaFact]
    public void UnconfiguredSentenceMissShowsConfigEntryNotRequest()
    {
        using var service = CreateService();
        var fake = new FakeTranslationService();
        var window = new MainWindow(service, autoDownload: false, translationService: fake);
        window.LookupOverride = q => Task.FromResult(LookupResult.NotFound(q, false));
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var searchBox = window.FindControl<TextBox>("SearchBox")!;
        searchBox.Text = "Hello world";
        PressEnter(searchBox);
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(fake.Requests);
        Assert.Contains(Translations.Instance.TranslationNotConfigured, ResultTexts(window));
        window.Close();
    }

    [AvaloniaFact]
    public void ClearingQueryCancelsInFlightTranslation()
    {
        using var service = CreateService();
        var settings = ConfiguredSettings();
        var fake = new FakeTranslationService();
        var window = NewWindow(service, settings, fake);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var searchBox = window.FindControl<TextBox>("SearchBox")!;
        fake.Gate = new TaskCompletionSource<TranslationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        searchBox.Text = "Hello world";
        PressEnter(searchBox);
        WaitUntil(() => fake.CallCount == 1);

        // Esc/清空 → 空查询 → ShowEmptyState → 取消在途请求（设计决策 C）。
        searchBox.Text = "";
        PressEnter(searchBox);
        WaitUntil(() => fake.Tokens[0].IsCancellationRequested);
        window.Close();
    }

    // ---- helpers ----

    private static AppSettings ConfiguredSettings() => new()
    {
        TranslationApiUrl = "http://localhost:11434/v1",
        TranslationModel = "fake-model",
        TranslationAutoOnMiss = true,
    };

    private static MainWindow NewWindow(DictionaryService service, AppSettings settings,
        FakeTranslationService fake)
    {
        var window = new MainWindow(service, autoDownload: false, settings: settings,
            translationService: fake);
        window.LookupOverride = q => Task.FromResult(LookupResult.NotFound(q, false));
        return window;
    }

    private static List<string?> ResultTexts(MainWindow window) =>
        window.FindControl<StackPanel>("ResultsPanel")!.GetVisualDescendants()
            .OfType<TextBlock>().Select(b => b.Text).ToList();

    private static Button Chip(MainWindow window, string content) =>
        window.FindControl<StackPanel>("ResultsPanel")!.GetVisualDescendants()
            .OfType<Button>().Single(b => b.Content as string == content);

    private static void RaiseClick(Button button) =>
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static DictionaryService CreateService()
    {
        var directory = Path.Combine(Path.GetTempPath(), "stupiddict-flowtests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var dictionaryPath = Path.Combine(directory, "dictionary.db");
        using (var db = DictionaryDatabase.Create(dictionaryPath))
        {
            db.InsertWord("cat", "kæt", "kæt", "n:100", "n. 猫", "", 1775, 0, "");
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
}
