using System.IO.Compression;
using StupidDict.App;
using Xunit;

namespace StupidDict.App.Tests;

/// <summary>
/// Pure-logic zip-slip gate tests (no Avalonia bootstrap): the gate must
/// allow reserved-device-name entries (us/con.mp3 — "con" is a real headword)
/// and reject escapes. GetFullPath-based containment is forbidden on Windows:
/// it rewrites a final reserved DOS device name into \\.\CON form. Also the
/// extraction progress reporting, which rides on the same entry-by-entry loop.
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

    [Fact]
    public void ImportAudioPackReportsMonotonicProgressThroughTotal()
    {
        var scratch = NewScratchDirectory();
        var zipPath = Path.Combine(scratch, "pack.zip");
        const int fileCount = 600; // crosses the 256-entry report stride
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            for (var i = 0; i < fileCount; i++)
            {
                var entry = archive.CreateEntry($"us/w{i:D4}.mp3");
                using var stream = entry.Open();
                stream.WriteByte((byte)'x');
            }

        var reports = new List<(int Done, int Total)>();
        var audio = Path.Combine(scratch, "audio");
        MainWindow.ImportAudioPack(zipPath, audio, (done, total) => reports.Add((done, total)));

        Assert.True(File.Exists(Path.Combine(audio, "us", "w0000.mp3")));
        Assert.True(File.Exists(Path.Combine(audio, "us", $"w{fileCount - 1:D4}.mp3")));
        Assert.Empty(Directory.EnumerateDirectories(audio, ".stupiddict-extracting-*"));

        Assert.NotEmpty(reports);
        Assert.All(reports, report => Assert.Equal(fileCount, report.Total));
        for (var i = 1; i < reports.Count; i++)
            Assert.True(reports[i].Done > reports[i - 1].Done, "progress must move forward");
        Assert.Equal((fileCount, fileCount), reports[^1]);
    }

    private static string NewScratchDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "stupiddict-uitests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
