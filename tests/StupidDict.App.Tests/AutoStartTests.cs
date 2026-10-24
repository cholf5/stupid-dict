using StupidDict.App;
using Xunit;

namespace StupidDict.App.Tests;

// AutoStart 的叶函数全部带显式路径参数（照 SingleInstanceGuard 的注入缝先例），
// 测试只碰临时目录与 scratch 注册表键，绝不写真实的 Run 键或用户 LaunchAgents。
public class AutoStartTests
{
    [Fact]
    public void WindowsRunDataQuotesTheExecutablePath()
    {
        Assert.Equal("\"C:\\Apps\\Stupid Dict\\StupidDict.exe\"",
            AutoStart.WindowsRunData(@"C:\Apps\Stupid Dict\StupidDict.exe"));
    }

    [Fact]
    public void PlistContentCarriesLabelAndProgram()
    {
        var content = AutoStart.PlistContent("/Applications/Stupid Dict.app/Contents/MacOS/StupidDict");

        Assert.Contains("<string>com.cholf5.stupiddict</string>", content);
        Assert.Contains("<string>/Applications/Stupid Dict.app/Contents/MacOS/StupidDict</string>", content);
        Assert.Contains("<key>RunAtLoad</key>", content);
        Assert.Contains("<true/>", content);
    }

    [Fact]
    public void DesktopEntryQuotesExecutablePathWithSpaces()
    {
        var content = AutoStart.DesktopEntryContent("/opt/my apps/StupidDict");

        Assert.Contains("Type=Application", content);
        Assert.Contains("Name=Stupid Dict", content);
        Assert.Contains("Exec=\"/opt/my apps/StupidDict\"", content);
        Assert.Contains("Terminal=false", content);
    }

    [Fact]
    public void EntryFileWriteMatchRemoveRoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), "stupiddict-autostart-tests",
            Guid.NewGuid().ToString("N"), "autostart", "StupidDict.desktop");
        var content = AutoStart.DesktopEntryContent("/opt/StupidDict");

        Assert.False(AutoStart.EntryFileIsCurrent(path, content));

        // WriteEntryFile must create the parent directories on demand.
        AutoStart.WriteEntryFile(path, content);
        Assert.True(File.Exists(path));
        Assert.True(AutoStart.EntryFileIsCurrent(path, content));

        // Stale content (e.g. the exe moved and the entry was rewritten with
        // the old path) must not count as current.
        File.WriteAllText(path, "stale");
        Assert.False(AutoStart.EntryFileIsCurrent(path, content));

        AutoStart.RemoveEntryFile(path);
        Assert.False(File.Exists(path));
        // Removing a missing entry is a no-op, never a throw.
        AutoStart.RemoveEntryFile(path);
    }

    [Fact]
    public void RegistryValueRoundTrip()
    {
        if (!OperatingSystem.IsWindows()) return;

        var keyPath = @"Software\StupidDictAutoStartTests\" + Guid.NewGuid().ToString("N");
        try
        {
            AutoStart.SetRunValue(keyPath, "StupidDict", "\"C:\\x\\StupidDict.exe\"");
            Assert.Equal("\"C:\\x\\StupidDict.exe\"", AutoStart.GetRunValue(keyPath, "StupidDict"));

            AutoStart.RemoveRunValue(keyPath, "StupidDict");
            Assert.Null(AutoStart.GetRunValue(keyPath, "StupidDict"));
            // Removing a missing value is a no-op, never a throw.
            AutoStart.RemoveRunValue(keyPath, "StupidDict");
        }
        finally
        {
            try
            {
                Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(keyPath);
            }
            catch (ArgumentException)
            {
                // Key never got created (or is already gone) — nothing to clean.
            }
        }
    }
}
