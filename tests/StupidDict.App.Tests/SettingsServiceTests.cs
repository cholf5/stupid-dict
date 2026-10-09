using StupidDict.App.Settings;
using Xunit;

namespace StupidDict.App.Tests;

public class SettingsServiceTests
{
    [Fact]
    public void RoundTripsTheme()
    {
        var path = NewPath();
        var settings = new AppSettings { Theme = AppTheme.Dark };

        SettingsService.Save(settings, path);

        Assert.Equal(AppTheme.Dark, SettingsService.Load(path).Theme);
    }

    [Fact]
    public void MissingFileFallsBackToDefaults()
    {
        Assert.Equal(AppTheme.System, SettingsService.Load(NewPath()).Theme);
    }

    [Fact]
    public void CorruptFileFallsBackToDefaults()
    {
        var path = NewPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not json at all");

        Assert.Equal(AppTheme.System, SettingsService.Load(path).Theme);
    }

    [Fact]
    public void UnknownThemeNameFallsBackToDefaults()
    {
        var path = NewPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{ "Theme": "Neon" }""");

        Assert.Equal(AppTheme.System, SettingsService.Load(path).Theme);
    }

    [Fact]
    public void RoundTripsLanguage()
    {
        var path = NewPath();
        var settings = new AppSettings { Language = AppLanguage.English };

        SettingsService.Save(settings, path);

        Assert.Equal(AppLanguage.English, SettingsService.Load(path).Language);
    }

    [Fact]
    public void UnknownLanguageFallsBackToDefaults()
    {
        var path = NewPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{ "Language": "Klingon" }""");

        Assert.Equal(AppLanguage.System, SettingsService.Load(path).Language);
    }

    [Fact]
    public void SavedFileUsesEnumNamesAndIsHandEditable()
    {
        var path = NewPath();
        SettingsService.Save(new AppSettings { Theme = AppTheme.Dark }, path);

        Assert.Contains("\"Theme\": \"Dark\"", File.ReadAllText(path));
    }

    [Fact]
    public void RoundTripsWindowBounds()
    {
        var path = NewPath();
        var settings = new AppSettings { WindowWidth = 900.5, WindowHeight = 640.25, WindowMaximized = true };

        SettingsService.Save(settings, path);

        var loaded = SettingsService.Load(path);
        Assert.Equal(900.5, loaded.WindowWidth);
        Assert.Equal(640.25, loaded.WindowHeight);
        Assert.True(loaded.WindowMaximized);
    }

    [Fact]
    public void MissingWindowBoundsFallBackToDefaults()
    {
        var path = NewPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{ "Theme": "Dark" }""");

        var loaded = SettingsService.Load(path);

        Assert.Equal(AppTheme.Dark, loaded.Theme);
        Assert.Null(loaded.WindowWidth);
        Assert.Null(loaded.WindowHeight);
        Assert.False(loaded.WindowMaximized);
    }

    [Fact]
    public void SaveToUnwritableLocationDoesNotThrow()
    {
        // A file where the settings directory should be: CreateDirectory
        // throws IOException, and persistence is best-effort — the write
        // rides property changes (including window close), which must never
        // crash over a locked or unwritable settings file.
        var directory = Path.Combine(Path.GetTempPath(), "stupiddict-settings-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var blocked = Path.Combine(directory, "blocked");
        File.WriteAllText(blocked, "occupied");
        var path = Path.Combine(blocked, "settings.json");

        SettingsService.Save(new AppSettings { Theme = AppTheme.Dark }, path);

        Assert.False(File.Exists(path));
    }

    private static string NewPath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "stupiddict-settings-tests", Guid.NewGuid().ToString("N"));
        return Path.Combine(directory, "settings.json");
    }
}
