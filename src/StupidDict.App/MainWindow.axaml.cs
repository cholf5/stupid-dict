using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using StupidDict.App.Assets;
using StupidDict.App.Localization;
using StupidDict.App.Settings;
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
    private readonly AppSettings _settings;
    private SettingsWindow? _settingsWindow;

    /// <summary>
    /// Rebuilds the currently rendered result page with fresh palette values;
    /// XAML styles recolor themselves via DynamicResource, but the result page
    /// is built in code, so a theme switch replays its render closure.
    /// </summary>
    private Action? _rebuildResults;

    public MainWindow() : this(new DictionaryService(AppPaths.DictionaryDatabasePath, AppPaths.HistoryDatabasePath))
    {
    }

    public MainWindow(DictionaryService service, ISpeechPlayer? speechPlayer = null,
        IAssetDownloader? downloader = null, AppLocations? locations = null, bool autoDownload = true,
        AppSettings? settings = null)
    {
        InitializeComponent();
        _service = service;
        _locations = locations ?? AppLocations.Default;
        _autoDownload = autoDownload;
        // Tests pass no settings: never read the user's real settings file.
        _settings = settings ?? new AppSettings();
        _speech = speechPlayer ?? SpeechPlayback.Create(_locations.AudioDirectory);
        _downloader = downloader ?? new AssetDownloadService();
        _dictionaryAvailable = File.Exists(service.DictionaryPath);

        Opened += (_, _) =>
        {
            SearchBox.Focus();
            UpdateSearchBoxLineMetrics();
        };
        SearchBox.KeyDown += OnSearchBoxKeyDown;
        SearchBox.TextChanged += OnSearchTextChanged;
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        ActualThemeVariantChanged += OnActualThemeVariantChanged;
        // A language switch re-renders the result page just like a theme
        // switch does; XAML bindings refresh themselves.
        Translations.Instance.PropertyChanged += OnTranslationsChanged;
        Closed += (_, _) => Translations.Instance.PropertyChanged -= OnTranslationsChanged;

        ShowEmptyState();
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
            case Key.OemComma when cmdCtrl:
                e.Handled = true;
                OpenSettings();
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

    private void OnClearSearchClick(object? sender, RoutedEventArgs e)
    {
        // Same semantics as the Escape shortcut: clear input and results,
        // then put the caret back for the next word.
        SearchBox.Clear();
        ShowEmptyState();
        SearchBox.Focus();
    }

    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        // The rightmost accessory slot swaps between settings and clear:
        // an empty box shows the gear, any input swaps it for the ✕.
        var hasText = !string.IsNullOrEmpty(SearchBox.Text);
        ClearSearchButton.IsVisible = hasText;
        SettingsButton.IsVisible = !hasText;
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

    private void OnSettingsClick(object? sender, RoutedEventArgs e) => OpenSettings();

    /// <summary>
    /// Single-instance modal dialog. While it is open the main window is
    /// disabled, so the ⌘, "toggle" closes from the settings side (its own
    /// key handler): here it only ever opens, and the re-open guard just
    /// focuses the existing dialog.
    /// </summary>
    private void OpenSettings()
    {
        if (_settingsWindow is { } open)
        {
            open.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(_settings);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _ = _settingsWindow.ShowDialog(this);
    }

    private void OnActualThemeVariantChanged(object? sender, EventArgs e)
    {
        if (ResultsPanel.IsVisible)
            _rebuildResults?.Invoke();
    }

    private void OnTranslationsChanged(object? sender, PropertyChangedEventArgs e)
    {
        UpdateSearchBoxLineMetrics();
        if (ResultsPanel.IsVisible)
            _rebuildResults?.Invoke();
    }

    private static double? _cjkLineHeight;
    private static double _cjkLineHeightFontSize;

    /// <summary>
    /// The empty caret line is laid out with the primary font's (Inter)
    /// metrics, but a CJK watermark is shaped through the platform's CJK
    /// fallback font, whose line metrics are much taller — the caret then
    /// floats above the placeholder text (English watermarks use the primary
    /// font and align on their own). Give the caret the fallback font's
    /// natural line height so it covers the placeholder glyphs exactly like
    /// it does while typing CJK.
    /// </summary>
    private void UpdateSearchBoxLineMetrics()
    {
        SearchBox.LineHeight = DictionaryService.IsChineseQuery(Translations.Instance.SearchWatermark)
            ? MeasureCjkLineHeight(SearchBox.FontSize) ?? double.NaN
            : double.NaN;
    }

    private static double? MeasureCjkLineHeight(double fontSize)
    {
        if (_cjkLineHeight.HasValue && _cjkLineHeightFontSize == fontSize)
            return _cjkLineHeight;
        double? lineHeight = null;
        if (FontManager.Current.TryMatchCharacter('输', FontStyle.Normal, FontWeight.Normal,
                FontStretch.Normal, null, null, out var typeface))
        {
            using var layout = new TextLayout("输", typeface, fontSize, Brushes.Black,
                TextAlignment.Left, TextWrapping.NoWrap);
            lineHeight = layout.Height;
        }
        _cjkLineHeight = lineHeight;
        _cjkLineHeightFontSize = fontSize;
        return lineHeight;
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
        _rebuildResults = null;
        RefreshRecents();
    }

    private void RefreshRecents()
    {
        var recent = _service.History.GetRecent();
        // The strip is empty-state chrome: a result page owns the whole window
        // (←/→ covers "back to a recent word" mid-session), and the strip
        // returns when the query is cleared. Visibility toggles instantly —
        // a fade-out would have to keep IsVisible (and this row's layout
        // space) alive for the animation, leaving a blank band pressed over
        // the bottom of the results page before snapping away.
        RecentList.ItemsSource = recent;
        RecentPanel.IsVisible = recent.Count > 0 && !ResultsPanel.IsVisible;
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
            var zipPath = await DownloadAndVerifyAsync(ReleaseAssets.DictionaryAsset, destination,
                new Progress<DownloadProgress>(UpdateDictionaryProgress),
                text => DictionaryDownloadStatus.Text = text,
                Translations.Instance.DownloadFailedFormat, cancellation);
            DictionaryDownloadStatus.Text = Translations.Instance.Extracting;
            await Task.Run(() => ExtractZip(zipPath, _locations.DataDirectory), cancellation);
            File.Delete(zipPath);
            FinishDictionarySetup();
        }
        catch (OperationCanceledException)
        {
            DictionaryDownloadStatus.Text = Translations.Instance.DownloadCancelled;
        }
        catch (AssetBootstrapException ex)
        {
            // Download/checksum failures carry their stage in the message already.
            DictionaryDownloadStatus.Text = ex.Message;
        }
        catch (Exception ex)
        {
            DictionaryDownloadStatus.Text = string.Format(Translations.Instance.ExtractFailedFormat, ex.Message);
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

    /// <summary>
    /// Download plus checksum with one purge-and-redownload recovery: a
    /// ".part" resumed across asset versions (or a corrupted transfer) yields
    /// a file that only the checksum can catch, and the old flow surfaced
    /// that as a baffling "download failed" long after the bar had filled.
    /// Deleting the artifacts and starting over turns the deterministic
    /// failure into a self-healing retry.
    /// </summary>
    private async Task<string> DownloadAndVerifyAsync(string assetName, string destinationFile,
        IProgress<DownloadProgress> progress, Action<string> setStatus, string downloadFailedFormat,
        CancellationToken cancellation)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var filePath = (await _downloader.DownloadAsync(assetName, destinationFile, progress, cancellation)).FilePath;
                setStatus(Translations.Instance.Verifying);
                var expected = await _downloader.FetchChecksumAsync(assetName, cancellation);
                if (expected is not null)
                    await Task.Run(() => AssetDownloadService.VerifyChecksum(filePath, expected), cancellation);
                return filePath;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ChecksumMismatchException ex)
            {
                // The message promises the corrupted file is gone — make it true,
                // so the retry cannot resume from the damaged bytes.
                PurgeDownloadArtifacts(destinationFile);
                if (attempt > 1)
                    throw new AssetBootstrapException(Translations.Instance.ChecksumFailed, ex);
                setStatus(Translations.Instance.ChecksumRedownloading);
            }
            catch (Exception ex)
            {
                throw new AssetBootstrapException(string.Format(downloadFailedFormat, ex.Message), ex);
            }
        }
    }

    private static void PurgeDownloadArtifacts(string destinationFile)
    {
        File.Delete(destinationFile);
        File.Delete(destinationFile + ".part");
    }

    private void UpdateDictionaryProgress(DownloadProgress progress)
    {
        if (progress.TotalBytes is { } total && total > 0)
        {
            DictionaryDownloadBar.IsIndeterminate = false;
            DictionaryDownloadBar.Value = 100.0 * progress.ReceivedBytes / total;
            // A second full-looking bar is a resumed attempt on another source,
            // not a stuck download — say so instead of "downloading" again.
            DictionaryDownloadStatus.Text = progress.ResumedFromBytes > 0
                ? string.Format(Translations.Instance.DownloadResumeFormat,
                    progress.ReceivedBytes / 1048576.0, total / 1048576.0)
                : string.Format(Translations.Instance.DownloadingDictionaryFormat,
                    progress.ReceivedBytes / 1048576.0, total / 1048576.0);
        }
        else
        {
            DictionaryDownloadBar.IsIndeterminate = true;
            DictionaryDownloadStatus.Text = string.Format(
                progress.ResumedFromBytes > 0
                    ? Translations.Instance.DownloadResumeUnsizedFormat
                    : Translations.Instance.DownloadingDictionaryUnsizedFormat,
                progress.ReceivedBytes / 1048576.0);
        }
    }

    private async void OnPickDictionaryClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Translations.Instance.PickerTitle,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(Translations.Instance.FileTypeDictionary)
                { Patterns = ["*.zip", "*.db"] }],
        });
        if (files.Count == 0) return;
        var path = files[0].TryGetLocalPath();
        if (path is null) return;

        DictionaryDownloadStatus.Text = Translations.Instance.Importing;
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
                throw new InvalidOperationException(Translations.Instance.ImportMissingDb);
            FinishDictionarySetup();
        }
        catch (Exception ex)
        {
            DictionaryDownloadStatus.Text = string.Format(Translations.Instance.ImportFailedFormat, ex.Message);
        }
    }

    private void FinishDictionarySetup()
    {
        _service.Dispose();
        _service = new DictionaryService(_locations.DictionaryDatabasePath, _locations.HistoryDatabasePath);
        _dictionaryAvailable = true;
        DictionaryDownloadPanel.IsVisible = false;
        HintPanel.IsVisible = true;
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
        AudioPackActionButton.Content = Translations.Instance.Cancel;
        AudioPackActionButton.IsEnabled = true;
        PickAudioPackButton.IsVisible = false;
        AudioPackDownloadPageButton.IsVisible = false;
        AudioPackBar.IsVisible = true;
        AudioPackStatus.Text = Translations.Instance.DownloadingAudioPack;
        var cancellation = _audioPackCts.Token;
        var destination = Path.Combine(Path.GetTempPath(), "stupiddict-downloads", ReleaseAssets.AudioPackAsset);
        try
        {
            var zipPath = await DownloadAndVerifyAsync(ReleaseAssets.AudioPackAsset, destination,
                new Progress<DownloadProgress>(UpdateAudioPackProgress),
                text => AudioPackStatus.Text = text,
                Translations.Instance.AudioPackFailedFormat, cancellation);
            AudioPackStatus.Text = Translations.Instance.Extracting;
            AudioPackBar.IsIndeterminate = true;
            await Task.Run(() => ExtractZip(zipPath, _locations.AudioDirectory), cancellation);
            File.Delete(zipPath);
            AudioPackPanel.IsVisible = false;
        }
        catch (OperationCanceledException)
        {
            AudioPackStatus.Text = Translations.Instance.AudioPackCancelled;
            AudioPackActionButton.Content = Translations.Instance.AudioPackDownloadButton;
            PickAudioPackButton.IsVisible = true;
            AudioPackDownloadPageButton.IsVisible = true;
            AudioPackBar.IsVisible = false;
        }
        catch (AssetBootstrapException ex)
        {
            // Download/checksum failures carry their stage in the message already.
            AudioPackStatus.Text = ex.Message;
            AudioPackActionButton.Content = Translations.Instance.Retry;
            PickAudioPackButton.IsVisible = true;
            AudioPackDownloadPageButton.IsVisible = true;
            AudioPackBar.IsVisible = false;
        }
        catch (Exception ex)
        {
            AudioPackStatus.Text = string.Format(Translations.Instance.AudioPackExtractFailedFormat, ex.Message);
            AudioPackActionButton.Content = Translations.Instance.Retry;
            PickAudioPackButton.IsVisible = true;
            AudioPackDownloadPageButton.IsVisible = true;
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
            AudioPackStatus.Text = progress.ResumedFromBytes > 0
                ? string.Format(Translations.Instance.DownloadResumeFormat,
                    progress.ReceivedBytes / 1048576.0, total / 1048576.0)
                : string.Format(Translations.Instance.DownloadingAudioPackFormat,
                    progress.ReceivedBytes / 1048576.0, total / 1048576.0);
        }
        else
        {
            AudioPackStatus.Text = string.Format(
                progress.ResumedFromBytes > 0
                    ? Translations.Instance.DownloadResumeUnsizedFormat
                    : Translations.Instance.DownloadingAudioPackUnsizedFormat,
                progress.ReceivedBytes / 1048576.0);
        }
    }

    private void OnAudioPackActionClick(object? sender, RoutedEventArgs e)
    {
        if (_audioPackCts is not null)
        {
            _audioPackCts.Cancel();
            AudioPackActionButton.IsEnabled = false;
            AudioPackStatus.Text = Translations.Instance.Cancelling;
        }
        else
        {
            StartAudioPackDownload();
        }
    }

    private async void OnPickAudioPackClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Translations.Instance.PickerTitleAudioPack,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(Translations.Instance.FileTypeAudioPack)
                { Patterns = ["*.zip"] }],
        });
        if (files.Count == 0) return;
        var path = files[0].TryGetLocalPath();
        if (path is null) return;

        AudioPackStatus.Text = Translations.Instance.Importing;
        AudioPackActionButton.IsEnabled = false;
        try
        {
            await Task.Run(() => ImportAudioPack(path, _locations.AudioDirectory));
            AudioPackPanel.IsVisible = false;
        }
        catch (Exception ex)
        {
            AudioPackStatus.Text = string.Format(Translations.Instance.ImportFailedFormat, ex.Message);
            AudioPackActionButton.Content = Translations.Instance.Retry;
            AudioPackActionButton.IsEnabled = true;
            PickAudioPackButton.IsVisible = true;
            AudioPackDownloadPageButton.IsVisible = true;
        }
    }

    private void OnOpenDownloadPageClick(object? sender, RoutedEventArgs e) =>
        _ = TopLevel.GetTopLevel(this)?.Launcher.LaunchUriAsync(new Uri(ReleaseAssets.DataReleasePageUrl));

    /// <summary>
    /// Validates that the zip really is a pronunciation pack (uk/ or us/ at
    /// the root) before extracting — importing a wrong zip must not scatter
    /// junk inside the audio directory.
    /// </summary>
    internal static void ImportAudioPack(string zipPath, string audioDirectory)
    {
        using var archive = System.IO.Compression.ZipFile.OpenRead(zipPath);
        var hasPack = archive.Entries.Any(entry =>
            entry.FullName.StartsWith("uk/", StringComparison.Ordinal) ||
            entry.FullName.StartsWith("us/", StringComparison.Ordinal));
        if (!hasPack)
            throw new InvalidOperationException(Translations.Instance.ImportMissingPack);
        ExtractZip(zipPath, audioDirectory);
    }

    /// <summary>
    /// Zip entries cannot escape the destination (zip-slip). Extraction is
    /// all-or-nothing: the archive unpacks into a staging directory inside
    /// the destination and only then moves into place — a half-extracted
    /// pack must never look installed (AudioPackInstalled checks for uk/).
    /// </summary>
    internal static void ExtractZip(string zipPath, string destinationDirectory)
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
                    throw new InvalidOperationException(
                        string.Format(Translations.Instance.ZipSlipFormat, entry.FullName));
            }
        }

        // Sweep staging directories a crashed run may have left behind.
        const string stagingPrefix = ".stupiddict-extracting-";
        foreach (var stale in Directory.EnumerateDirectories(destinationDirectory, stagingPrefix + "*"))
            Directory.Delete(stale, recursive: true);

        var staging = Path.Combine(destinationDirectory, stagingPrefix + Guid.NewGuid().ToString("N"));
        try
        {
            System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, staging);
            // Materialize before moving: moving entries out of staging while
            // lazily enumerating it races the enumerator (the pack has two).
            foreach (var entry in Directory.EnumerateFileSystemEntries(staging).ToArray())
                MoveIntoPlace(entry, Path.Combine(destinationDirectory, Path.GetFileName(entry)));
        }
        finally
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
        }
    }

    // Same-volume renames, so moving is cheap and the destination never holds
    // half-written data. Existing entries are replaced wholesale, matching the
    // overwriteFiles behaviour this replaced.
    private static void MoveIntoPlace(string source, string target)
    {
        if (File.Exists(target))
            File.Delete(target);
        else if (Directory.Exists(target))
            Directory.Delete(target, recursive: true);
        if (File.Exists(source))
            File.Move(source, target, overwrite: true);
        else
            Directory.Move(source, target);
    }

    private void RenderResult(LookupResult result)
    {
        HintPanel.IsVisible = false;
        ResultsPanel.IsVisible = true;
        RecentPanel.IsVisible = false;
        ResultsPanel.Children.Clear();
        _rebuildResults = () => RenderResult(result);

        if (result.IsChineseQuery)
            RenderChineseResult(result);
        else
            RenderEnglishResult(result);
    }

    private void RenderEnglishResult(LookupResult result)
    {
        if (result.Primary is not { } entry)
        {
            ResultsPanel.Children.Add(Text(string.Format(Translations.Instance.NoResultFormat, result.Query),
                20, FontWeight.SemiBold, Palette.TextTitle, margin: new Thickness(2, 8, 0, 0)));
            if (result.WordSuggestions.Count > 0)
            {
                ResultsPanel.Children.Add(Text(Translations.Instance.DidYouMean, fontSize: 12, brushKey: Palette.TextMuted, margin: new Thickness(2, 18, 0, 8)));
                ResultsPanel.Children.Add(BuildChips(result.WordSuggestions.Select(w => w.Word)));
            }
            else
            {
                ResultsPanel.Children.Add(Text(Translations.Instance.TryShorter, fontSize: 14, brushKey: Palette.TextMuted, margin: new Thickness(2, 10, 0, 0)));
            }
            return;
        }

        ResultsPanel.Children.Add(Text(entry.Word, 30, FontWeight.SemiBold, Palette.TextStrong, margin: new Thickness(2, 0, 0, 0)));

        if (result.WordFormNote is { } note)
            ResultsPanel.Children.Add(Text($"{result.Query} → {note}", fontSize: 13, brushKey: Palette.TextMuted, margin: new Thickness(2, 4, 0, 0)));

        ResultsPanel.Children.Add(BuildPhoneticsLine(entry, margin: new Thickness(2, 4, 0, 0)));

        if (entry.Chinese.Length > 0)
        {
            var chinese = new StackPanel { Spacing = 5, Margin = new Thickness(2, 14, 0, 0) };
            foreach (var line in SplitLines(entry.Chinese))
                chinese.Children.Add(Text(line, 16, brushKey: Palette.TextBody));
            ResultsPanel.Children.Add(chinese);
        }

        if (entry.English.Length > 0)
        {
            ResultsPanel.Children.Add(Text(Translations.Instance.EnglishDefinitions, fontSize: 12, brushKey: Palette.TextMuted, margin: new Thickness(2, 18, 0, 5)));
            ResultsPanel.Children.Add(Text(entry.English, fontSize: 14, brushKey: Palette.TextSecondary, margin: new Thickness(2, 0, 0, 0), lineHeight: 22));
        }

        RenderThesaurus(result);
    }

    private void RenderThesaurus(LookupResult result)
    {
        if (result.Synonyms.Count > 0)
        {
            ResultsPanel.Children.Add(Text(Translations.Instance.Synonyms, fontSize: 12, brushKey: Palette.TextMuted, margin: new Thickness(2, 18, 0, 5)));
            foreach (var line in result.Synonyms)
                ResultsPanel.Children.Add(BuildLinkText(PosLineSegments(line), fontSize: 15,
                    margin: new Thickness(2, 0, 0, 0)));
        }

        if (result.Antonyms.Count > 0)
        {
            ResultsPanel.Children.Add(Text(Translations.Instance.Antonyms, fontSize: 12, brushKey: Palette.TextMuted, margin: new Thickness(2, 14, 0, 5)));
            foreach (var line in result.Antonyms)
                ResultsPanel.Children.Add(BuildLinkText(PosLineSegments(line), fontSize: 15,
                    margin: new Thickness(2, 0, 0, 0)));
        }

        if (result.RelatedWords.Count > 0)
        {
            ResultsPanel.Children.Add(Text(Translations.Instance.RelatedWords, fontSize: 12, brushKey: Palette.TextMuted, margin: new Thickness(2, 14, 0, 5)));
            List<LinkSegment> segments = [];
            foreach (var related in result.RelatedWords)
            {
                segments.Add(new LinkSegment(related.Word, related.Word));
                if (related.Gloss.Length > 0)
                    segments.Add(new LinkSegment(" " + related.Gloss, null));
                segments.Add(new LinkSegment("; ", null));
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
            ResultsPanel.Children.Add(Text(string.Format(Translations.Instance.NoResultFormat, result.Query),
                20, FontWeight.SemiBold, Palette.TextTitle, margin: new Thickness(2, 8, 0, 0)));
            return;
        }

        ResultsPanel.Children.Add(Text(result.Query, 30, FontWeight.SemiBold, Palette.TextStrong, margin: new Thickness(2, 0, 0, 0)));

        if (result.Primary is { } primary)
        {
            var wordLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(2, 8, 0, 0) };
            wordLine.Children.Add(Text(primary.Word, 20, FontWeight.SemiBold, Palette.TextBody, verticalCenter: true));
            wordLine.Children.Add(BuildPhoneticsLine(primary));
            ResultsPanel.Children.Add(wordLine);

            if (primary.English.Length > 0)
                ResultsPanel.Children.Add(Text(primary.English, fontSize: 14, brushKey: Palette.TextSecondary, margin: new Thickness(2, 6, 0, 0), lineHeight: 22));

            if (primary.Chinese.Length > 0)
            {
                var chinese = new StackPanel { Spacing = 5, Margin = new Thickness(2, 12, 0, 0) };
                foreach (var line in SplitLines(primary.Chinese))
                    chinese.Children.Add(Text(line, 16, brushKey: Palette.TextBody));
                ResultsPanel.Children.Add(chinese);
            }
        }

        var others = result.ChineseMatches
            .Where(m => result.Primary is null || !string.Equals(m.Entry.Word, result.Primary.Word, StringComparison.OrdinalIgnoreCase))
            .Take(12)
            .ToList();
        if (others.Count > 0)
        {
            ResultsPanel.Children.Add(Text(result.Primary is null
                ? Translations.Instance.RelatedEntries
                : Translations.Instance.OtherEntries, fontSize: 12, brushKey: Palette.TextMuted, margin: new Thickness(2, 18, 0, 8)));
            ResultsPanel.Children.Add(BuildChips(others.Select(m => m.Entry.Word)));
        }
    }

    private void RenderError(string query, Exception ex)
    {
        HintPanel.IsVisible = false;
        ResultsPanel.IsVisible = true;
        RecentPanel.IsVisible = false;
        ResultsPanel.Children.Clear();
        _rebuildResults = () => RenderError(query, ex);
        ResultsPanel.Children.Add(Text(string.Format(Translations.Instance.LookupErrorFormat, query),
            20, FontWeight.SemiBold, Palette.ErrorForeground, margin: new Thickness(2, 8, 0, 0)));
        ResultsPanel.Children.Add(Text(ex.Message, fontSize: 13, brushKey: Palette.TextMuted, margin: new Thickness(2, 8, 0, 0)));
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
    /// The phonetic line under the headword: one speaker button per accent,
    /// followed by its selectable phonetic text. The buttons always join the
    /// line — even a word without IPA can be spoken through TTS — so the line
    /// shows whenever there is a primary entry.
    /// </summary>
    private StackPanel BuildPhoneticsLine(DictionaryEntry entry, Thickness? margin = null)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = margin ?? new Thickness(0) };
        line.Children.Add(AccentGroup(entry.Word, SpeechAccent.British, entry.Phonetic));
        line.Children.Add(AccentGroup(entry.Word, SpeechAccent.American, entry.UsPhonetic));
        return line;
    }

    private StackPanel AccentGroup(string word, SpeechAccent accent, string phonetic)
    {
        var group = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        group.Children.Add(SpeakerButton(word, accent));
        // The accent label stays visible even without IPA (TTS still speaks):
        // formatting the prefix with an empty phonetic yields 英/美 or UK/US.
        var prefix = accent == SpeechAccent.British
            ? Translations.Instance.UkPhoneticPrefix
            : Translations.Instance.UsPhoneticPrefix;
        var label = string.Format(prefix, phonetic.Length > 0 ? FormatPhonetic(phonetic) : string.Empty).Trim();
        group.Children.Add(Text(label, fontSize: 14, brushKey: Palette.TextMuted, mono: true, verticalCenter: true));
        return group;
    }

    private Button SpeakerButton(string word, SpeechAccent accent)
    {
        var button = new Button
        {
            Classes = { "spk" },
            Name = accent == SpeechAccent.British ? "UkSpeakerButton" : "UsSpeakerButton",
            Content = SpeakerIcon(),
        };
        ToolTip.SetTip(button, accent == SpeechAccent.British
            ? Translations.Instance.UkTip
            : Translations.Instance.UsTip);
        button.Click += (_, _) => PlayWord(button, word, accent);
        return button;
    }

    /// <summary>
    /// Material Icons volume_up (Apache-2.0). Fill follows the button
    /// foreground through the Button.spk &gt; Path style.
    /// </summary>
    private static readonly Geometry SpeakerIconGeometry = StreamGeometry.Parse(
        "M3 9v6h4l5 5V4L7 9H3zm13.5 3c0-1.77-1.02-3.29-2.5-4.03v8.05c1.48-.73 2.5-2.25 2.5-4.02zM14 3.23v2.06c2.89.86 5 3.54 5 6.71s-2.11 5.85-5 6.71v2.06c4.01-.91 7-4.49 7-8.77s-2.99-7.86-7-8.77z");

    private static Avalonia.Controls.Shapes.Path SpeakerIcon() => new()
    {
        Width = 12,
        Height = 12,
        Stretch = Stretch.Uniform,
        Data = SpeakerIconGeometry,
    };

    private async void PlayWord(Button button, string word, SpeechAccent accent)
    {
        if (_speech.Play(word, accent)) return;
        // No engine could speak. When the pack is simply missing, surface the
        // download entry; otherwise flash the button instead of failing silently.
        if (!AudioPackInstalled() && _audioPackCts is null)
        {
            AudioPackPanel.IsVisible = true;
            AudioPackActionButton.Content = Translations.Instance.AudioPackDownloadButton;
            AudioPackStatus.Text = Translations.Instance.NoLocalPronunciation;
        }
        var original = button.Content;
        button.Content = "✕";
        await Task.Delay(1500);
        if (button.Content is "✕") button.Content = original;
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
        string brushKey = Palette.TextSecondary, Thickness? margin = null)
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
            Foreground = Palette.Get(brushKey, ActualThemeVariant),
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
        string brushKey = Palette.TextBody, bool mono = false, Thickness? margin = null,
        double? lineHeight = null, bool verticalCenter = false)
    {
        var block = new SelectableTextBlock
        {
            Text = value,
            FontSize = fontSize,
            FontWeight = weight,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Palette.Get(brushKey, ActualThemeVariant),
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
