using System.Reflection;
using StupidDict.App.Localization;
using Xunit;

namespace StupidDict.App.Tests;

/// <summary>
/// Translation UI strings completeness: zh is the source dictionary — a
/// missing zh key renders as the property name (一眼假), so both language
/// tables are pinned via reflection on the private dictionaries. The test
/// deliberately never calls SetLanguage: the singleton is process-global and
/// a live window from another test class would re-render on the burst
/// outside any headless application context.
/// </summary>
public class TranslationStringsTests
{
    [Fact]
    public void TranslationKeysExistInBothDictionaries()
    {
        var keys = typeof(Translations).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(string)
                        && (p.Name.StartsWith("Translation", StringComparison.Ordinal)
                            || p.Name == nameof(Translations.SectionTranslation)))
            .Select(p => p.Name)
            .ToList();
        Assert.NotEmpty(keys);

        var zh = PrivateStringTable("Zh");
        var en = PrivateStringTable("En");
        foreach (var key in keys)
        {
            Assert.True(zh.TryGetValue(key, out var zhValue), $"zh missing key {key}");
            Assert.False(string.IsNullOrWhiteSpace(zhValue));
            Assert.NotEqual(key, zhValue);

            Assert.True(en.TryGetValue(key, out var enValue), $"en missing key {key}");
            Assert.False(string.IsNullOrWhiteSpace(enValue));
            Assert.NotEqual(key, enValue);
        }
    }

    [Fact]
    public void AllPropertyNamesMatchesActualProperties()
    {
        // The production array went explicit for NativeAOT (a GetProperties
        // scan at static-init time is what trimming punishes). This mirror
        // keeps the array honest: every public instance property must be
        // listed, and nothing stale may linger — SetLanguage fans
        // PropertyChanged over the array, so a missing name would leave that
        // binding speaking the old language after a switch.
        var actual = typeof(Translations).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
        var declared = (string[])typeof(Translations)
            .GetField("AllPropertyNames", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

        Assert.Equal(actual, declared.OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }

    private static Dictionary<string, string> PrivateStringTable(string name)
    {
        var field = typeof(Translations).GetField(name,
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        return (Dictionary<string, string>)field!.GetValue(null)!;
    }
}
