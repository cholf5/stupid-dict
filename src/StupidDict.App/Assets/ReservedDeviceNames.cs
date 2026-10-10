namespace StupidDict.App.Assets;

/// <summary>
/// Windows reserves the legacy DOS device names for file use, and pre-Windows
/// 11 resolves a final path segment with such a stem to the device itself —
/// extension ignored ("CON.TXT" opens the console) — so ordinary Win32 calls
/// on us/con.mp3 address the console instead of a file: existence probes say
/// no, reads can block forever, deletion misses. "con" is a real headword
/// (and con/aux/nul/prn-class stems are reachable words), so a legitimate
/// pronunciation pack carries entries Windows cannot address by name. The
/// pack therefore never materializes them: extraction maps a reserved stem
/// to the same name prefixed with '_' (us/con.mp3 lands as us/_con.mp3) and
/// the pack player applies the identical mapping when looking a word up.
/// The mapping is injective across headword audio: headwords are
/// [a-z' -]+, so nothing real starts with '_'.
/// </summary>
internal static class ReservedDeviceNames
{
    private static readonly HashSet<string> Devices = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        "COM¹", "COM²", "COM³", "LPT¹", "LPT²", "LPT³",
    };

    /// <summary>
    /// A segment is reserved when the name before its first dot is a device
    /// name (pre-Win11 matches "CON.THING" as CON) — case-insensitively.
    /// </summary>
    public static bool IsReserved(string segment)
    {
        var dot = segment.IndexOf('.');
        return Devices.Contains(dot < 0 ? segment : segment[..dot]);
    }

    /// <summary>
    /// The name an entry materializes as: reserved stems gain a '_'-prefix,
    /// everything else passes through byte-for-byte.
    /// </summary>
    public static string MapSegment(string segment) => IsReserved(segment) ? "_" + segment : segment;
}
