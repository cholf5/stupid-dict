using StupidDict.App.Translation;
using Xunit;

namespace StupidDict.App.Tests;

public class TranslationTextTests
{
    [Theory]
    [InlineData("Hello", false)]
    [InlineData("OpenAI", false)]
    [InlineData("Hello world", true)]
    [InlineData("How are you?", true)]
    [InlineData("你好", false)]
    [InlineData("你真的要去北京吗？", true)]
    public void SentenceDetectionMatchesTheDictionaryAssumption(string text, bool expected) =>
        Assert.Equal(expected, TranslationText.LooksLikeSentence(text));

    [Theory]
    [InlineData("Hello there.", TranslationDirection.EnglishToChinese)]
    [InlineData("你好。", TranslationDirection.ChineseToEnglish)]
    [InlineData("OpenAI 发布了 GPT-5。", TranslationDirection.ChineseToEnglish)]
    public void DirectionUsesTheDominantScript(string text, TranslationDirection expected) =>
        Assert.Equal(expected, TranslationText.DetectDirection(text));

    [Fact]
    public void CacheNormalizationKeepsCaseAndPunctuation()
    {
        var normalized = TranslationText.NormalizeForCache("  Hello   World.  ");
        Assert.Equal("Hello World.", normalized);
    }

    [Fact]
    public void QualityGateRejectsRepetitionAndRefusal()
    {
        Assert.NotNull(TranslationQualityGate.FindProblem(
            "Under the weather.", "天气下 天气下 天气下 天气下", TranslationDirection.EnglishToChinese));
        Assert.Equal(TranslationFailureKind.Refused, TranslationQualityGate.FindProblem(
            "Hello", "I'm sorry, I can't translate that.", TranslationDirection.EnglishToChinese));
        Assert.Null(TranslationQualityGate.FindProblem(
            "The government announced a new policy.", "政府宣布了一项新政策。",
            TranslationDirection.EnglishToChinese));
    }
}
