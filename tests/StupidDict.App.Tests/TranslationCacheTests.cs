using StupidDict.App.Translation;
using Xunit;

namespace StupidDict.App.Tests;

public class TranslationCacheTests
{
    [Fact]
    public void HitUpdatesLruAndEvictsOldest()
    {
        var path = TempPath();
        long now = 100;
        using var cache = new TranslationCache(path, maxBytes: 700, clock: () => now++);

        cache.Put("a", "译文一");
        cache.Put("b", "译文二");
        Assert.True(cache.TryGet("a", out var a));
        Assert.Equal("译文一", a);

        cache.Put("c", "译文三");
        Assert.True(cache.TryGet("a", out _));
        Assert.False(cache.TryGet("b", out _));
        Assert.True(cache.TryGet("c", out _));
    }

    [Fact]
    public void RetryOverwritesSameKey()
    {
        using var cache = new TranslationCache(TempPath(), maxBytes: 10_000);
        cache.Put("k", "旧译文");
        cache.Put("k", "新译文");
        Assert.True(cache.TryGet("k", out var value));
        Assert.Equal("新译文", value);
    }

    [Fact]
    public void CorruptDatabaseRecoversWithoutThrowing()
    {
        // 设计：损坏 → 删文件重建（缓存可丢），绝不能抛出或拖垮翻译。
        // （旧实现只禁用自身并保留坏文件，2026-10-20 评审后改为删建。）
        var path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "not sqlite");
        using var cache = new TranslationCache(path, maxBytes: 10_000);
        cache.Put("k", "v");
        Assert.True(cache.TryGet("k", out var value));
        Assert.Equal("v", value);
    }

    [Fact]
    public void CorruptDatabaseIsDeletedAndRebuiltNotJustDisabled()
    {
        // 设计：损坏 → 关连接、删文件、重建；仍失败才禁用。只禁用会让坏文件
        // 永久占位（设置页显示 0.0 MB 像是空缓存，跨会话也修不好）。
        var path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "not sqlite");
        using (var cache = new TranslationCache(path, maxBytes: 10_000))
        {
            cache.Put("k", "v");
            Assert.True(cache.TryGet("k", out _)); // 重建后首实例即恢复工作
        }

        using var second = new TranslationCache(path, maxBytes: 10_000);
        Assert.True(second.TryGet("k", out var value)); // 落盘的是重建后的库
        Assert.Equal("v", value);
    }

    [Fact]
    public void OversizedEntryIsNotCached()
    {
        // 单条超过预算不缓存（否则 LRU 无法收敛）。
        using var cache = new TranslationCache(TempPath(), maxBytes: 100);
        cache.Put("k", new string('x', 500));
        Assert.False(cache.TryGet("k", out _));
        Assert.Equal(0, cache.SizeBytes());
    }

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), "stupiddict-cache-tests", Guid.NewGuid().ToString("N"), "c.db");
}
