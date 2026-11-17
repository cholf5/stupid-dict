using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using StupidDict.App;
using StupidDict.App.Localization;
using StupidDict.App.Settings;
using StupidDict.App.Tray;
using StupidDict.Core.Application;
using StupidDict.Core.Dictionary;
using Xunit;

namespace StupidDict.App.Tests;

/// <summary>
/// 关闭到托盘的语义：首次关闭询问一次（取消不消费询问）、之后按保存的选择
/// 收进托盘或退出、托盘图标可恢复/退出、没有托盘后端时绝不躲进看不见的角落。
/// headless 没有系统托盘，托盘行为经 <see cref="ITrayHost"/> 注入 recorder。
/// </summary>
public class CloseToTrayTests
{
    public CloseToTrayTests() =>
        Translations.Instance.SetLanguage(AppLanguage.SimplifiedChinese);

    [AvaloniaFact]
    public void FirstCloseAsksAndMinimizingHidesTheWindow()
    {
        var settings = new AppSettings { Language = AppLanguage.SimplifiedChinese };
        var tray = new FakeTrayHost();
        var window = NewWindow(settings);
        var probe = new WindowProbe(window);
        window.TrayHost = tray;
        var asked = 0;
        window.CloseToTrayPromptOverride = () =>
        {
            asked++;
            return Task.FromResult(CloseToTrayChoice.MinimizeToTray);
        };
        window.Show();

        window.Close();
        WaitUntil(() => asked == 1 && !window.IsVisible);

        Assert.True(tray.Shown);
        Assert.True(settings.MinimizeToTrayOnClose);
        Assert.True(settings.CloseToTrayAsked);
        Assert.False(probe.Closed);
    }

    [AvaloniaFact]
    public void QuittingOnTheFirstCloseClosesAndRemembersTheAnswer()
    {
        var settings = new AppSettings { Language = AppLanguage.SimplifiedChinese };
        var tray = new FakeTrayHost();
        var window = NewWindow(settings);
        var probe = new WindowProbe(window);
        window.TrayHost = tray;
        var asked = 0;
        window.CloseToTrayPromptOverride = () =>
        {
            asked++;
            return Task.FromResult(CloseToTrayChoice.Quit);
        };
        window.Show();

        window.Close();
        WaitUntil(() => probe.Closed);

        Assert.Equal(1, asked);
        Assert.Equal(0, tray.ShowCalls);
        Assert.True(settings.CloseToTrayAsked);
        Assert.False(settings.MinimizeToTrayOnClose);
    }

    [AvaloniaFact]
    public void DismissingThePromptKeepsTheWindowOpenAndAsksAgainNextTime()
    {
        var settings = new AppSettings { Language = AppLanguage.SimplifiedChinese };
        var tray = new FakeTrayHost();
        var window = NewWindow(settings);
        window.TrayHost = tray;
        var asked = 0;
        window.CloseToTrayPromptOverride = () =>
        {
            asked++;
            return Task.FromResult(CloseToTrayChoice.Cancel);
        };
        window.Show();

        window.Close();
        WaitUntil(() => asked == 1);

        // Cancel answers nothing: the window stays, the prompt stays pending.
        Assert.True(window.IsVisible);
        Assert.False(settings.CloseToTrayAsked);

        window.CloseToTrayPromptOverride = () =>
        {
            asked++;
            return Task.FromResult(CloseToTrayChoice.Quit);
        };
        window.Close();
        WaitUntil(() => !window.IsVisible);

        Assert.Equal(2, asked);
        Assert.True(settings.CloseToTrayAsked);
    }

    [AvaloniaFact]
    public void AnsweredPromptIsNotAskedAgain()
    {
        var settings = new AppSettings
        {
            Language = AppLanguage.SimplifiedChinese,
            CloseToTrayAsked = true,
            MinimizeToTrayOnClose = true,
        };
        var tray = new FakeTrayHost();
        var window = NewWindow(settings);
        var probe = new WindowProbe(window);
        window.TrayHost = tray;
        window.CloseToTrayPromptOverride = () =>
            throw new InvalidOperationException("the prompt must not run a second time");
        window.Show();

        window.Close();
        WaitUntil(() => !window.IsVisible);

        Assert.True(tray.Shown);
        Assert.False(probe.Closed);
    }

    [AvaloniaFact]
    public void TrayRestoreShowsTheWindowAndRemovesTheIcon()
    {
        var settings = new AppSettings
        {
            Language = AppLanguage.SimplifiedChinese,
            CloseToTrayAsked = true,
            MinimizeToTrayOnClose = true,
        };
        var tray = new FakeTrayHost();
        var window = NewWindow(settings);
        var probe = new WindowProbe(window);
        window.TrayHost = tray;
        window.Show();
        window.Close();
        WaitUntil(() => !window.IsVisible && tray.Restore is not null);

        tray.Restore!();
        WaitUntil(() => window.IsVisible);

        Assert.False(tray.Shown);
        Assert.False(probe.Closed);
        // Restore also puts the caret back in the search box (Activated handler).
        var searchBox = window.FindControl<TextBox>("SearchBox")!;
        WaitUntil(() => searchBox.IsFocused);
    }

