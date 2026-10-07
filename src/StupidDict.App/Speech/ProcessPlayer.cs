using System.Diagnostics;

namespace StupidDict.App.Speech;

/// <summary>
/// Plays by launching a CLI process (say, afplay, espeak, mpg123…) and
/// killing it when the next play starts. Spawn failures — missing binary,
/// unsupported platform — surface as a false return, not an exception.
/// </summary>
internal sealed class ProcessPlayer
{
    private Process? _current;

    public bool Play(string program, params string[] arguments)
    {
        Stop();
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = program,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in arguments) info.ArgumentList.Add(argument);
            _current = Process.Start(info);
            return _current is not null;
        }
        catch
        {
            _current = null;
            return false;
        }
    }

    public void Stop()
    {
        try
        {
            if (_current is { HasExited: false } process) process.Kill();
        }
        catch
        {
            // the process may already be gone; nothing to do about it
        }
        _current?.Dispose();
        _current = null;
    }
}

internal static class PathLookup
{
    /// <summary>Resolves a command name against PATH, or null when absent.</summary>
    public static string? Which(string name)
    {
        if (name.Contains('/')) return File.Exists(name) ? name : null;
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        var separator = OperatingSystem.IsWindows() ? ';' : ':';
        foreach (var directory in path.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(directory, name);
            if (OperatingSystem.IsWindows() && !candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                candidate += ".exe";
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
