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
        // 跨语言展示名：GNOME/KDE 的自启列表按语言取 Name / Name[zh_CN]。
        Assert.Contains("Name[zh_CN]=傻瓜词典", content);
        Assert.Contains("Exec=\"/opt/my apps/StupidDict\"", content);
        // 图标指向 exe 同目录的 app-icon.png（package.sh 在 Linux zip 携带）；
        // 文件不存在时桌面环境回退通用图标，条目不因此失效。期望路径用同一
        // API 计算——分隔符随平台（生产消费方在 Linux 上恒为 /）。
        var expectedIcon = Path.Combine(Path.GetDirectoryName("/opt/my apps/StupidDict")!, "app-icon.png");
        Assert.Contains($"Icon={expectedIcon}", content);
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
        // 原子写（temp + Move 进位）不留 .tmp 在场。
        Assert.False(File.Exists(path + ".tmp"));

        // Stale content (e.g. the exe moved and the entry was rewritten with
        // the old path) must not count as current.
        File.WriteAllText(path, "stale");
        Assert.False(AutoStart.EntryFileIsCurrent(path, content));

        AutoStart.RemoveEntryFile(path);
        Assert.False(File.Exists(path));
        // Removing a missing entry is a no-op, never a throw.
        AutoStart.RemoveEntryFile(path);
        // Remove 顺带清扫崩溃残留的 .tmp。
        File.WriteAllText(path + ".tmp", "junk");
        AutoStart.RemoveEntryFile(path);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void FileEntryConvergesOntoEnabledState()
    {
        var path = Path.Combine(Path.GetTempPath(), "stupiddict-autostart-tests",
            Guid.NewGuid().ToString("N"), "autostart", "StupidDict.desktop");
        var content = AutoStart.DesktopEntryContent("/opt/StupidDict");

        // Disabled with nothing on disk: nothing appears (no churn).
        AutoStart.ApplyFileEntry(enabled: false, path, content);
        Assert.False(File.Exists(path));

        // Enabling writes the entry, creating parent directories on demand.
        AutoStart.ApplyFileEntry(enabled: true, path, content);
        Assert.True(AutoStart.EntryFileIsCurrent(path, content));

        // Already current: re-apply keeps the exact bytes (no rewrite churn —
        // the startup converge path runs on every launch).
        AutoStart.ApplyFileEntry(enabled: true, path, content);
        Assert.True(AutoStart.EntryFileIsCurrent(path, content));

        // Stale content (the exe moved) is rewritten by the next apply.
        File.WriteAllText(path, "stale");
        AutoStart.ApplyFileEntry(enabled: true, path, content);
        Assert.True(AutoStart.EntryFileIsCurrent(path, content));

        // Disabling removes the entry.
        AutoStart.ApplyFileEntry(enabled: false, path, content);
        Assert.False(File.Exists(path));
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
