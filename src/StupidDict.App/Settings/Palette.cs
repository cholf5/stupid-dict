using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace StupidDict.App.Settings;

/// <summary>
/// Semantic color tokens shared by the XAML styles (as DynamicResource keys)
/// and the code-rendered result pages. The constants are the resource keys
/// defined in App.axaml's theme dictionaries; the Light values are the
/// original paper-white palette, the Dark values the matching warm charcoal.
/// </summary>
internal static class Palette
{
    public const string WindowBackground = "WindowBackground";
    public const string CardBackground = "CardBackground";
    public const string CardBorder = "CardBorder";
    public const string AudioBarBackground = "AudioBarBackground";
    public const string TextTitle = "TextTitle";
    public const string TextStrong = "TextStrong";
    public const string TextBody = "TextBody";
    public const string TextSecondary = "TextSecondary";
    public const string TextMuted = "TextMuted";
    public const string TextFaint = "TextFaint";
    public const string TextNav = "TextNav";
    public const string ChipBackground = "ChipBackground";
    public const string ChipHover = "ChipHover";
    public const string ChipPressed = "ChipPressed";
    public const string ChipText = "ChipText";
    public const string SuggestHover = "SuggestHover";
    public const string WordLink = "WordLink";
    public const string WordLinkHover = "WordLinkHover";
    public const string ErrorForeground = "ErrorForeground";

    /// <summary>
    /// Resolves a token against the app theme dictionaries for the resolved
    /// variant of the calling window. A missing key is a programming error and
    /// must fail loudly — a silently transparent brush would be invisible.
    /// </summary>
    public static IBrush Get(string key, ThemeVariant variant)
    {
        var application = Application.Current
            ?? throw new InvalidOperationException("Application is not initialized.");
        if (application.TryGetResource(key, variant, out var value) && value is IBrush brush)
            return brush;
        throw new InvalidOperationException($"Missing theme resource '{key}'.");
    }
}
