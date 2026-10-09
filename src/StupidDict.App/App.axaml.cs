using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using StupidDict.App.Localization;
using StupidDict.App.Settings;
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
            desktop.MainWindow = new MainWindow(
                new DictionaryService(AppPaths.DictionaryDatabasePath, AppPaths.HistoryDatabasePath),
                settings: settings);
        }
        base.OnFrameworkInitializationCompleted();
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
                    break;
                default:
                    return;
            }
            SettingsService.Save(settings, savePath);
        };
    }
}
