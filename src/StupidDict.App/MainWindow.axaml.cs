using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using StupidDict.App.Assets;
using StupidDict.App.Speech;
using StupidDict.Core.Application;
using StupidDict.Core.Dictionary;
using StupidDict.Core.History;

namespace StupidDict.App;

public partial class MainWindow : Window
{
    private static readonly TimeSpan SuggestDebounce = TimeSpan.FromMilliseconds(120);

    private readonly ISpeechPlayer _speech;
    private readonly IAssetDownloader _downloader;
    private readonly AppLocations _locations;
    private DictionaryService _service;
    private readonly LookupNavigator _navigator = new();
    private bool _dictionaryAvailable;
    private CancellationTokenSource? _suggestDebounce;
    private List<string> _suggestions = [];
    private int _suggestSelection = -1;
    private int _suggestGeneration;
    private bool _suppressSuggest;
    private int _searchGeneration;
    private CancellationTokenSource? _dictionaryDownloadCts;
    private CancellationTokenSource? _audioPackCts;
    private readonly bool _autoDownload;

    public MainWindow() : this(new DictionaryService(AppPaths.DictionaryDatabasePath, AppPaths.HistoryDatabasePath))
    {
    }

    public MainWindow(DictionaryService service, ISpeechPlayer? speechPlayer = null,
        IAssetDownloader? downloader = null, AppLocations? locations = null, bool autoDownload = true)
    {
        InitializeComponent();
        _service = service;
        _locations = locations ?? AppLocations.Default;
        _autoDownload = autoDownload;
        _speech = speechPlayer ?? SpeechPlayback.Create(_locations.AudioDirectory);
        _downloader = downloader ?? new AssetDownloadService();
        _dictionaryAvailable = File.Exists(service.DictionaryPath);

        Opened += (_, _) => SearchBox.Focus();
        SearchBox.KeyDown += OnSearchBoxKeyDown;
        SearchBox.TextChanged += OnSearchTextChanged;
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);

