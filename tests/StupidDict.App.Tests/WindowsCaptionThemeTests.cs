using Avalonia.Styling;
using StupidDict.App;
using Xunit;

namespace StupidDict.App.Tests;

/// <summary>
/// Property-level coverage for the Windows caption fix. The visual result
/// (a dark caption right after a restart, no interaction) can only be checked
/// on a real Windows machine — headless windows have no HWND and DWM is not
/// involved at all. What is testable here is the write + fallback + recompute
/// protocol and the zero-handle skip.
/// </summary>
public class WindowsCaptionThemeTests
{
    private sealed class RecordingBackend : ICaptionThemeBackend
    {
        private readonly HashSet<int> _rejecting;

        internal RecordingBackend(params int[] rejecting) => _rejecting = [.. rejecting];

        internal List<(int Attribute, int Value)> Attempts { get; } = [];

        internal int Recomputes { get; private set; }

        public bool TrySetAttribute(IntPtr handle, int attribute, int value)
        {
            Attempts.Add((attribute, value));
            return !_rejecting.Contains(attribute);
        }

        public void ForceFrameRecompute(IntPtr handle) => Recomputes++;
    }

    private static readonly IntPtr Handle = new(0x1234);

    [Fact]
    public void DarkDeclaresImmersiveDarkModeAndForcesFrameRecompute()
    {
        var backend = new RecordingBackend();

        WindowsCaptionTheme.Apply(Handle, ThemeVariant.Dark, backend);

        var (attribute, value) = Assert.Single(backend.Attempts);
        Assert.Equal(WindowsCaptionTheme.ImmersiveDarkMode, attribute);
        Assert.Equal(1, value);
        Assert.Equal(1, backend.Recomputes);
    }

    [Fact]
    public void LightWritesZeroInsteadOfLeavingThePreviousValue()
    {
        var backend = new RecordingBackend();

        WindowsCaptionTheme.Apply(Handle, ThemeVariant.Light, backend);

        var (attribute, value) = Assert.Single(backend.Attempts);
        Assert.Equal(WindowsCaptionTheme.ImmersiveDarkMode, attribute);
        Assert.Equal(0, value);
        Assert.Equal(1, backend.Recomputes);
    }

    [Fact]
    public void FallsBackToLegacyAttributeWhenOlderBuildRejectsTwenty()
    {
        var backend = new RecordingBackend(WindowsCaptionTheme.ImmersiveDarkMode);

        WindowsCaptionTheme.Apply(Handle, ThemeVariant.Dark, backend);

        Assert.Equal(2, backend.Attempts.Count);
        Assert.Equal((WindowsCaptionTheme.ImmersiveDarkMode, 1), backend.Attempts[0]);
        Assert.Equal((WindowsCaptionTheme.ImmersiveDarkModeLegacy, 1), backend.Attempts[1]);
        Assert.Equal(1, backend.Recomputes);
    }

    [Fact]
    public void SkipsFrameRecomputeWhenNoAttributeIsSupported()
    {
        var backend = new RecordingBackend(
            WindowsCaptionTheme.ImmersiveDarkMode, WindowsCaptionTheme.ImmersiveDarkModeLegacy);

        WindowsCaptionTheme.Apply(Handle, ThemeVariant.Dark, backend);

        Assert.Equal(2, backend.Attempts.Count);
        // Nothing changed, so there is nothing to recompose.
        Assert.Equal(0, backend.Recomputes);
    }

    [Fact]
    public void ZeroHandleIsSkippedEntirely()
    {
        var backend = new RecordingBackend();

        WindowsCaptionTheme.Apply(IntPtr.Zero, ThemeVariant.Dark, backend);

        Assert.Empty(backend.Attempts);
        Assert.Equal(0, backend.Recomputes);
    }
}
