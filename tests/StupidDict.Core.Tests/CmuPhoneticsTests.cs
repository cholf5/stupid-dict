using StupidDict.Core.Dictionary;
using Xunit;

namespace StupidDict.Core.Tests;

public class CmuPhoneticsTests
{
    [Theory]
    [InlineData("HH AH0 L OW1", "həˈloʊ")]          // hello
    [InlineData("K AE1 T", "ˈkæt")]                 // cat
    [InlineData("B AH1 T ER0", "ˈbʌtər")]           // butter
    [InlineData("AH0 B AW1 T", "əˈbaʊt")]           // about
    [InlineData("S AH0 P OW1 Z", "səˈpoʊz")]        // suppose
    [InlineData("K AH0 N S IH1 D ER0", "kənˈsɪdər")]          // consider — /s/ leaves the /n/ as coda
    [InlineData("T R AE0 N Z L EY1 SH AH0 N", "trænzˈleɪʃən")] // translation — /zl/ is not an English onset
    [InlineData("D IH1 K SH AH0 N EH2 R IY0", "ˈdɪkʃəˌnɛri")]  // dictionary — secondary stress mid-word
    [InlineData("IH0 L EH1 K T R IH0 K", "ɪˈlɛktrɪk")]         // electric
    [InlineData("AO2 L R AY1 T", "ˌɔlˈraɪt")]                  // alright
    public void TranscribesArpabetToAmericanIpa(string phonemes, string expected) =>
        Assert.Equal(expected, CmuPhonetics.ToIpa(phonemes));

    [Fact]
    public void UnknownSymbolsAreSkippedNotFatal() =>
        Assert.Equal("ˈkæt", CmuPhonetics.ToIpa("K XX1 AE1 T"));

    [Fact]
    public void LoadKeepsFirstPronunciationAndSkipsVariantsAndComments()
    {
        var path = Path.Combine(Path.GetTempPath(), "stupiddict-tests", Guid.NewGuid().ToString("N") + "-cmudict.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path,
        [
            ";;; # CMUdict -- Major Version: 0.07b",
            "HELLO  HH AH0 L OW1",
            "HELLO(2)  HH EH0 L OW1",
            "CAT  K AE1 T",
            "",
            "not-a-word-line",
        ]);

        var map = CmuPhonetics.Load(path);

        Assert.Equal(2, map.Count);
        Assert.Equal("həˈloʊ", map["hello"]);
        Assert.Equal("ˈkæt", map["cat"]);
    }
}
