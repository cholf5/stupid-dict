using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Styling;

namespace StupidDict.App;

/// <summary>
/// Windows only: paints the native title bar in the app theme. The classic
/// Win32 caption never follows the app theme on its own — the app has to
/// declare DWMWA_USE_IMMERSIVE_DARK_MODE to DWM. Avalonia's own Win32 backend
/// only does that on Windows 11 (build >= 22000), so Windows 10 would keep a
/// white caption in dark mode. Worse, writing the attribute alone is not
/// enough: DWM composes the caption lazily, so the write has to be followed by
/// SetWindowPos(SWP_FRAMECHANGED) or the change may not be visible until some
/// later frame event (drag, maximize, restart). Both halves live here.
/// </summary>
internal static class WindowsCaptionTheme
{
    /// <summary>DWMWA_USE_IMMERSIVE_DARK_MODE on Windows 10 2004+ and Windows 11.</summary>
    internal const int ImmersiveDarkMode = 20;

    /// <summary>The same attribute on Windows 10 1809–1903, before the renumber.</summary>
    internal const int ImmersiveDarkModeLegacy = 19;

    /// <summary>
    /// Keeps one window's caption in step with its theme: once at construction
    /// (the HWND may not exist yet — the pass becomes a no-op and Opened
    /// retries), on open, and on every light/dark switch.
    /// </summary>
    internal static void Attach(Window window)
    {
        Apply(window);
        window.Opened += (_, _) => Apply(window);
        window.ActualThemeVariantChanged += (_, _) => Apply(window);
    }

    private static void Apply(Window window)
    {
        try
        {
            var handle = HandleOf(window);
            if (handle == IntPtr.Zero) return;
            Apply(handle, window.ActualThemeVariant, DwmCaptionBackend.Instance);
        }
        catch
        {
            // Chrome color is cosmetic; a missing dwmapi or a locked-down
            // session must never take a window (or the app) down.
        }
    }

    /// <summary>
    /// The Win32 HWND, or zero when this is not a real Windows window: the
    /// headless test platform's windows expose no platform handle at all, and
    /// on macOS/Linux there is nothing to paint.
    /// </summary>
    private static IntPtr HandleOf(Window window)
    {
        if (!OperatingSystem.IsWindows()) return IntPtr.Zero;
        return window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
    }

    /// <summary>
    /// Writes the dark-mode attribute (20, falling back to 19 on 1809–1903)
    /// and forces the frame recompute that makes DWM show it. The zero-handle
    /// guard keeps the construction-time call a no-op before Show. Internal and
    /// backend-injected purely as the test seam — headless tests can assert the
    /// attribute values and the fallback, but the visual result is only
    /// verifiable on a real machine.
    /// </summary>
    internal static void Apply(IntPtr handle, ThemeVariant variant, ICaptionThemeBackend backend)
    {
        if (handle == IntPtr.Zero) return;
        // Light must be written explicitly (0), not skipped: DWM keeps whatever
        // was set before, so a dark -> light switch would otherwise stick.
        var value = ThemeVariant.Dark.Equals(variant) ? 1 : 0;
        if (backend.TrySetAttribute(handle, ImmersiveDarkMode, value)
            || backend.TrySetAttribute(handle, ImmersiveDarkModeLegacy, value))
        {
            backend.ForceFrameRecompute(handle);
        }
    }
}

/// <summary>Test seam for the two Win32 calls; production uses <see cref="DwmCaptionBackend"/>.</summary>
internal interface ICaptionThemeBackend
{
    /// <summary>Sets one DWMWA attribute; false means DWM rejected it (older builds).</summary>
    bool TrySetAttribute(IntPtr handle, int attribute, int value);

    /// <summary>Asks DWM to recompose the non-client frame right now.</summary>
    void ForceFrameRecompute(IntPtr handle);
}

internal sealed class DwmCaptionBackend : ICaptionThemeBackend
{
    internal static readonly DwmCaptionBackend Instance = new();

    // SWP_* flags: recompute the frame without touching size, position,
    // z-order or activation.
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;

    private DwmCaptionBackend()
    {
    }

    public bool TrySetAttribute(IntPtr handle, int attribute, int value)
    {
        if (!OperatingSystem.IsWindows()) return false;
        return DwmSetWindowAttribute(handle, attribute, ref value, sizeof(int)) == 0;
    }

    public void ForceFrameRecompute(IntPtr handle)
    {
        if (!OperatingSystem.IsWindows()) return;
        SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0,
            SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy,
        uint flags);
}
