using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using StupidDict.App.Settings;
using StupidDict.Core.Application;

namespace StupidDict.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
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
                default:
                    return;
            }
            SettingsService.Save(settings, savePath);
        };
    }
}
