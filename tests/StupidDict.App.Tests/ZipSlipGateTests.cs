using StupidDict.App;
using Xunit;

namespace StupidDict.App.Tests;

/// <summary>
/// Pure-logic zip-slip gate tests (no Avalonia bootstrap): the gate must
/// allow reserved-device-name entries (us/con.mp3 — "con" is a real headword)
/// and reject escapes. GetFullPath-based containment is forbidden on Windows:
/// it rewrites a final reserved DOS device name into \\.\CON form.
/// </summary>
public class ZipSlipGateTests
{
    [Theory]
    [InlineData("us/con.mp3", false)]
    [InlineData("con", false)]
    [InlineData("us/aux.mp3", false)]
    [InlineData("nul", false)]
    [InlineData("...", false)]
    [InlineData("us/./cat.mp3", false)]
    [InlineData("us/cat.mp3", false)]
    [InlineData("../evil.mp3", true)]
    [InlineData("us/../evil.mp3", true)]
    [InlineData("us/..", true)]
    [InlineData("/abs/evil.mp3", true)]
    public void EntryEscapesDestinationClassifiesEntryNames(string entryName, bool escapes)
    {
        Assert.Equal(escapes, MainWindow.EntryEscapesDestination(entryName));
    }

    [Fact]
    public void EntryEscapesDestinationRejectsWindowsRootedNames()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.True(MainWindow.EntryEscapesDestination("C:\\evil.mp3"));
        Assert.True(MainWindow.EntryEscapesDestination("\\evil.mp3"));
        Assert.True(MainWindow.EntryEscapesDestination("C:evil.mp3"));
        Assert.True(MainWindow.EntryEscapesDestination("\\\\server\\share\\evil.mp3"));
    }
}
