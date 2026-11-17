using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using StupidDict.App.Localization;
using StupidDict.App.Settings;
using StupidDict.App.Speech;
using StupidDict.Core.Application;
using StupidDict.Core.Dictionary;

namespace StupidDict.App.Tray;

/// <summary>
/// The tray's left-click card: a borderless mini dictionary. It exists for one
/// glance — type, read the headword/phonetics/first senses, hear it, press Enter
/// to open the full entry in the main window, or Esc to dismiss.
/// <para>
/// Deliberately not a second result page: no thesaurus, no related-word links,
/// no translation, no double-click-to-look-up. Those belong to the main window;
/// this one only answers "what does this word mean" without stealing the screen.
/// </para>
/// <para>
/// The card hides on focus loss, which on Windows arrives <b>before</b> the tray
/// click that caused it (the taskbar takes focus on mouse-down, the tray raises
/// Clicked on mouse-up). <see cref="LastAutoHiddenUtc"/> records that self-hide
/// so the left-click entry point can tell "the user closed me with this very
/// click" from "open me" — without it every click while open would hide and
/// instantly reopen.
/// </para>
/// </summary>
public partial class QuickLookupWindow : Window
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(150);

    // A provider, not a service instance: the main window swaps its
    // DictionaryService when the dictionary finishes downloading.
    private readonly Func<DictionaryService> _serviceProvider;
    private readonly ISpeechPlayer _speech;
    private CancellationTokenSource? _debounceCts;
    private int _generation;
    private LookupResult? _rendered;

    /// <summary>Test seam: stands in for the dictionary lookup so headless tests
    /// need no real data. Null in production.</summary>
    internal Func<string, Task<LookupResult>>? LookupOverride;

    /// <summary>Opens the full entry in the main window (Enter).</summary>
    internal Action<string>? OpenInMainWindow;

    /// <summary>When the card last hid itself because something else took focus.
    /// Zero means "never" — far enough in the past for any guard window.</summary>
    internal DateTimeOffset LastAutoHiddenUtc { get; set; }

    public QuickLookupWindow() : this(() => new DictionaryService(
        AppPaths.DictionaryDatabasePath, AppPaths.HistoryDatabasePath))
    {
    }

    public QuickLookupWindow(Func<DictionaryService> serviceProvider, ISpeechPlayer? speech = null)
    {
        InitializeComponent();
        _serviceProvider = serviceProvider;
        // Silent fallback: a card must never hold a second MCI alias open, so
        // production always injects the main window's player; this is only for
        // tests and the XAML loader.
        _speech = speech ?? new NullSpeechPlayer();
        QueryBox.TextChanged += OnQueryTextChanged;
        Deactivated += OnWindowDeactivated;
        AddHandler(KeyDownEvent, OnQuickLookupKeyDown,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        Translations.Instance.PropertyChanged += OnTranslationsChanged;
        ActualThemeVariantChanged += (_, _) => ReplayRender();
        Closed += (_, _) => Translations.Instance.PropertyChanged -= OnTranslationsChanged;
    }

    /// <summary>Places the card at <paramref name="position"/> (physical pixels)
    /// and shows it with the caret in the box.</summary>
    internal void ShowQuickLookup(PixelPoint position)
    {
        Position = position;
        Show();
        // macOS: showing a window while another app is frontmost does not make
        // this app active, and an inactive window cannot take the caret — the
        // card would appear but swallow every keystroke.
        Activate();
        QueryBox.Focus();
        QueryBox.SelectAll();
    }

    internal void HideQuickLookup() => Hide();

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        // Only a focus loss of a *visible* card counts: our own Hide() (Esc,
        // Enter, tray toggle) raises Deactivated too, and recording that would
        // make the next tray click get swallowed by the guard.
        if (!IsVisible) return;
        Hide();
        LastAutoHiddenUtc = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Esc/Enter are taken in the tunnel phase with handledEventsToo: the text
    /// box (IME composition, its own Enter handling) must never swallow them.
    /// </summary>
    private void OnQuickLookupKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                HideQuickLookup();
                break;
            case Key.Enter:
                e.Handled = true;
                OpenCurrentWord();
                break;
        }
    }

    private void OpenCurrentWord()
    {
        var word = (QueryBox.Text ?? "").Trim();
        if (word.Length == 0) return;
        var target = _rendered?.Primary?.Word ?? word;
        HideQuickLookup();
        OpenInMainWindow?.Invoke(target);
    }

    private void OnQueryTextChanged(object? sender, TextChangedEventArgs e)
    {
        CancelScheduledLookup();
        var text = (QueryBox.Text ?? "").Trim();
        if (text.Length == 0)
        {
            _rendered = null;
            ResultPanel.Children.Clear();
            ResultScroll.IsVisible = false;
            return;
        }
        var cts = new CancellationTokenSource();
        _debounceCts = cts;
        _ = RunLookupAsync(text, _generation, cts);
    }

    private async Task RunLookupAsync(string query, int generation, CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(Debounce, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        LookupResult result;
        try
        {
            result = await (LookupOverride?.Invoke(query) ?? _serviceProvider().LookupAsync(query));
        }
        catch (Exception ex)
        {
            if (generation == _generation) RenderError(query, ex);
            return;
        }
        if (generation != _generation) return;
        Render(result);
    }

    private void CancelScheduledLookup()
    {
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        _debounceCts = null;
        _generation++;
    }

    // ---- rendering ----

    private void Render(LookupResult result)
    {
        _rendered = result;
        ResultPanel.Children.Clear();
        var theme = ActualThemeVariant;

        if (result.IsChineseQuery)
        {
            if (result.Primary is not { } primary && result.ChineseMatches.Count == 0)
            {
                ResultPanel.Children.Add(NoResultBlock(result.Query, theme));
            }
            else if (result.Primary is { } entry)
            {
                AddEntry(entry, theme);
                var others = result.ChineseMatches
                    .Select(m => m.Entry.Word)
                    .Where(w => !string.Equals(w, entry.Word, StringComparison.OrdinalIgnoreCase))
                    .Take(8);
                AddChips(others);
            }
            else
            {
                ResultPanel.Children.Add(Text(result.Query, 15, FontWeight.SemiBold, Palette.TextStrong, theme));
                AddChips(result.ChineseMatches.Select(m => m.Entry.Word).Take(8));
            }
            ResultScroll.IsVisible = true;
            return;
        }

        if (result.Primary is not { } primaryEntry)
        {
            ResultPanel.Children.Add(NoResultBlock(result.Query, theme));
            if (result.WordSuggestions.Count > 0)
            {
                ResultPanel.Children.Add(Text(Translations.Instance.DidYouMean, 11,
                    brushKey: Palette.TextMuted, theme: theme, margin: new Thickness(0, 6, 0, 0)));
                AddChips(result.WordSuggestions.Select(w => w.Word).Take(8));
            }
            ResultScroll.IsVisible = true;
            return;
        }

        AddEntry(primaryEntry, theme);
        if (result.WordFormNote is { } note)
        {
            ResultPanel.Children.Insert(0, Text($"{result.Query} → {note}", 11,
                brushKey: Palette.TextMuted, theme: theme));
        }
        ResultScroll.IsVisible = true;
    }

    private void AddEntry(DictionaryEntry entry, Avalonia.Styling.ThemeVariant theme)
    {
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        head.Children.Add(Text(entry.Word, 20, FontWeight.SemiBold, Palette.TextStrong,
            theme, verticalCenter: true));
        head.Children.Add(AccentGroup(entry.Word, SpeechAccent.British, entry.Phonetic, theme));
        head.Children.Add(AccentGroup(entry.Word, SpeechAccent.American, entry.UsPhonetic, theme));
        ResultPanel.Children.Add(head);

        var senses = SplitLines(entry.Chinese);
        if (senses.Length == 0) senses = SplitLines(entry.English);
        foreach (var line in senses.Take(6))
            ResultPanel.Children.Add(Text(line, 14, brushKey: Palette.TextBody, theme: theme));
    }

    private void AddChips(IEnumerable<string> words)
    {
        var panel = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
        foreach (var word in words)
        {
            var chip = new Button { Classes = { "chip" }, Content = word, Margin = new Thickness(0, 0, 6, 6) };
            chip.Click += (_, _) =>
            {
                QueryBox.Text = word;
                QueryBox.CaretIndex = word.Length;
                QueryBox.Focus();
            };
            panel.Children.Add(chip);
        }
        if (panel.Children.Count > 0) ResultPanel.Children.Add(panel);
    }

    private SelectableTextBlock NoResultBlock(string query, Avalonia.Styling.ThemeVariant theme)
    {
        var block = Text(string.Format(Translations.Instance.NoResultFormat, query),
            15, FontWeight.SemiBold, Palette.TextTitle, theme);
        return block;
    }

    private void RenderError(string query, Exception ex)
    {
        ResultPanel.Children.Clear();
        var theme = ActualThemeVariant;
        ResultPanel.Children.Add(Text(string.Format(Translations.Instance.LookupErrorFormat, query),
            15, FontWeight.SemiBold, Palette.ErrorForeground, theme));
        ResultPanel.Children.Add(Text(ex.Message, 11, brushKey: Palette.TextMuted, theme: theme));
        ResultScroll.IsVisible = true;
    }

    private void ReplayRender()
    {
        if (_rendered is { } result) Render(result);
    }

    private void OnTranslationsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(Translations.CurrentLanguage)) return;
        ReplayRender();
    }

    private StackPanel AccentGroup(string word, SpeechAccent accent, string phonetic,
        Avalonia.Styling.ThemeVariant theme)
    {
        var group = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        group.Children.Add(SpeakerButton(word, accent));
        var prefix = accent == SpeechAccent.British
            ? Translations.Instance.UkPhoneticPrefix
            : Translations.Instance.UsPhoneticPrefix;
        var label = string.Format(prefix, phonetic.Length > 0 ? FormatPhonetic(phonetic) : string.Empty).Trim();
        group.Children.Add(Text(label, 12, brushKey: Palette.TextMuted, theme: theme, mono: true,
            verticalCenter: true));
        return group;
    }

    /// <summary>Material Icons volume_up (Apache-2.0), same glyph as the main window.</summary>
    private static readonly Geometry SpeakerIconGeometry = StreamGeometry.Parse(
        "M3 9v6h4l5 5V4L7 9H3zm13.5 3c0-1.77-1.02-3.29-2.5-4.03v8.05c1.48-.73 2.5-2.25 2.5-4.02zM14 3.23v2.06c2.89.86 5 3.54 5 6.71s-2.11 5.85-5 6.71v2.06c4.01-.91 7-4.49 7-8.77s-2.99-7.86-7-8.77z");

    private Button SpeakerButton(string word, SpeechAccent accent)
    {
        var button = new Button
        {
            Classes = { "spk" },
            Name = accent == SpeechAccent.British ? "UkSpeakerButton" : "UsSpeakerButton",
            Content = new Avalonia.Controls.Shapes.Path
            {
                Width = 12,
                Height = 12,
                Stretch = Stretch.Uniform,
                Data = SpeakerIconGeometry,
            },
        };
        ToolTip.SetTip(button, accent == SpeechAccent.British
            ? Translations.Instance.UkTip
            : Translations.Instance.UsTip);
        button.Click += (_, _) => PlayWord(button, word, accent);
        return button;
    }

    private async void PlayWord(Button button, string word, SpeechAccent accent)
    {
        if (_speech.Play(word, accent)) return;
        // Nothing could speak (no pack, no engine): flash like the main window
        // instead of failing silently. Never throws into the click path.
        var original = button.Content;
        button.Content = "✕";
        await Task.Delay(1200);
        if (button.Content is "✕") button.Content = original;
    }

    private static string[] SplitLines(string value) =>
        value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string FormatPhonetic(string phonetic) =>
        phonetic.StartsWith('/') ? phonetic : $"/{phonetic}/";

    private SelectableTextBlock Text(string value, double fontSize, FontWeight weight = FontWeight.Normal,
        string brushKey = Palette.TextBody, Avalonia.Styling.ThemeVariant? theme = null, bool mono = false,
        Thickness? margin = null, bool verticalCenter = false)
    {
        var block = new SelectableTextBlock
        {
            Text = value,
            FontSize = fontSize,
            FontWeight = weight,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Palette.Get(brushKey, theme ?? ActualThemeVariant),
            Margin = margin ?? new Thickness(0),
            VerticalAlignment = verticalCenter ? VerticalAlignment.Center : VerticalAlignment.Top,
        };
        if (mono) block.FontFamily = new FontFamily("Menlo, Consolas, DejaVu Sans Mono");
        return block;
    }

    /// <summary>Silent stand-in used by the XAML loader and tests only.</summary>
    private sealed class NullSpeechPlayer : ISpeechPlayer
    {
        public bool Play(string word, SpeechAccent accent) => false;
        public void WarmUp() { }
        public void Stop() { }
    }
}
