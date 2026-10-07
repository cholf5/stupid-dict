using System.Text.Json;
using System.Text.Json.Serialization;

namespace StupidDict.App.Settings;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as JSON in the user data
/// directory. A missing or corrupt file falls back to defaults, and saving is
/// atomic (tmp file + move) so a crash mid-write cannot corrupt settings.
/// </summary>
internal static class SettingsService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string DefaultPath => Path.Combine(AppPaths.DataDirectory, "settings.json");

    public static AppSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<AppSettings>(stream, Options) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or JsonException or NotSupportedException)
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings, string? path = null)
    {
        path ??= DefaultPath;
        var directory = Path.GetDirectoryName(path);
        if (directory is not null) Directory.CreateDirectory(directory);
        var temporary = path + ".tmp";
        using (var stream = File.Create(temporary))
            JsonSerializer.Serialize(stream, settings, Options);
        File.Move(temporary, path, overwrite: true);
    }
}
