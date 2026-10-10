using StupidDict.App.Assets;
using Xunit;

namespace StupidDict.App.Tests;

/// <summary>
/// Reserved DOS device names (CON/PRN/AUX/NUL/COM0-9/LPT0-9 and the
/// superscript variants) must be detected on the stem before the first dot
/// (pre-Windows 11 matches "CON.THING" as CON) and case-insensitively
/// ("CON" is a real headword; MP3 file names carry ".mp3"). Mapping prefixes
/// a '_' so no reserved name ever materializes on disk; ordinary names pass
/// through byte-for-byte. Pure [Fact] tests — no Avalonia surface.
/// </summary>
public sealed class ReservedDeviceNamesTests
{
    [Theory]
    [InlineData("con.mp3", "_con.mp3")]
    [InlineData("con", "_con")]
    [InlineData("CON.MP3", "_CON.MP3")]
    [InlineData("aux", "_aux")]
    [InlineData("Aux.mp3", "_Aux.mp3")]
    [InlineData("nul", "_nul")]
    [InlineData("prn", "_prn")]
    [InlineData("com0", "_com0")]
    [InlineData("com1", "_com1")]
    [InlineData("lpt9", "_lpt9")]
    [InlineData("con.tact.mp3", "_con.tact.mp3")]
    [InlineData("com¹.mp3", "_com¹.mp3")]
    public void ReservedStemsGetAnUnderscorePrefix(string name, string mapped) =>
        Assert.Equal(mapped, ReservedDeviceNames.MapSegment(name));

    [Theory]
    [InlineData("cat.mp3")]
    [InlineData("constant.mp3")]
    [InlineData("conrad")]
    [InlineData("auxiliary.mp3")]
    [InlineData("com10.mp3")]
    [InlineData("uk")]
    [InlineData("us")]
    public void OrdinaryNamesPassThroughUnchanged(string name) =>
        Assert.Equal(name, ReservedDeviceNames.MapSegment(name));
}
