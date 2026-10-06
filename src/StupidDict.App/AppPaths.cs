namespace StupidDict.App;

/// <summary>
/// Where the app finds its data. The dictionary ships next to the executable
/// when packaged; otherwise it lives in the user data directory (built by
/// StupidDict.DataBuilder). History always lives in the user data directory.
/// </summary>
internal static class AppPaths
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "StupidDict");

    public static string HistoryDatabasePath { get; } = Path.Combine(DataDirectory, "history.db");

    public static string DictionaryDatabasePath { get; } =
        File.Exists(Path.Combine(AppContext.BaseDirectory, "dictionary.db"))
            ? Path.Combine(AppContext.BaseDirectory, "dictionary.db")
            : Path.Combine(DataDirectory, "dictionary.db");
}