    [AvaloniaFact]
    public void TrayQuitClosesTheWindowForReal()
    {
        var settings = new AppSettings
        {
            Language = AppLanguage.SimplifiedChinese,
            CloseToTrayAsked = true,
            MinimizeToTrayOnClose = true,
        };
        var tray = new FakeTrayHost();
        var window = NewWindow(settings);
        var probe = new WindowProbe(window);
        window.TrayHost = tray;
        window.Show();
        window.Close();
        WaitUntil(() => tray.Exit is not null);

        tray.Exit!();
        WaitUntil(() => probe.Closed);

        Assert.True(tray.Disposed);
    }

    [AvaloniaFact]
    public void APlatformWithoutATrayQuitsInsteadOfHiding()
    {
        // A tray preference must never leave the process invisible with no way
        // back: when the platform cannot show the icon the close goes through.
        var settings = new AppSettings
        {
            Language = AppLanguage.SimplifiedChinese,
            CloseToTrayAsked = true,
            MinimizeToTrayOnClose = true,
        };
        var tray = new FakeTrayHost { ShowResult = false };
        var window = NewWindow(settings);
        var probe = new WindowProbe(window);
        window.TrayHost = tray;
        window.Show();

        window.Close();
        WaitUntil(() => probe.Closed);

        Assert.Equal(1, tray.ShowCalls);
        Assert.False(tray.Shown);
    }

    [AvaloniaFact]
    public void ChoosingTrayOnThePromptFallsBackToQuitWhenNoTrayExists()
    {
        var settings = new AppSettings { Language = AppLanguage.SimplifiedChinese };
        var tray = new FakeTrayHost { ShowResult = false };
        var window = NewWindow(settings);
        var probe = new WindowProbe(window);
        window.TrayHost = tray;
        window.CloseToTrayPromptOverride =
            () => Task.FromResult(CloseToTrayChoice.MinimizeToTray);
        window.Show();

        window.Close();
        WaitUntil(() => probe.Closed);

        // The choice is remembered as given (a later platform/session may have
        // a tray); only this close falls back to exiting.
        Assert.True(settings.CloseToTrayAsked);
        Assert.True(settings.MinimizeToTrayOnClose);
    }

