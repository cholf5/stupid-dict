using System.Runtime.InteropServices;
using Avalonia;

namespace StupidDict.App.Tray;

/// <summary>What the platform could tell about the tray icon's neighbourhood.</summary>
/// <param name="StatusItem">macOS status item frame in Avalonia screen coordinates (physical px).</param>
/// <param name="Cursor">Windows cursor position at click time — it is on the tray icon.</param>
internal readonly record struct TrayProbe(PixelRect? StatusItem, PixelPoint? Cursor)
{
    public static TrayProbe None => default;
}

/// <summary>
/// Platform probes behind <see cref="PopoverPositioner"/>. Everything here is
/// best-effort: a failed probe yields <see cref="TrayProbe"/>.None and the
/// caller falls back to the working-area corner, which never misplaces the
/// card off screen.
/// </summary>
internal static class TrayAnchor
{
    public static TrayProbe Probe()
    {
        // macOS owns the exact status item geometry (MacStatusItem), Windows has
        // no tray rectangle API that Avalonia exposes but the cursor is a perfect
        // stand-in. Linux panels vary too much — corner fallback.
        if (OperatingSystem.IsMacOS() && MacStatusItem.TryGetStatusItemFrame(out var frame))
            return new TrayProbe(frame, null);
        if (OperatingSystem.IsWindows() && TryGetCursorPos(out var cursor))
            return new TrayProbe(null, cursor);
        return TrayProbe.None;
    }

    private static bool TryGetCursorPos(out PixelPoint point)
    {
        point = default;
        if (!GetCursorPos(out var native)) return false;
        // Win32 cursor coordinates are physical pixels, the same space as
        // Screen.WorkingArea and Window.Position — no DPI conversion.
        point = new PixelPoint(native.X, native.Y);
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);
}