        ShowEmptyState();
        RefreshRecents();
        UpdateNavButtons();
        if (_dictionaryAvailable)
        {
            _ = _service.WarmupAsync();
            if (_autoDownload && !AudioPackInstalled())
                StartAudioPackDownload();
        }
        else
        {
            DictionaryDownloadPanel.IsVisible = true;
            HintPanel.IsVisible = false;
            if (_autoDownload)
                StartDictionaryDownload();
        }
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
        if (!_dictionaryAvailable)
        {
            _suppressSuggest = false;
            HideSuggestions();
            return;
        }
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
        if (!_dictionaryAvailable)
            return; // the download panel owns the screen until the dictionary lands

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
        HintPanel.IsVisible = _dictionaryAvailable;
        DictionaryDownloadPanel.IsVisible = !_dictionaryAvailable;
        ResultsPanel.IsVisible = false;
        ResultsPanel.Children.Clear();
    }

    private void RefreshRecents()
    {
        var recent = _service.History.GetRecent();
        RecentPanel.IsVisible = recent.Count > 0;
        RecentList.ItemsSource = recent;
    }

    // ---- asset bootstrap: the dictionary and the pronunciation pack ----

    private bool AudioPackInstalled() =>
        Directory.Exists(Path.Combine(_locations.AudioDirectory, "uk"));

    private void OnDownloadDictionaryClick(object? sender, RoutedEventArgs e) => StartDictionaryDownload();

    private void OnCancelDictionaryClick(object? sender, RoutedEventArgs e) => _dictionaryDownloadCts?.Cancel();

    private async void StartDictionaryDownload()
    {
        if (_dictionaryDownloadCts is not null) return;
        _dictionaryDownloadCts = new CancellationTokenSource();
        DownloadDictionaryButton.IsVisible = false;
        PickDictionaryButton.IsVisible = false;
        CancelDictionaryButton.IsVisible = true;
        DictionaryDownloadBar.IsVisible = true;
        var cancellation = _dictionaryDownloadCts.Token;
        var destination = Path.Combine(Path.GetTempPath(), "stupiddict-downloads", ReleaseAssets.DictionaryAsset);
        try
        {
            var result = await _downloader.DownloadAsync(ReleaseAssets.DictionaryAsset, destination,
                new Progress<DownloadProgress>(UpdateDictionaryProgress), cancellation);
            DictionaryDownloadStatus.Text = "校验中…";
            await VerifyChecksumAsync(result.FilePath, ReleaseAssets.DictionaryAsset, cancellation);
            DictionaryDownloadStatus.Text = "解压中…";
            await Task.Run(() => ExtractZip(result.FilePath, _locations.DataDirectory), cancellation);
            File.Delete(result.FilePath);
            FinishDictionarySetup();
        }
        catch (OperationCanceledException)
        {
            DictionaryDownloadStatus.Text = "已取消下载。可以直接下载，或选择本地已有文件。";
        }
        catch (Exception ex)
        {
            DictionaryDownloadStatus.Text = $"下载失败：{ex.Message}";
        }
        finally
        {
            _dictionaryDownloadCts.Dispose();
            _dictionaryDownloadCts = null;
            DownloadDictionaryButton.IsVisible = true;
            PickDictionaryButton.IsVisible = true;
            CancelDictionaryButton.IsVisible = false;
        }
    }

    private void UpdateDictionaryProgress(DownloadProgress progress)
    {
        if (progress.TotalBytes is { } total && total > 0)
        {
            DictionaryDownloadBar.IsIndeterminate = false;
            DictionaryDownloadBar.Value = 100.0 * progress.ReceivedBytes / total;
            DictionaryDownloadStatus.Text =
                $"正在下载词典 {progress.ReceivedBytes / 1048576.0:F0} / {total / 1048576.0:F0} MB";
        }
        else
        {
            DictionaryDownloadBar.IsIndeterminate = true;
            DictionaryDownloadStatus.Text = $"正在下载词典 {progress.ReceivedBytes / 1048576.0:F0} MB";
        }
    }

    private async void OnPickDictionaryClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择 dictionary.zip 或 dictionary.db",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("词典数据") { Patterns = ["*.zip", "*.db"] }],
        });
        if (files.Count == 0) return;
        var path = files[0].TryGetLocalPath();
        if (path is null) return;

        DictionaryDownloadStatus.Text = "导入中…";
        try
        {
            await Task.Run(() =>
            {
                if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    ExtractZip(path, _locations.DataDirectory);
                else
                    File.Copy(path, Path.Combine(_locations.DataDirectory, "dictionary.db"), overwrite: true);
            });
            if (!File.Exists(Path.Combine(_locations.DataDirectory, "dictionary.db")))
                throw new InvalidOperationException("文件里没有 dictionary.db");
            FinishDictionarySetup();
        }
        catch (Exception ex)
        {
            DictionaryDownloadStatus.Text = $"导入失败：{ex.Message}";
        }
    }

    private void FinishDictionarySetup()
    {
        _service.Dispose();
        _service = new DictionaryService(_locations.DictionaryDatabasePath, _locations.HistoryDatabasePath);
        _dictionaryAvailable = true;
        DictionaryDownloadPanel.IsVisible = false;
        HintPanel.IsVisible = true;
        HintText.Text = "输入单词或中文，按 Enter 查询";
        RefreshRecents();
        _ = _service.WarmupAsync();

        if (_autoDownload && !AudioPackInstalled())
            StartAudioPackDownload();
    }

    private async void StartAudioPackDownload()
    {
        if (_audioPackCts is not null || AudioPackInstalled()) return;
        _audioPackCts = new CancellationTokenSource();
        AudioPackPanel.IsVisible = true;
        AudioPackActionButton.Content = "取消";
        AudioPackActionButton.IsEnabled = true;
        AudioPackBar.IsVisible = true;
        AudioPackStatus.Text = "正在下载发音包（约 1 GB，一次性）";
        var cancellation = _audioPackCts.Token;
        var destination = Path.Combine(Path.GetTempPath(), "stupiddict-downloads", ReleaseAssets.AudioPackAsset);
        try
        {
            var result = await _downloader.DownloadAsync(ReleaseAssets.AudioPackAsset, destination,
                new Progress<DownloadProgress>(UpdateAudioPackProgress), cancellation);
            AudioPackStatus.Text = "校验中…";
            await VerifyChecksumAsync(result.FilePath, ReleaseAssets.AudioPackAsset, cancellation);
            AudioPackStatus.Text = "解压中…";
            AudioPackBar.IsIndeterminate = true;
            await Task.Run(() => ExtractZip(result.FilePath, _locations.AudioDirectory), cancellation);
            File.Delete(result.FilePath);
            AudioPackPanel.IsVisible = false;
        }
        catch (OperationCanceledException)
        {
            AudioPackStatus.Text = "发音包下载已取消。未覆盖的单词会用系统语音朗读。";
            AudioPackActionButton.Content = "下载";
            AudioPackBar.IsVisible = false;
        }
        catch (Exception ex)
        {
            AudioPackStatus.Text = $"发音包下载失败：{ex.Message}";
            AudioPackActionButton.Content = "重试";
            AudioPackBar.IsVisible = false;
        }
        finally
        {
            _audioPackCts.Dispose();
            _audioPackCts = null;
        }
    }

    private void UpdateAudioPackProgress(DownloadProgress progress)
    {
        AudioPackBar.IsIndeterminate = false;
        if (progress.TotalBytes is { } total && total > 0)
        {
            AudioPackBar.Value = 100.0 * progress.ReceivedBytes / total;
            AudioPackStatus.Text =
                $"正在下载发音包 {progress.ReceivedBytes / 1048576.0:F0} / {total / 1048576.0:F0} MB";
        }
        else
        {
            AudioPackStatus.Text = $"正在下载发音包 {progress.ReceivedBytes / 1048576.0:F0} MB";
        }
    }

    private void OnAudioPackActionClick(object? sender, RoutedEventArgs e)
    {
        if (_audioPackCts is not null)
        {
            _audioPackCts.Cancel();
            AudioPackActionButton.IsEnabled = false;
            AudioPackStatus.Text = "正在取消…";
        }
        else
        {
            StartAudioPackDownload();
        }
    }

    private async Task VerifyChecksumAsync(string filePath, string assetName, CancellationToken cancellation)
    {
        var expected = await _downloader.FetchChecksumAsync(assetName, cancellation);
        if (expected is null) return;
        await Task.Run(() => AssetDownloadService.VerifyChecksum(filePath, expected), cancellation);
    }

    /// <summary>
    /// Zip entries cannot escape the destination (zip-slip); extraction is
    /// all-or-nothing because a half-extracted pack would look installed.
    /// </summary>
    private static void ExtractZip(string zipPath, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        var root = Path.GetFullPath(destinationDirectory);
        using (var archive = System.IO.Compression.ZipFile.OpenRead(zipPath))
        {
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.Length == 0) continue;
                var target = Path.GetFullPath(Path.Combine(destinationDirectory, entry.FullName));
                if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) && target != root)
                    throw new InvalidOperationException($"压缩包内出现非法路径：{entry.FullName}");
            }
        }
        System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, destinationDirectory, overwriteFiles: true);
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

        ResultsPanel.Children.Add(BuildPhoneticsLine(entry, margin: new Thickness(2, 4, 0, 0)));

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

        RenderThesaurus(result);
    }

    private void RenderThesaurus(LookupResult result)
    {
        if (result.Synonyms.Count > 0)
        {
            ResultsPanel.Children.Add(Text("近义词", fontSize: 12, color: "#9C9A91", margin: new Thickness(2, 18, 0, 5)));
            foreach (var line in result.Synonyms)
                ResultsPanel.Children.Add(BuildLinkText(PosLineSegments(line), fontSize: 15,
                    margin: new Thickness(2, 0, 0, 0)));
        }

        if (result.Antonyms.Count > 0)
        {
            ResultsPanel.Children.Add(Text("反义词", fontSize: 12, color: "#9C9A91", margin: new Thickness(2, 14, 0, 5)));
            foreach (var line in result.Antonyms)
                ResultsPanel.Children.Add(BuildLinkText(PosLineSegments(line), fontSize: 15,
                    margin: new Thickness(2, 0, 0, 0)));
        }

        if (result.RelatedWords.Count > 0)
        {
            ResultsPanel.Children.Add(Text("联想词", fontSize: 12, color: "#9C9A91", margin: new Thickness(2, 14, 0, 5)));
            List<LinkSegment> segments = [];
            foreach (var related in result.RelatedWords)
            {
                segments.Add(new LinkSegment(related.Word, related.Word));
                if (related.Gloss.Length > 0)
                    segments.Add(new LinkSegment(related.Gloss, null));
                segments.Add(new LinkSegment(";", null));
            }
            ResultsPanel.Children.Add(BuildLinkText(segments, fontSize: 15, margin: new Thickness(2, 0, 0, 0)));
        }
    }

    private static List<LinkSegment> PosLineSegments(SynonymLine line)
    {
        List<LinkSegment> segments = [new LinkSegment(line.Pos + " ", null)];
        foreach (var word in line.Words)
        {
            if (segments.Count > 1) segments.Add(new LinkSegment(", ", null));
            segments.Add(new LinkSegment(word, word));
        }
        return segments;
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
            wordLine.Children.Add(BuildPhoneticsLine(primary));
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

    /// <summary>
    /// The 英/美 phonetic line under the headword. Speaker buttons always
    /// join it — even a word without IPA can be spoken through TTS — so the
    /// line shows whenever there is a primary entry.
    /// </summary>
    private StackPanel BuildPhoneticsLine(DictionaryEntry entry, Thickness? margin = null)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = margin ?? new Thickness(0) };
        if (entry.Phonetic.Length > 0)
            line.Children.Add(Text("英 " + FormatPhonetic(entry.Phonetic), fontSize: 14, color: "#8B897F", mono: true, verticalCenter: true));
        if (entry.UsPhonetic.Length > 0)
            line.Children.Add(Text("美 " + FormatPhonetic(entry.UsPhonetic), fontSize: 14, color: "#8B897F", mono: true, verticalCenter: true));
        line.Children.Add(SpeakerButton(entry.Word, SpeechAccent.British));
        line.Children.Add(SpeakerButton(entry.Word, SpeechAccent.American));
        return line;
    }

    private Button SpeakerButton(string word, SpeechAccent accent)
    {
        var label = accent == SpeechAccent.British ? "UK" : "US";
        var button = new Button { Classes = { "spk" }, Content = label };
        ToolTip.SetTip(button, accent == SpeechAccent.British ? "英音" : "美音");
        button.Click += (_, _) => PlayWord(button, word, accent, label);
        return button;
    }

    private async void PlayWord(Button button, string word, SpeechAccent accent, string label)
    {
        if (_speech.Play(word, accent)) return;
        // No engine could speak. When the pack is simply missing, surface the
        // download entry; otherwise flash the button instead of failing silently.
        if (!AudioPackInstalled() && _audioPackCts is null)
        {
            AudioPackPanel.IsVisible = true;
            AudioPackActionButton.Content = "下载";
            AudioPackStatus.Text = "该词没有本地发音。下载发音包可获得离线英/美真人发音。";
        }
        button.Content = "✕";
        await Task.Delay(1500);
        if (button.Content is "✕") button.Content = label;
    }

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

    /// <summary>One piece of a link line: plain text, or a clickable word link.</summary>
    private sealed record LinkSegment(string Text, string? Target);

    /// <summary>
    /// A wrapping text line mixing plain runs and single-click word links. Link
    /// words are real controls embedded via InlineUIContainer: character
    /// hit-testing on a multi-run SelectableTextBlock proved unreliable in
    /// Avalonia 11.3 (clicks resolving to neighbouring characters, occasionally
    /// even IndexOutOfRangeException inside GlyphRun.FindNearestCharacterHit).
    /// </summary>
    private SelectableTextBlock BuildLinkText(IReadOnlyList<LinkSegment> segments, double fontSize,
        string color = "#55534B", Thickness? margin = null)
    {
        var inlines = new InlineCollection();
        foreach (var segment in segments)
        {
            if (segment.Target is { } target)
                inlines.Add(CreateLinkInline(target, fontSize));
            else
                inlines.Add(new Run(segment.Text));
        }

        return new SelectableTextBlock
        {
            FontSize = fontSize,
            Foreground = Brush.Parse(color),
            TextWrapping = TextWrapping.Wrap,
            Margin = margin ?? new Thickness(0),
            LineHeight = 22,
            Inlines = inlines,
        };
    }

    private InlineUIContainer CreateLinkInline(string word, double fontSize)
    {
        // The Border is the click surface: hit testing on an inline TextBlock
        // only covers its currently shaped glyphs (and desyncs inside
        // InlineUIContainer), so clicks in glyph gaps used to fall through.
        var link = new TextBlock { Text = word, FontSize = fontSize, Classes = { "wordlink" } };
        var surface = new Border
        {
            Background = Brushes.Transparent,
            Child = link,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        surface.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            SetQueryText(word);
            RunSearch(word);
        };
        return new InlineUIContainer { Child = surface };
    }

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
