using System.Net;

namespace StupidDict.App;

/// <summary>
/// Registers and unregisters the per-user "launch at login" entry for the
/// current platform. The setting is the single source of truth: every app
/// start and every settings toggle converges the OS state onto it via
/// <see cref="Apply"/>, which also self-heals entries whose recorded
/// executable path went stale after a portable zip was moved. Everything is
/// per-user (no admin rights anywhere) and best-effort — a failed
/// registration must never crash or block the app.
/// </summary>
internal static class AutoStart
{
    /// <summary>Value name in the Windows Run key and the file stem on Unix.</summary>
    private const string EntryName = "StupidDict";

    /// <summary>LaunchAgent label and plist file stem; matches the bundle id in package.sh.</summary>
    private const string Label = "com.cholf5.stupiddict";

    private const string WindowsRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Converges the OS boot entry onto <paramref name="enabled"/>; never throws.</summary>
    internal static void Apply(bool enabled)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            if (OperatingSystem.IsWindows())
                ApplyWindows(enabled, exe);
            else if (OperatingSystem.IsMacOS())
                ApplyMacOs(enabled, exe);
            else if (OperatingSystem.IsLinux())
                ApplyLinux(enabled, exe);
        }
        catch
        {
            // Boot registration is best-effort by definition: policy edits,
            // read-only homes or registry trouble must never take the app down.
        }
    }

    private static void ApplyWindows(bool enabled, string exe)
    {
        if (!enabled)
        {
            RemoveRunValue(WindowsRunKeyPath, EntryName);
            return;
        }
        var data = WindowsRunData(exe);
        if (GetRunValue(WindowsRunKeyPath, EntryName) != data)
            SetRunValue(WindowsRunKeyPath, EntryName, data);
    }

    private static void ApplyMacOs(bool enabled, string exe)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home.Length == 0) return;
        var path = Path.Combine(home, "Library", "LaunchAgents", $"{Label}.plist");
        ApplyFileEntry(enabled, path, PlistContent(exe));
    }

    private static void ApplyLinux(bool enabled, string exe)
    {
        var config = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (config.Length == 0) return;
        var path = Path.Combine(config, "autostart", $"{EntryName}.desktop");
        ApplyFileEntry(enabled, path, DesktopEntryContent(exe));
    }

    private static void ApplyFileEntry(bool enabled, string path, string content)
    {
        if (!enabled)
        {
            RemoveEntryFile(path);
            return;
        }
        if (!EntryFileIsCurrent(path, content))
            WriteEntryFile(path, content);
    }

    /// <summary>Run values are quoted so paths with spaces stay a single argument.</summary>
    internal static string WindowsRunData(string exePath) => $"\"{exePath}\"";

    internal static string PlistContent(string exePath) => $"""
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
	<key>Label</key>
	<string>{Label}</string>
	<key>Program</key>
	<string>{WebUtility.HtmlEncode(exePath)}</string>
	<key>RunAtLoad</key>
	<true/>
</dict>
</plist>
""";

    internal static string DesktopEntryContent(string exePath) => $"""
[Desktop Entry]
Type=Application
Name=Stupid Dict
Exec="{exePath}"
Terminal=false
""";

    internal static void WriteEntryFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    internal static void RemoveEntryFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    internal static bool EntryFileIsCurrent(string path, string content)
    {
        try
        {
            return File.Exists(path) && File.ReadAllText(path) == content;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    // Registry helpers take the full key path so tests exercise them against a
    // scratch key instead of the real Run key — never touch real user state.
    // The IsWindows guards are for the CA1416 platform analyzer as much as for
    // behavior (Apply already dispatches, but these are internal and directly
    // callable from tests on any OS).

    internal static void SetRunValue(string keyPath, string name, string data)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(keyPath);
        key.SetValue(name, data);
    }

    internal static string? GetRunValue(string keyPath, string name)
    {
        if (!OperatingSystem.IsWindows()) return null;
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(keyPath);
        return key?.GetValue(name) as string;
    }

    internal static void RemoveRunValue(string keyPath, string name)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
            key?.DeleteValue(name, throwOnMissingValue: false);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