    [AvaloniaFact]
    public void RealPromptReportsTheClickedChoice()
    {
        var prompt = new CloseToTrayPrompt();
        prompt.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Null(prompt.Choice);
        prompt.FindControl<Button>("TrayButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(CloseToTrayChoice.MinimizeToTray, prompt.Choice);

        var second = new CloseToTrayPrompt();
        second.Show();
        Dispatcher.UIThread.RunJobs();
        second.FindControl<Button>("QuitButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(CloseToTrayChoice.Quit, second.Choice);
    }

    [AvaloniaFact]
    public void DismissingTheRealPromptReadsAsCancel()
    {
        // Esc (and the title-bar close button) leave Choice null, which the
        // window translates to Cancel — never to an accidental quit.
        var prompt = new CloseToTrayPrompt();
        prompt.Show();
        Dispatcher.UIThread.RunJobs();
        prompt.Close();

        Assert.Null(prompt.Choice);
    }

    [AvaloniaFact]
    public void ThePromptReportsItsAnswerThroughShowDialog()
    {
        // Regression: ShowDialog<TResult> returns whatever was passed to
        // Close(result). Setting Choice and calling the parameterless Close()
        // reports null, which MainWindow reads as Cancel — every button of the
        // real prompt then did nothing while the Choice-only tests stayed green.
        var owner = new Window();
        owner.Show();
        Dispatcher.UIThread.RunJobs();

        var prompt = new CloseToTrayPrompt();
        var task = prompt.ShowDialog<CloseToTrayChoice?>(owner);
        Dispatcher.UIThread.RunJobs();

        prompt.FindControl<Button>("TrayButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.True(task.IsCompleted);
        Assert.Equal(CloseToTrayChoice.MinimizeToTray, task.Result);
        owner.Close();
    }

    [AvaloniaFact]
    public void FirstCloseThroughTheRealPromptHidesTheWindow()
    {
        // End-to-end over the real dialog (no override): answering "minimize to
        // tray" must consume the prompt, remember the choice and hide.
        var settings = new AppSettings { Language = AppLanguage.SimplifiedChinese };
        var tray = new FakeTrayHost();
        var window = NewWindow(settings);
        var probe = new WindowProbe(window);
        window.TrayHost = tray;
        window.Show();

        window.Close();
        WaitUntil(() => window.OwnedWindows.OfType<CloseToTrayPrompt>().Any());

        var prompt = window.OwnedWindows.OfType<CloseToTrayPrompt>().Single();
        prompt.FindControl<Button>("TrayButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        WaitUntil(() => !window.IsVisible);

        Assert.True(settings.CloseToTrayAsked);
        Assert.True(settings.MinimizeToTrayOnClose);
        Assert.True(tray.Shown);
        Assert.False(probe.Closed);
    }

    [AvaloniaFact]
    public void RealTrayHostNeverThrows()
    {
        // Headless has no notification area, so the return value is
        // platform-dependent; what must hold everywhere is that no exception
        // escapes into the close path and Repeat hide/dispose is safe.
        using var host = new TrayIconHost();
        _ = host.Show(() => { }, () => { });
        host.Hide();
        host.Hide();
    }

    [AvaloniaFact]
    public void SettingsCheckboxAnswersTheOneTimePrompt()
    {
        var settings = new AppSettings { Language = AppLanguage.SimplifiedChinese };
        var savePath = Path.Combine(Path.GetTempPath(), "stupiddict-uitests",
            Guid.NewGuid().ToString("N"), "settings.json");
        App.WireSettings(settings, savePath);
        try
        {
            var window = new SettingsWindow(settings);
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var checkbox = window.FindControl<CheckBox>("MinimizeToTrayCheckBox")!;
            Assert.False(checkbox.IsChecked);

            checkbox.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(settings.MinimizeToTrayOnClose);
            // Toggling the checkbox is itself an answer: no prompt on the next
            // close even though this settings object was never asked.
            Assert.True(settings.CloseToTrayAsked);
            Assert.True(SettingsService.Load(savePath).MinimizeToTrayOnClose);
            Assert.True(SettingsService.Load(savePath).CloseToTrayAsked);

            checkbox.IsChecked = false;
            Dispatcher.UIThread.RunJobs();
            Assert.False(settings.MinimizeToTrayOnClose);
            Assert.False(SettingsService.Load(savePath).MinimizeToTrayOnClose);

            window.Close();

            // A reopened window reflects the saved value.
            var second = new SettingsWindow(new AppSettings { MinimizeToTrayOnClose = true });
            second.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.True(second.FindControl<CheckBox>("MinimizeToTrayCheckBox")!.IsChecked);
            second.Close();
        }
        finally
        {
            App.ApplyTheme(AppTheme.System);
            Translations.Instance.SetLanguage(AppLanguage.SimplifiedChinese);
        }
    }

    [AvaloniaFact]
    public void PromptAnswerIsPersistedThroughWiring()
    {
        var settings = new AppSettings { Language = AppLanguage.SimplifiedChinese };
        var savePath = Path.Combine(Path.GetTempPath(), "stupiddict-uitests",
            Guid.NewGuid().ToString("N"), "settings.json");
        App.WireSettings(settings, savePath);
        try
        {
            var window = NewWindow(settings);
            var probe = new WindowProbe(window);
            window.TrayHost = new FakeTrayHost();
            window.CloseToTrayPromptOverride =
                () => Task.FromResult(CloseToTrayChoice.MinimizeToTray);
            window.Show();

            window.Close();
            WaitUntil(() => settings.CloseToTrayAsked);

            var loaded = SettingsService.Load(savePath);
            Assert.True(loaded.CloseToTrayAsked);
            Assert.True(loaded.MinimizeToTrayOnClose);
            Assert.False(probe.Closed);
        }
        finally
        {
            Translations.Instance.SetLanguage(AppLanguage.SimplifiedChinese);
        }
    }

    private static MainWindow NewWindow(AppSettings settings) =>
        new(CreateService(), autoDownload: false, settings: settings);

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

    /// <summary>Closed vs hidden: both have IsVisible == false, only Closed means exit.</summary>
    private sealed class WindowProbe
    {
        public WindowProbe(Window window) => window.Closed += (_, _) => Closed = true;

        public bool Closed { get; private set; }
    }

    private sealed class FakeTrayHost : ITrayHost
    {
        public bool ShowResult { get; init; } = true;
        public bool Shown { get; private set; }
        public int ShowCalls { get; private set; }
        public Action? Restore { get; private set; }
        public Action? Exit { get; private set; }
        public bool Disposed { get; private set; }

        public bool Show(Action restoreWindow, Action exitApplication)
        {
            ShowCalls++;
            if (!ShowResult) return false;
            Shown = true;
            Restore = restoreWindow;
            Exit = exitApplication;
            return true;
        }

        public void Hide()
        {
            Shown = false;
            Restore = null;
            Exit = null;
        }

        public void Dispose()
        {
            Disposed = true;
            Shown = false;
        }
    }
}
