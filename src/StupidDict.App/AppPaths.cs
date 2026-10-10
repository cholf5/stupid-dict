namespace StupidDict.App;

/// <summary>
/// Where the app finds its data. The dictionary ships next to the executable
/// when packaged — or in a macOS .app bundle's Contents/Resources, where the
/// code seal requires data files to live; otherwise it falls to the user data
/// directory (built by StupidDict.DataBuilder). History always lives in the
/// user data directory.
/// </summary>
internal static class AppPaths
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "StupidDict");

    public static string HistoryDatabasePath { get; } = Path.Combine(DataDirectory, "history.db");

    public static string DictionaryDatabasePath { get; } =
        ResolveBundledFile("dictionary.db", AppContext.BaseDirectory)
            ?? Path.Combine(DataDirectory, "dictionary.db");

    /// <summary>
    /// The pronunciation pack as a single SQLite database (word → MP3 blobs,
    /// built by Assets/AudioPackConverter from the downloaded zip). Always in
    /// the user data directory — it is produced, not shipped.
    /// </summary>
    public static string AudioPackDatabasePath { get; } = Path.Combine(DataDirectory, "audio-pack.db");

    /// <summary>
    /// Legacy loose layout (uk/us per-word MP3 directories) extracted by app
    /// versions before the database store; kept as a lookup fallback so
    /// existing installs keep working. New installs never create it.
    /// </summary>
    public static string AudioDirectory { get; } =
        ResolveBundledDirectory("audio", AppContext.BaseDirectory)
            ?? Path.Combine(DataDirectory, "audio");

    /// <summary>
    /// Bundled data first: adjacent to the executable (win/linux zip layout),
    /// then Contents/Resources — inside a macOS .app the code seal treats
    /// everything in MacOS/ beyond the main executable as nested code and
    /// refuses to sign a plain data file there, so the with-dictionary
    /// package carries dictionary.db in Resources. Null when neither exists,
    /// letting the caller fall through to the user data directory; the
    /// Resources step simply misses on other layouts (no ../Resources there).
    /// Split from the static properties for tests: AppContext.BaseDirectory
    /// cannot be faked in place.
    /// </summary>
    internal static string? ResolveBundledFile(string name, string baseDirectory)
    {
        var exeAdjacent = Path.Combine(baseDirectory, name);
        if (File.Exists(exeAdjacent)) return exeAdjacent;
        var inResources = Path.Combine(baseDirectory, "..", "Resources", name);
        return File.Exists(inResources) ? inResources : null;
    }

    internal static string? ResolveBundledDirectory(string name, string baseDirectory)
    {
        var exeAdjacent = Path.Combine(baseDirectory, name);
        if (Directory.Exists(exeAdjacent)) return exeAdjacent;
        var inResources = Path.Combine(baseDirectory, "..", "Resources", name);
        return Directory.Exists(inResources) ? inResources : null;
    }
}
