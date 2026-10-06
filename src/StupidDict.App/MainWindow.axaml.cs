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
    private readonly DictionaryService _service;
    private readonly bool _dictionaryAvailable;
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
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);

        ShowEmptyState();
        RefreshRecents();
        if (!_dictionaryAvailable)
            HintText.Text = "未找到词典数据 dictionary.db — 先运行 dotnet run --project src/StupidDict.DataBuilder";
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.K when (e.KeyModifiers & (KeyModifiers.Meta | KeyModifiers.Control)) != 0:
                e.Handled = true;
                SearchBox.Focus();
                SearchBox.SelectAll();
                break;
            case Key.Escape:
                e.Handled = true;
                SearchBox.Clear();
                ShowEmptyState();
                SearchBox.Focus();
                break;
        }
    }

    private void OnSearchBoxKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                RunSearch(SearchBox.Text);
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
            SearchBox.Text = recent.Query;
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
        RefreshRecents();
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
                SearchBox.Text = item;
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

    private static TextBlock Text(string value, double fontSize, FontWeight weight = FontWeight.Normal,
        string color = "#2B2A24", bool mono = false, Thickness? margin = null,
        double? lineHeight = null, bool verticalCenter = false)
    {
        var block = new TextBlock
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
        return block;
    }
}
