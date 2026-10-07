using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using StupidDict.Core.Application;
using StupidDict.Core.Dictionary;
using StupidDict.Core.History;

namespace StupidDict.App;

public partial class MainWindow : Window
{
    private static readonly TimeSpan SuggestDebounce = TimeSpan.FromMilliseconds(120);

    private readonly DictionaryService _service;
    private readonly LookupNavigator _navigator = new();
    private readonly bool _dictionaryAvailable;
    private CancellationTokenSource? _suggestDebounce;
    private List<string> _suggestions = [];
    private int _suggestSelection = -1;
    private int _suggestGeneration;
    private bool _suppressSuggest;
    private int _searchGeneration;

    public MainWindow() : this(new DictionaryService(AppPaths.DictionaryDatabasePath, AppPaths.HistoryDatabasePath))
    {
    }

    public MainWindow(DictionaryService service)
    {
        InitializeComponent();
        _service = service;
        _dictionaryAvailable = File.Exists(service.DictionaryPath);

        Opened += (_, _) => SearchBox.Focus();
        SearchBox.KeyDown += OnSearchBoxKeyDown;
        SearchBox.TextChanged += OnSearchTextChanged;
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);

        ShowEmptyState();
        RefreshRecents();
        UpdateNavButtons();
        if (!_dictionaryAvailable)
            HintText.Text = "未找到词典数据 dictionary.db — 先运行 dotnet run --project src/StupidDict.DataBuilder";
        else
            _ = service.WarmupAsync();
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        var cmdCtrl = (e.KeyModifiers & (KeyModifiers.Meta | KeyModifiers.Control)) != 0;
        switch (e.Key)
        {
            case Key.K when cmdCtrl:
                e.Handled = true;
                SearchBox.Focus();
                SearchBox.SelectAll();
                break;
            case Key.OemOpenBrackets when cmdCtrl:
                e.Handled = true;
                Navigate(_navigator.GoBack());
                break;
            case Key.OemCloseBrackets when cmdCtrl:
                e.Handled = true;
                Navigate(_navigator.GoForward());
                break;
            case Key.Escape:
                e.Handled = true;
                if (SuggestPanel.IsVisible)
                {
                    HideSuggestions();
                    HintPanel.IsVisible = !ResultsPanel.IsVisible;
                    break;
                }
                SearchBox.Clear();
                ShowEmptyState();
                SearchBox.Focus();
                break;
        }
    }

    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppressSuggest)
        {
            _suppressSuggest = false;
            HideSuggestions();
            HintPanel.IsVisible = !ResultsPanel.IsVisible;
            return;
        }
        CancelScheduledSuggestions();
        var text = SearchBox.Text ?? "";
        if (text.Trim().Length == 0 || DictionaryService.IsChineseQuery(text))
        {
            HideSuggestions();
            HintPanel.IsVisible = !ResultsPanel.IsVisible;
            return;
        }
        ScheduleSuggestions(text);
    }

    private void ScheduleSuggestions(string query)
    {
        var cts = new CancellationTokenSource();
        _suggestDebounce = cts;
        var generation = _suggestGeneration;
        _ = RunSuggestionsAsync(query, generation, cts);
    }

    private async Task RunSuggestionsAsync(string query, int generation, CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(SuggestDebounce, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        List<string> words;
        try
        {
            words = await _service.SuggestAsync(query);
        }
        catch
        {
            return; // completion is best-effort; dictionary errors surface on Lookup
        }
        if (generation != _suggestGeneration) return;
        ShowSuggestions(words);
    }

    /// <summary>Invalidates any scheduled or in-flight suggestion fetch.</summary>
    private void CancelScheduledSuggestions()
    {
        _suggestDebounce?.Cancel();
        _suggestDebounce?.Dispose();
        _suggestDebounce = null;
        _suggestGeneration++;
    }

    private void ShowSuggestions(List<string> words)
    {
        if (words.Count == 0)
        {
            HideSuggestions();
            return;
        }
        _suggestions = words;
        _suggestSelection = -1;
        SuggestList.ItemsSource = words;
        SuggestPanel.IsVisible = true;
        HintPanel.IsVisible = false;
    }

    private void HideSuggestions()
    {
        CancelScheduledSuggestions();
        _suggestions = [];
        _suggestSelection = -1;
        SuggestPanel.IsVisible = false;
    }

    private void UpdateSuggestSelection()
    {
        var index = 0;
        foreach (var button in SuggestList.GetVisualDescendants().OfType<Button>())
            button.Classes.Set("selected", index++ == _suggestSelection);
    }

    private void SelectSuggestion(string word)
    {
        HideSuggestions();
        SetQueryText(word);
        RunSearch(word);
    }

    /// <summary>Programmatic text assignment that must not retrigger live completion.</summary>
    private void SetQueryText(string text)
    {
        if (SearchBox.Text != text)
            _suppressSuggest = true;
        SearchBox.Text = text;
        SearchBox.CaretIndex = text.Length;
        SearchBox.Focus();
    }

    private void OnSuggestClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Content: string word })
            SelectSuggestion(word);
    }

    private void OnSearchBoxKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                if (_suggestSelection >= 0 && _suggestSelection < _suggestions.Count)
                    SelectSuggestion(_suggestions[_suggestSelection]);
                else
                {
                    HideSuggestions();
                    RunSearch(SearchBox.Text);
                }
                break;
            case Key.Down when SuggestPanel.IsVisible && _suggestSelection < _suggestions.Count - 1:
                e.Handled = true;
                _suggestSelection++;
                UpdateSuggestSelection();
                break;
            case Key.Up when SuggestPanel.IsVisible && _suggestSelection >= 0:
                e.Handled = true;
                _suggestSelection--;
                UpdateSuggestSelection();
                break;
            case Key.Up or Key.Down when string.IsNullOrEmpty(SearchBox.Text):
                var firstRecent = RecentList.GetVisualDescendants().OfType<Button>().FirstOrDefault();
                if (firstRecent is not null)
                {
                    e.Handled = true;
                    firstRecent.Focus();
                }
                break;
        }
    }

    private void OnRecentClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: RecentSearch recent })
        {
            SetQueryText(recent.Query);
            RunSearch(recent.Query);
        }
    }

    private async void RunSearch(string? text)
    {
        var query = (text ?? "").Trim();
        if (query.Length == 0)
        {
            ShowEmptyState();
            return;
        }

        var generation = ++_searchGeneration;
        LookupResult result;
        try
        {
            result = await _service.LookupAsync(query);
        }
        catch (Exception ex)
        {
            if (generation == _searchGeneration)
                RenderError(query, ex);
            return;
        }
        if (generation != _searchGeneration) return;

        RenderResult(result);
        _navigator.Push(result);
        UpdateNavButtons();
        RefreshRecents();
    }

    private void OnNavBackClick(object? sender, RoutedEventArgs e) => Navigate(_navigator.GoBack());

    private void OnNavForwardClick(object? sender, RoutedEventArgs e) => Navigate(_navigator.GoForward());

    private void Navigate(LookupResult? result)
    {
        if (result is null) return;
        RenderResult(result);
        ShowQueryInSearchBox(result.Query);
        UpdateNavButtons();
    }

    private void UpdateNavButtons()
    {
        NavBackButton.IsEnabled = _navigator.CanGoBack;
        NavForwardButton.IsEnabled = _navigator.CanGoForward;
    }

    /// <summary>Mirrors the navigated page into the search box without taking focus.</summary>
    private void ShowQueryInSearchBox(string query)
    {
        if (SearchBox.Text != query)
            _suppressSuggest = true;
        SearchBox.Text = query;
        SearchBox.CaretIndex = query.Length;
    }

    private void ShowEmptyState()
    {
        _searchGeneration++;
        HintPanel.IsVisible = true;
        ResultsPanel.IsVisible = false;
        ResultsPanel.Children.Clear();
    }

    private void RefreshRecents()
    {
        var recent = _service.History.GetRecent();
        RecentPanel.IsVisible = recent.Count > 0;
        RecentList.ItemsSource = recent;
    }

    private void RenderResult(LookupResult result)
    {
        HintPanel.IsVisible = false;
        ResultsPanel.IsVisible = true;
        ResultsPanel.Children.Clear();

        if (result.IsChineseQuery)
            RenderChineseResult(result);
        else
            RenderEnglishResult(result);
    }

    private void RenderEnglishResult(LookupResult result)
    {
        if (result.Primary is not { } entry)
        {
            ResultsPanel.Children.Add(Text($"没有找到 “{result.Query}”", 20, FontWeight.SemiBold, "#403F38", margin: new Thickness(2, 8, 0, 0)));
            if (result.WordSuggestions.Count > 0)
            {
                ResultsPanel.Children.Add(Text("你是不是要找", fontSize: 12, color: "#9C9A91", margin: new Thickness(2, 18, 0, 8)));
                ResultsPanel.Children.Add(BuildChips(result.WordSuggestions.Select(w => w.Word)));
            }
            else
            {
                ResultsPanel.Children.Add(Text("试试更短的拼写，或换个说法。", fontSize: 14, color: "#8B897F", margin: new Thickness(2, 10, 0, 0)));
            }
            return;
        }

        ResultsPanel.Children.Add(Text(entry.Word, 30, FontWeight.SemiBold, "#211F1A", margin: new Thickness(2, 0, 0, 0)));

        if (result.WordFormNote is { } note)
            ResultsPanel.Children.Add(Text($"{result.Query} → {note}", fontSize: 13, color: "#9C9A91", margin: new Thickness(2, 4, 0, 0)));

        if (entry.Phonetic.Length > 0)
            ResultsPanel.Children.Add(Text(FormatPhonetic(entry.Phonetic), fontSize: 14, color: "#8B897F", mono: true, margin: new Thickness(2, 4, 0, 0)));

        if (entry.Chinese.Length > 0)
        {
            var chinese = new StackPanel { Spacing = 5, Margin = new Thickness(2, 14, 0, 0) };
            foreach (var line in SplitLines(entry.Chinese))
                chinese.Children.Add(Text(line, 16, color: "#2B2A24"));
            ResultsPanel.Children.Add(chinese);
        }

        if (entry.English.Length > 0)
        {
            ResultsPanel.Children.Add(Text("英英释义", fontSize: 12, color: "#9C9A91", margin: new Thickness(2, 18, 0, 5)));
            ResultsPanel.Children.Add(Text(entry.English, fontSize: 14, color: "#55534B", margin: new Thickness(2, 0, 0, 0), lineHeight: 22));
        }
    }

    private void RenderChineseResult(LookupResult result)
    {
        if (result.Primary is null && result.ChineseMatches.Count == 0)
        {
            ResultsPanel.Children.Add(Text($"没有找到 “{result.Query}”", 20, FontWeight.SemiBold, "#403F38", margin: new Thickness(2, 8, 0, 0)));
            return;
        }

        ResultsPanel.Children.Add(Text(result.Query, 30, FontWeight.SemiBold, "#211F1A", margin: new Thickness(2, 0, 0, 0)));

        if (result.Primary is { } primary)
        {
            var wordLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(2, 8, 0, 0) };
            wordLine.Children.Add(Text(primary.Word, 20, FontWeight.SemiBold, "#2B2A24", verticalCenter: true));
            if (primary.Phonetic.Length > 0)
                wordLine.Children.Add(Text(FormatPhonetic(primary.Phonetic), fontSize: 13, color: "#8B897F", mono: true, verticalCenter: true));
            ResultsPanel.Children.Add(wordLine);

            if (primary.English.Length > 0)
                ResultsPanel.Children.Add(Text(primary.English, fontSize: 14, color: "#55534B", margin: new Thickness(2, 6, 0, 0), lineHeight: 22));

            if (primary.Chinese.Length > 0)
            {
                var chinese = new StackPanel { Spacing = 5, Margin = new Thickness(2, 12, 0, 0) };
                foreach (var line in SplitLines(primary.Chinese))
                    chinese.Children.Add(Text(line, 16, color: "#2B2A24"));
                ResultsPanel.Children.Add(chinese);
            }
        }

        var others = result.ChineseMatches
            .Where(m => result.Primary is null || !string.Equals(m.Entry.Word, result.Primary.Word, StringComparison.OrdinalIgnoreCase))
            .Take(12)
            .ToList();
        if (others.Count > 0)
        {
            ResultsPanel.Children.Add(Text(result.Primary is null ? "相关词条" : "其他词条", fontSize: 12, color: "#9C9A91", margin: new Thickness(2, 18, 0, 8)));
            ResultsPanel.Children.Add(BuildChips(others.Select(m => m.Entry.Word)));
        }
    }

    private void RenderError(string query, Exception ex)
    {
        HintPanel.IsVisible = false;
        ResultsPanel.IsVisible = true;
        ResultsPanel.Children.Clear();
        ResultsPanel.Children.Add(Text($"查询 “{query}” 时出错", 20, FontWeight.SemiBold, "#8A3B33", margin: new Thickness(2, 8, 0, 0)));
        ResultsPanel.Children.Add(Text(ex.Message, fontSize: 13, color: "#8B897F", margin: new Thickness(2, 8, 0, 0)));
    }

    private WrapPanel BuildChips(IEnumerable<string> items)
    {
        var panel = new WrapPanel();
        foreach (var item in items)
        {
            var chip = new Button { Classes = { "chip" }, Content = item, Margin = new Thickness(0, 0, 8, 8) };
            chip.Click += (_, _) =>
            {
                SetQueryText(item);
                RunSearch(item);
            };
            panel.Children.Add(chip);
        }
        return panel;
    }

    private static string FormatPhonetic(string phonetic) =>
        phonetic.StartsWith('/') ? phonetic : $"/{phonetic}/";

    private static string[] SplitLines(string value) =>
        value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Double-clicking a word in any result text looks it up. The handler must
    /// see through the block's own selection handling (it claims PointerPressed
    /// to start a drag selection), hence handledEventsToo. Pressing runs before
    /// the built-in selection update, so the tapped word is found by hit-testing
    /// the layout, then highlighted via SelectionStart/End — no built-in
    /// double-click word selection exists in Avalonia 11.
    /// </summary>
    private void OnResultTextPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount != 2 || sender is not SelectableTextBlock block) return;
        var tapped = HitTestWord(block, e.GetPosition(block));
        if (tapped is not { } word) return;
        block.SelectionStart = word.Start;
        block.SelectionEnd = word.End;
        SetQueryText(word.Text);
        RunSearch(word.Text);
    }

    private static (string Text, int Start, int End)? HitTestWord(SelectableTextBlock block, Point position)
    {
        var text = block.Text;
        if (string.IsNullOrEmpty(text) || block.TextLayout is not { } layout) return null;

        var hit = layout.HitTestPoint(position);
        if (!hit.IsInside) return null;

        // A trailing hit resolves to the boundary after a character, so the
        // character under the pointer is the one just before it.
        var boundary = hit.CharacterHit.FirstCharacterIndex + hit.CharacterHit.TrailingLength;
        var index = Math.Clamp(hit.IsTrailing ? boundary - 1 : boundary, 0, text.Length - 1);
        if (!IsWordChar(text[index])) return null;

        var start = index;
        while (start > 0 && IsWordChar(text[start - 1])) start--;
        var end = index + 1;
        while (end < text.Length && IsWordChar(text[end])) end++;

        var word = text[start..end].Trim('\'', '-');
        if (word.Length == 0 || DictionaryService.IsChineseQuery(word)) return null;
        return (word, start, end);
    }

    private static bool IsWordChar(char c) => char.IsLetter(c) || c is '\'' or '-';

    private SelectableTextBlock Text(string value, double fontSize, FontWeight weight = FontWeight.Normal,
        string color = "#2B2A24", bool mono = false, Thickness? margin = null,
        double? lineHeight = null, bool verticalCenter = false)
    {
        var block = new SelectableTextBlock
        {
            Text = value,
            FontSize = fontSize,
            FontWeight = weight,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush.Parse(color),
            Margin = margin ?? new Thickness(0),
            VerticalAlignment = verticalCenter ? Avalonia.Layout.VerticalAlignment.Center : Avalonia.Layout.VerticalAlignment.Top,
        };
        if (mono) block.FontFamily = new FontFamily("Menlo, Consolas, DejaVu Sans Mono");
        if (lineHeight is { } height) block.LineHeight = height;
        block.AddHandler(InputElement.PointerPressedEvent, OnResultTextPointerPressed,
            RoutingStrategies.Bubble, handledEventsToo: true);
        return block;
    }
}
