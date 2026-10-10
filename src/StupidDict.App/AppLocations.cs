namespace StupidDict.App;

/// <summary>
/// Where the window looks for and installs its data. Defaults to the real
/// AppPaths layout; tests pass a temp copy so downloads never touch the
/// user's data directory.
/// </summary>
public sealed record AppLocations(
    string DataDirectory,
    string DictionaryDatabasePath,
    string HistoryDatabasePath,
    string AudioDirectory,
    string AudioPackDatabasePath)
{
    public static AppLocations Default { get; } = new(
        AppPaths.DataDirectory,
        AppPaths.DictionaryDatabasePath,
        AppPaths.HistoryDatabasePath,
        AppPaths.AudioDirectory,
        AppPaths.AudioPackDatabasePath);
}
