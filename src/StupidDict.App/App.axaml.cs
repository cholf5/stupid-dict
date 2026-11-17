using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using StupidDict.App.Localization;
using StupidDict.App.Settings;
using StupidDict.App.Update;
using StupidDict.Core.Application;

namespace StupidDict.App;

public partial class App : Application
{
    public App()
    {
        // The macOS menu-bar app title is Application.Name: AvaloniaNative pushes it
        // to the native SetApplicationTitle exactly once at platform init, before
        // settings load (the Application ctor default is "Avalonia Application").
        // Seed it from the machine UI culture here, then follow Translations — the
        // same source the window title binds to — replaying the native push on every
        // language change (MacAppTitle).
        Name = Translations.Instance.AppName;
        Translations.Instance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Translations.AppName))
                ApplyAppName();
        };
    }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // macOS bare-process Dock icon for dev runs; the packaged bundle gets it
            // from Info.plist (see MacDockIcon). Posted onto the idle dispatcher so the
            // set lands after NSApplication has finished launching and the window is up
            // (checked isRunning/activationPolicy while debugging); cost is a possible
            // brief flash of the generic icon before the real one lands.
            Dispatcher.UIThread.Post(MacDockIcon.TrySetFromEmbeddedIcon, DispatcherPriority.ApplicationIdle);
            var settings = SettingsService.Load();
            WireSettings(settings);
            // Converge the OS boot entry onto the setting at every start —
            // re-registers after a portable zip moved (stale recorded path)
            // and removes leftovers a settings reset left behind. Headless
            // tests never reach the classic-desktop branch, so this line is
            // production-only by construction.
            AutoStart.Apply(settings.LaunchAtLogin);
            // Self-update: one service per process (App owns it, the window and
            // the settings dialog borrow it). Constructing it also interprets
            // the journal the previous update left behind — and only then does
            // it clear leftover staging folders (a live journal still needs its
            // backup for the rollback).
            var updateService = new UpdateService(new UpdateChecker(), settings);
            var mainWindow = new MainWindow(
                new DictionaryService(AppPaths.DictionaryDatabasePath, AppPaths.HistoryDatabasePath),
                settings: settings, updateService: updateService);
            desktop.MainWindow = mainWindow;
            // Files are replaced (installer started / overlay applied) → this
            // process must exit through the normal lifetime: the old instance
            // holds the exe and the native libraries, and a late exit is what
            // makes an installer report "file in use" or the new instance lose
            // the single-instance lock. Posted because the request arrives from
            // a button handler on the UI thread; Shutdown must not tear the UI
            // down mid-command.
            updateService.RestartRequested +=
                () => Dispatcher.UIThread.Post(() => desktop.Shutdown());
            // Startup update check (weekly throttle, silent unless there is
            // something to say). Headless tests never reach this branch.
            _ = RunStartupUpdateCheckAsync(updateService, mainWindow);
            // Second-launch activation: the guard raises it on its listener thread,
            // hence the dispatcher hop. RestoreFromTray also reopens a window that
            // was closed to the tray; a closed (invisible) window is still ignored,
            // which means the app is already shutting down.
            SingleInstanceGuard.Current?.ActivationRequested +=
                () => Dispatcher.UIThread.Post(() =>
                {
                    if (desktop.MainWindow is MainWindow window)
                        window.RestoreFromTray();
                });
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>How long after startup the update check runs: far enough out that the first dictionary/audio-pack work owns the network and the UI.</summary>
    internal static TimeSpan StartupUpdateCheckDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Startup check throttle: at most one automatic check per week (a manual check is never throttled).</summary>
    internal static TimeSpan StartupUpdateCheckInterval { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// The background startup check. Silent on "up to date" and on failure (an
    /// offline user must not be told anything); a new version brings the window
    /// back and shows the update prompt. Every exception is swallowed — this is
    /// a background nicety, never a startup dependency.
    /// </summary>
    private static async Task RunStartupUpdateCheckAsync(UpdateService updateService, MainWindow window)
    {
        try
        {
            await Task.Delay(StartupUpdateCheckDelay);
            if (!updateService.ShouldAutoCheck(StartupUpdateCheckInterval))
            {
                return;
            }

            var result = await updateService.CheckNowAsync();
            if (result.Outcome == UpdateCheckOutcome.UpdateAvailable)
            {
                // The window may be hidden to the tray by now; ShowUpdatePrompt
                // restores it first.
                Dispatcher.UIThread.Post(window.ShowUpdatePrompt);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[StupidDict] 启动期检查更新失败：{ex.Message}");
        }
    }

    /// <summary>Maps the user's theme choice onto the Avalonia variant; Default follows the OS.</summary>
    internal static void ApplyTheme(AppTheme theme) =>
        Current!.RequestedThemeVariant = theme switch
        {
            AppTheme.Light => ThemeVariant.Light,
            AppTheme.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };

    /// <summary>
    /// Keeps Application.Name and the native macOS menu-bar title on the current
    /// UI language. WireSettings' initial SetLanguage raises AppName, so the boot
    /// path re-pushes the saved language over the machine-culture seed without a
    /// dedicated call here.
    /// </summary>
    internal static void ApplyAppName()
    {
        var name = Translations.Instance.AppName;
        if (Current is { } app)
            app.Name = name;
        MacAppTitle.TrySet(name);
    }

    /// <summary>
    /// Applies theme and language once, then applies and persists every change
    /// so the settings window only has to mutate the shared instance. Tests
    /// pass a save path so they never touch the real settings file.
    /// </summary>
    internal static void WireSettings(AppSettings settings, string? savePath = null)
    {
        ApplyTheme(settings.Theme);
        Localization.Translations.Instance.SetLanguage(settings.Language);
        settings.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(AppSettings.Theme):
                    ApplyTheme(settings.Theme);
                    break;
                case nameof(AppSettings.Language):
                    Localization.Translations.Instance.SetLanguage(settings.Language);
                    break;
                // Window bounds are recorded by MainWindow at close; nothing to
                // apply live, the write below is the whole job.
                case nameof(AppSettings.WindowWidth):
                case nameof(AppSettings.WindowHeight):
                case nameof(AppSettings.WindowMaximized):
                // Translation config is applied on the next lookup; the write
                // below is the whole job.
                case nameof(AppSettings.TranslationApiUrl):
                case nameof(AppSettings.TranslationApiKey):
                case nameof(AppSettings.TranslationModel):
                case nameof(AppSettings.TranslationAutoOnMiss):
                // Boot registration is applied by the settings toggle's seam
                // and reconciled at startup in OnFrameworkInitializationCompleted;
                // the write below is the whole job. Deliberately NOT applied
                // here: tests call WireSettings directly and a side effect in
                // this switch would write the real registry/user files.
                case nameof(AppSettings.LaunchAtLogin):
                // Tray behaviour is read at close time; the write is the job.
                case nameof(AppSettings.MinimizeToTrayOnClose):
                case nameof(AppSettings.CloseToTrayAsked):
                // The update check's timestamp is bookkeeping (the startup
                // throttle reads it next run); nothing to apply live.
                case nameof(AppSettings.LastUpdateCheckUtc):
                    break;
                default:
                    return;
            }
            SettingsService.Save(settings, savePath);
        };
    }
}
