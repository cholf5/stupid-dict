namespace StupidDict.App.Assets;

/// <summary>
/// Zip-slip gate, purely syntactic: an entry escapes only by being rooted
/// (drive/UNC/leading separator) or carrying a ".." segment; anything else
/// stays inside the destination. Deliberately NOT a GetFullPath + prefix
/// comparison — on Windows, GetFullPath rewrites a path whose final segment
/// is a reserved DOS device name (CON/PRN/AUX/NUL/COM1-9/LPT1-9, extension
/// ignored; "con" is a real headword) into "\\.\CON" form, which always
/// fails the prefix check and makes a legitimate us/con.mp3 look like an
/// attack. Both separators are matched: the zip spec says '/', but Win32
/// treats '\' identically.
/// The gate guards both consumers of untrusted zips: dictionary extraction
/// (MainWindow.ExtractZip, which materializes entries) and the audio pack
/// converter (which would otherwise swallow "us/../evil.mp3" as a harmless
/// text key — the old import refused the archive outright, and so does the
/// conversion).
/// </summary>
internal static class ZipSafety
{
    public static bool EntryEscapesDestination(string entryName)
    {
        if (Path.IsPathRooted(entryName)) return true;
        return entryName.Split('/', '\\').Any(segment => segment == "..");
    }
}
