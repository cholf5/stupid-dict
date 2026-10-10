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
    // Destinations whose complete zip this session has accepted (checksum
    // verified, or none published): a later attempt reuses the file instead
    // of re-downloading it. Only this class writes these paths, so the file
    // cannot change under a session-verified entry; the File.Exists check in
    // the reuse path makes a stale entry (post-extraction delete) harmless.
    private readonly HashSet<string> _verifiedZips = new(StringComparer.Ordinal);
    // Where downloaded asset zips live until extraction succeeds. Defaults to
    // the per-user temp directory; tests inject a scratch copy so runs stay
    // hermetic (a kept zip would otherwise change what the next attempt does).
    private readonly string _downloadDirectory;
    private readonly bool _autoDownload;
    private readonly AppSettings _settings;
    // Reopen guard for the single-instance modal settings dialog; internal for
    // tests, which replay the "ShowDialog failed before the window appeared"
    // state that the Closed handler alone cannot clear.
    internal SettingsWindow? _settingsWindow;
    // Last client size seen while the window was Normal; Width/Height are
    // clobbered by maximize (Window.HandleResized assigns them on every
    // platform resize), so a close in a maximized state saves these instead.
    private double _lastNormalWidth;
    private double _lastNormalHeight;

    /// <summary>
    /// Rebuilds the currently rendered result page with fresh palette values;
    /// XAML styles recolor themselves via DynamicResource, but the result page
    /// is built in code, so a theme switch replays its render closure.
    /// </summary>
    private Action? _rebuildResults;

    /// <summary>
    /// Test seam (InternalsVisibleTo): raised after every result-page render,
    /// i.e. once per <c>_rebuildResults</c> closure execution — the
    /// language-switch consolidation test counts these (B-002).
    /// </summary>
    internal Action? ResultRendered;

    /// <summary>
    /// Test seam (InternalsVisibleTo): stands in for the dictionary lookup so
    /// a test can hold a query in flight across a Navigate and complete it at
    /// a chosen moment (B-003). Null in production; RunSearch falls back to
    /// <see cref="DictionaryService.LookupAsync"/>.
    /// </summary>
    internal Func<string, Task<LookupResult>>? LookupOverride;

    /// <summary>
    /// Test seam (InternalsVisibleTo): stands in for the file-picker call so a
    /// test can fail it deterministically — IStorageProvider is marked
    /// NotClientImplementable, so no fake can implement it. Null in
    /// production; both pick handlers fall back to
    /// <see cref="TopLevel.StorageProvider"/>.OpenFilePickerAsync.
    /// </summary>
    internal Func<FilePickerOpenOptions, Task<IReadOnlyList<IStorageFile>>>? OpenFilePickerOverride;

    public MainWindow() : this(new DictionaryService(AppPaths.DictionaryDatabasePath, AppPaths.HistoryDatabasePath))
    {
    }

    public MainWindow(DictionaryService service, ISpeechPlayer? speechPlayer = null,
        IAssetDownloader? downloader = null, AppLocations? locations = null, bool autoDownload = true,
        AppSettings? settings = null, string? downloadDirectory = null)
    {
        InitializeComponent();
        _service = service;
        _locations = locations ?? AppLocations.Default;
        _autoDownload = autoDownload;
        // Tests pass no settings: never read the user's real settings file.
        _settings = settings ?? new AppSettings();
        _speech = speechPlayer ?? SpeechPlayback.Create(_locations.AudioDirectory, _locations.AudioPackDatabasePath);
        _downloader = downloader ?? new AssetDownloadService();
        _downloadDirectory = downloadDirectory ?? Path.Combine(Path.GetTempPath(), "stupiddict-downloads");
        _dictionaryAvailable = File.Exists(service.DictionaryPath);

        // Restore the size recorded at last close (XAML defaults otherwise);
        // the maximized flag rides along. WindowState is a styled property,
        // so setting it before Show reaches the platform at show time.
        if (_settings.WindowWidth is { } savedWidth) Width = Math.Max(MinWidth, savedWidth);
        if (_settings.WindowHeight is { } savedHeight) Height = Math.Max(MinHeight, savedHeight);
        _lastNormalWidth = Width;
        _lastNormalHeight = Height;
        if (_settings.WindowMaximized)
            WindowState = WindowState.Maximized;

        Opened += (_, _) =>
        {
            SearchBox.Focus();
            UpdateSearchBoxLineMetrics();
            ClampWindowToScreen();
        };
        SearchBox.KeyDown += OnSearchBoxKeyDown;
        SearchBox.TextChanged += OnSearchTextChanged;
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        ActualThemeVariantChanged += OnActualThemeVariantChanged;
        // A language switch re-renders the result page just like a theme
        // switch does; XAML bindings refresh themselves.
        Translations.Instance.PropertyChanged += OnTranslationsChanged;
        Closed += (_, _) => Translations.Instance.PropertyChanged -= OnTranslationsChanged;
        Closed += (_, _) => SaveWindowBounds();
        // Exit stops playback in flight (B-012): the Unix process players would
        // otherwise keep speaking their word to the end as orphans after the
        // app quits, and the Windows MCI alias gets an explicit close instead
        // of relying on process teardown. Closed (not Closing — a close can be
        // vetoed there) runs on the UI thread, which the MCI backend requires
        // (DirectShow is STA-only); the default lifetime shuts down when the
        // last suitable window closes, so this is the app-exit point, and
        // headless tests reach the same handler by closing the window. Stop is
        // best-effort and never throws.
        Closed += (_, _) => _speech.Stop();

        ShowEmptyState();
        UpdateNavButtons();
        if (_dictionaryAvailable)
        {
            _ = WarmupQuietlyAsync(_service);
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

    /// <summary>
    /// Fire-and-forget wrapper that keeps the warm-up's exception observed: a
    /// corrupt or locked dictionary makes <see cref="DictionaryService.WarmupAsync"/>
    /// fault, and the discarded task would otherwise land in
    /// UnobservedTaskException while silently doing nothing — the user first
    /// learns of the problem from the failed lookup (RenderError). Warm-up
    /// stays best-effort; there is nothing to do with the exception here.
    /// Internal static so a test can drive it against a corrupt database.
    /// </summary>
    internal static async Task WarmupQuietlyAsync(DictionaryService service)
    {
        try
        {
            await service.WarmupAsync().ConfigureAwait(false);
        }
        catch
        {
            // surfaced by the first lookup; nothing this early can act on it
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        // Bounds only counts as the user's chosen size while the window is
        // Normal (maximize/fullscreen own the bounds otherwise); the min-size
        // gate keeps the pre-layout 0x0 out.
        if (e.Property == Visual.BoundsProperty && WindowState == WindowState.Normal
            && Bounds.Width >= MinWidth && Bounds.Height >= MinHeight)
        {
            _lastNormalWidth = Bounds.Width;
            _lastNormalHeight = Bounds.Height;
        }
    }

    private void SaveWindowBounds()
    {
        // Setting the properties persists them: WireSettings saves on any of
        // the Window* changes. Unwired instances (tests) just mutate in memory.
        _settings.WindowWidth = _lastNormalWidth;
        _settings.WindowHeight = _lastNormalHeight;
        _settings.WindowMaximized = WindowState == WindowState.Maximized;
    }

    private void ClampWindowToScreen()
    {
        // A size recorded on a larger (or since-unplugged) monitor must not
        // open past the current screen's edge; drop it to the work area.
        if (Screens.ScreenCount == 0) return;
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null) return;
        var area = screen.WorkingArea;
        if (Width > area.Width) Width = area.Width;
        if (Height > area.Height) Height = area.Height;
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
            // No hint resurrection here: every suppress path (SelectSuggestion,
            // recent/chip/link click, Navigate) either already owns the screen
            // with a result page or starts a lookup immediately, so re-showing
            // the empty state only flashed the watermark between selection and
            // the first render. The Enter path never resurrects it either.
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
        {
            var selected = index++ == _suggestSelection;
            button.Classes.Set("selected", selected);
            if (selected)
                ScrollSuggestionIntoView(button);
        }
    }

    // Keyboard selection must stay visible: the panel caps at MaxHeight 280,
    // which sits right at eight rows — with fonts that run a few px taller the
    // last selections clip half a row (Enter still works, but the user arrows
    // blind). Highlight and scroll land in the same tick: the offset math runs
    // on live layout bounds rather than a deferred BringIntoView request.
    private void ScrollSuggestionIntoView(Control item)
    {
        if (SuggestScroll.Content is not Visual content) return;
        if (item.TranslatePoint(default, content) is not { } origin) return;
        var viewport = SuggestScroll.Viewport.Height;
        if (viewport <= 0) return; // not laid out yet; the next arrow press corrects
        var top = origin.Y;
        var bottom = top + item.Bounds.Height;
        var offset = SuggestScroll.Offset;
        if (top < offset.Y)
            SuggestScroll.Offset = offset.WithY(top);
        else if (bottom > offset.Y + viewport)
            SuggestScroll.Offset = offset.WithY(bottom - viewport);
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
            result = await (LookupOverride?.Invoke(query) ?? _service.LookupAsync(query));
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
        _settingsWindow = new SettingsWindow(_settings, locations: _locations);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _ = ShowSettingsDialogAsync(_settingsWindow, this);
    }

    /// <summary>
    /// The awaited half of OpenSettings, so a failed <c>ShowDialog</c> cannot
    /// strand the reopen guard: when the dialog never opens (owner already
    /// closing, platform error), <c>Closed</c> never fires and a discarded
    /// task would leave the guard naming a window that will never appear —
    /// ⌘, then only ever <c>Activate()</c>es a ghost until restart. The
    /// failure releases the guard so the next attempt opens a fresh dialog.
    /// <paramref name="owner"/> is <c>this</c> in production; tests pass null,
    /// which makes ShowDialog fail deterministically before anything shows.
    /// </summary>
    internal async Task ShowSettingsDialogAsync(SettingsWindow dialog, Window owner)
    {
        try
        {
            await dialog.ShowDialog(owner);
        }
        catch
        {
            // Only clear the guard while it still names this dialog: a normal
            // close already cleared it (and may name a newer one).
            if (ReferenceEquals(_settingsWindow, dialog))
                _settingsWindow = null;
        }
    }

    private void OnActualThemeVariantChanged(object? sender, EventArgs e)
    {
        if (ResultsPanel.IsVisible)
            _rebuildResults?.Invoke();
    }

    private void OnTranslationsChanged(object? sender, PropertyChangedEventArgs e)
    {
        // SetLanguage raises PropertyChanged for every property in one burst
        // (XAML bindings refresh off each raise; do not disturb that). The
        // resolved CurrentLanguage is part of the burst, is assigned before
        // any raise fires, and is the once-per-switch signal: reacting to
        // every raise used to rebuild the result page 98 times per switch.
        if (e.PropertyName != nameof(Translations.CurrentLanguage))
            return;
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
        // Navigation is a page switch, not a query: invalidate any lookup
        // still in flight so its completion is dropped by the staleness
        // check in RunSearch instead of clobbering the navigated page,
        // truncating the forward history the step just restored, or leaving
        // the search box showing the navigated word over a different page.
        // Same contract ShowEmptyState already follows.
        _searchGeneration++;
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

    // Installed means any layout the store can play: the current single-file
    // database or the legacy loose uk/us directories from older versions.
    private bool AudioPackInstalled() =>
        File.Exists(_locations.AudioPackDatabasePath) ||
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
        var destination = Path.Combine(_downloadDirectory, ReleaseAssets.DictionaryAsset);
        try
        {
            var zipPath = await DownloadAndVerifyAsync(ReleaseAssets.DictionaryAsset, destination,
                new Progress<DownloadProgress>(UpdateDictionaryProgress),
                text => DictionaryDownloadStatus.Text = text,
                Translations.Instance.DownloadFailedFormat, cancellation);
            DictionaryDownloadStatus.Text = Translations.Instance.Extracting;
            await Task.Run(() => ExtractZip(zipPath, _locations.DataDirectory, cancellation: cancellation),
                cancellation);
            // The install lands first, the zip delete comes last as pure
            // cleanup: a transient lock on the just-written file (Windows
            // antivirus, indexer) must not surface as "extraction failed"
            // with a working dictionary left uninstalled (B-009). The delete
            // is best-effort — a kept zip is harmless, the reuse path
            // re-verifies it and the known-good bytes pass.
            FinishDictionarySetup();
            try { File.Delete(zipPath); }
            catch { /* the zip stays behind for the reuse path */ }
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
            if (ex is InvalidDataException)
            {
                // Damaged archive bytes: reusing the kept zip can never
                // succeed — same bytes, same decoder, same failure — so purge
                // and let the retry download again. Every other extraction
                // failure keeps the zip: the bytes already passed the
                // checksum, and re-downloading cannot fix them. The purge is
                // best-effort (same shape as the reuse-path purge): File.Delete
                // can hit a transient Windows lock (antivirus, indexer), and
                // one thrown out of this async void would kill the process —
                // degrade to the plain extraction-failure text instead, so the
                // real error surfaces and the purge must not claim a deletion
                // that did not happen.
                try
                {
                    PurgeDownloadArtifacts(destination);
                    _verifiedZips.Remove(destination);
                    DictionaryDownloadStatus.Text = Translations.Instance.ExtractCorruptPurged;
                }
                catch
                {
                    DictionaryDownloadStatus.Text = string.Format(Translations.Instance.ExtractFailedFormat, ex.Message);
                }
            }
            else
                DictionaryDownloadStatus.Text = string.Format(Translations.Instance.ExtractFailedFormat, ex.Message);
        }
        finally
        {
            _dictionaryDownloadCts.Dispose();
            _dictionaryDownloadCts = null;
            // Every terminal state clears the bar (B-009): cancel and
            // failure used to leave it frozen at the last percentage; on
            // success the panel is already gone and this only resets the
            // flag. Same outcome as the audio pack flow's per-branch hides.
            DictionaryDownloadBar.IsVisible = false;
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
    /// failure into a self-healing retry. A complete zip left by an earlier
    /// attempt (extraction failed, app closed mid-extract) is reused instead
    /// of re-downloaded — see <see cref="TryReuseDownloadedZipAsync"/>.
    /// </summary>
    private async Task<string> DownloadAndVerifyAsync(string assetName, string destinationFile,
        IProgress<DownloadProgress> progress, Action<string> setStatus, string downloadFailedFormat,
        CancellationToken cancellation)
    {
        if (await TryReuseDownloadedZipAsync(assetName, destinationFile, setStatus, cancellation))
            return destinationFile;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var filePath = (await _downloader.DownloadAsync(assetName, destinationFile, progress, cancellation)).FilePath;
                setStatus(Translations.Instance.Verifying);
                var expected = await _downloader.FetchChecksumAsync(assetName, cancellation);
                if (expected is not null)
                    await Task.Run(() => AssetDownloadService.VerifyChecksum(filePath, expected), cancellation);
                _verifiedZips.Add(filePath);
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

    /// <summary>
    /// True when a complete zip already sits at the destination and is good to
    /// extract. Extraction failures leave the downloaded zip in place (it is
    /// deleted only after extraction succeeds), and re-downloading cannot fix
    /// an extraction problem — the bytes already passed the checksum — so a
    /// retry must pick up from the file. A zip verified earlier this session
    /// is reused as-is (only this class writes the path, so it cannot change
    /// underneath the entry); one left by a previous session is re-verified
    /// against the published checksum, which also purges it when the remote
    /// asset has been replaced. With no checksum published (or offline) the
    /// reuse stands on the extraction loop's per-entry CRC instead.
    /// </summary>
    private async Task<bool> TryReuseDownloadedZipAsync(string assetName, string destinationFile,
        Action<string> setStatus, CancellationToken cancellation)
    {
        if (!File.Exists(destinationFile)) return false;
        if (_verifiedZips.Contains(destinationFile)) return true;

        setStatus(Translations.Instance.Verifying);
        var expected = await _downloader.FetchChecksumAsync(assetName, cancellation);
        if (expected is null)
        {
            _verifiedZips.Add(destinationFile);
            return true;
        }
        try
        {
            await Task.Run(() => AssetDownloadService.VerifyChecksum(destinationFile, expected), cancellation);
            _verifiedZips.Add(destinationFile);
            return true;
        }
        catch (ChecksumMismatchException)
        {
            // Stale or damaged: purge so the fresh download starts from zero,
            // mirroring the loop's recovery.
            PurgeDownloadArtifacts(destinationFile);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Unreadable (locked, half-written): purge what we can and let the
            // download attempt run; if the purge itself fails, that attempt
            // surfaces the real error.
            try { PurgeDownloadArtifacts(destinationFile); }
            catch { /* fall through to the download */ }
            return false;
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

    private async void OnPickDictionaryClick(object? sender, RoutedEventArgs e) =>
        await PickDictionaryAsync();

    /// <summary>
    /// The picker await lives in its own try: OpenFilePickerAsync fails on
    /// platforms without a working portal backend (Linux) or on platform
    /// errors, and this async void handler must never let one escape. The
    /// import itself keeps its own stage-specific handling below.
    /// </summary>
    internal async Task PickDictionaryAsync()
    {
        var options = new FilePickerOpenOptions
        {
            Title = Translations.Instance.PickerTitle,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(Translations.Instance.FileTypeDictionary)
                { Patterns = ["*.zip", "*.db"] }],
        };
        IReadOnlyList<IStorageFile> files;
        try
        {
            files = await (OpenFilePickerOverride?.Invoke(options)
                ?? StorageProvider.OpenFilePickerAsync(options));
        }
        catch (Exception ex)
        {
            DictionaryDownloadStatus.Text =
                string.Format(Translations.Instance.PickerFailedFormat, ex.Message);
            return;
        }
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
        _ = WarmupQuietlyAsync(_service);

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
        var destination = Path.Combine(_downloadDirectory, ReleaseAssets.AudioPackAsset);
        try
        {
            var zipPath = await DownloadAndVerifyAsync(ReleaseAssets.AudioPackAsset, destination,
                new Progress<DownloadProgress>(UpdateAudioPackProgress),
                text => AudioPackStatus.Text = text,
                Translations.Instance.AudioPackFailedFormat, cancellation);
            AudioPackStatus.Text = Translations.Instance.Converting;
            AudioPackBar.IsIndeterminate = true;
            IProgress<(int Done, int Total)> convertProgress =
                new Progress<(int Done, int Total)>(p =>
                    UpdateAudioPackFileProgress(p, Translations.Instance.ConvertingFilesFormat));
            await Task.Run(
                () => AudioPackConverter.ConvertZipToDatabase(zipPath, _locations.AudioPackDatabasePath,
                    (done, total) => convertProgress.Report((done, total)), cancellation),
                cancellation);
            // Same shape as the dictionary flow (B-009): the completion
            // action lands first, the zip delete comes last as pure cleanup —
            // a transient lock on the just-written file (Windows antivirus,
            // indexer) must not surface as "extraction failed" with Retry as
            // the only way out (a retry AudioPackInstalled() would
            // short-circuit into a no-op, freezing this panel until restart).
            AudioPackPanel.IsVisible = false;
            try { File.Delete(zipPath); }
            catch { /* the zip stays behind for the reuse path */ }
        }
        catch (OperationCanceledException)
        {
            AudioPackStatus.Text = Translations.Instance.AudioPackCancelled;
            AudioPackActionButton.Content = Translations.Instance.AudioPackDownloadButton;
            // The cancel click disabled the button ("取消中…"); the next
            // attempt starts from this same button, so it must come back up.
            AudioPackActionButton.IsEnabled = true;
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
            if (ex is InvalidDataException)
            {
                // Same classification as the dictionary flow: damaged archive
                // bytes purge (a retry from the same file can never work),
                // everything else keeps the zip for the checksum-passed reuse.
                // The purge is best-effort there for the same reason — a
                // transient lock must not escape this async void; degrade to
                // the plain extraction-failure text with the real error.
                try
                {
                    PurgeDownloadArtifacts(destination);
                    _verifiedZips.Remove(destination);
                    AudioPackStatus.Text = Translations.Instance.ExtractCorruptPurged;
                }
                catch
                {
                    AudioPackStatus.Text = string.Format(Translations.Instance.AudioPackImportFailedFormat, ex.Message);
                }
            }
            else
                AudioPackStatus.Text = string.Format(Translations.Instance.AudioPackImportFailedFormat, ex.Message);
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

    // Per-entry extraction progress, shared by the audio pack download and
    // the local import: the pack is ~10⁵ small files, so the bar counts
    // entries. The worker throttles reports (every 256 entries); this only
    // renders them.
    private void UpdateAudioPackFileProgress((int Done, int Total) progress, string format)
    {
        AudioPackBar.IsIndeterminate = false;
        AudioPackBar.Value = progress.Total == 0 ? 0 : 100.0 * progress.Done / progress.Total;
        AudioPackStatus.Text = string.Format(format, progress.Done, progress.Total);
    }

    private void UpdateAudioPackProgress(DownloadProgress progress)
    {
        if (progress.TotalBytes is { } total && total > 0)
        {
            AudioPackBar.IsIndeterminate = false;
            AudioPackBar.Value = 100.0 * progress.ReceivedBytes / total;
            AudioPackStatus.Text = progress.ResumedFromBytes > 0
                ? string.Format(Translations.Instance.DownloadResumeFormat,
                    progress.ReceivedBytes / 1048576.0, total / 1048576.0)
                : string.Format(Translations.Instance.DownloadingAudioPackFormat,
                    progress.ReceivedBytes / 1048576.0, total / 1048576.0);
        }
        else
        {
            AudioPackBar.IsIndeterminate = true;
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

    private async void OnPickAudioPackClick(object? sender, RoutedEventArgs e) =>
        await PickAudioPackAsync();

    /// <summary>Same picker guard as <see cref="PickDictionaryAsync"/>.</summary>
    internal async Task PickAudioPackAsync()
    {
        var options = new FilePickerOpenOptions
        {
            Title = Translations.Instance.PickerTitleAudioPack,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(Translations.Instance.FileTypeAudioPack)
                { Patterns = ["*.db", "*.zip"] }],
        };
        IReadOnlyList<IStorageFile> files;
        try
        {
            files = await (OpenFilePickerOverride?.Invoke(options)
                ?? StorageProvider.OpenFilePickerAsync(options));
        }
        catch (Exception ex)
        {
            AudioPackStatus.Text =
                string.Format(Translations.Instance.PickerFailedFormat, ex.Message);
            return;
        }
        if (files.Count == 0) return;
        var path = files[0].TryGetLocalPath();
        if (path is null) return;

        AudioPackStatus.Text = Translations.Instance.Importing;
        AudioPackActionButton.IsEnabled = false;
        AudioPackBar.IsVisible = true;
        AudioPackBar.IsIndeterminate = false;
        IProgress<(int Done, int Total)> progress =
            new Progress<(int Done, int Total)>(p =>
                UpdateAudioPackFileProgress(p, Translations.Instance.ConvertingFilesFormat));
        try
        {
            await Task.Run(() => ImportAudioPack(path, _locations.AudioPackDatabasePath,
                (done, total) => progress.Report((done, total))));
            AudioPackPanel.IsVisible = false;
        }
        catch (Exception ex)
        {
            AudioPackStatus.Text = string.Format(Translations.Instance.ImportFailedFormat, ex.Message);
            AudioPackBar.IsVisible = false;
            AudioPackActionButton.Content = Translations.Instance.Retry;
            AudioPackActionButton.IsEnabled = true;
            PickAudioPackButton.IsVisible = true;
            AudioPackDownloadPageButton.IsVisible = true;
        }
    }

    private void OnOpenDownloadPageClick(object? sender, RoutedEventArgs e) =>
        _ = TopLevel.GetTopLevel(this)?.Launcher.LaunchUriAsync(new Uri(ReleaseAssets.DataReleasePageUrl));

    /// <summary>
    /// Installs a manually chosen pronunciation pack. A .db file is verified
    /// (integrity_check + a non-empty audio table) and copied into the data
    /// directory — the user's original stays where it is. A .zip goes through
    /// the same conversion the download flow uses (pack-shape gate, per-entry
    /// CRC, atomic landing). Anything else is rejected with a plain message.
    /// </summary>
    internal static void ImportAudioPack(string packPath, string databasePath,
        Action<int, int>? progress = null, CancellationToken cancellation = default)
    {
        if (packPath.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
        {
            AudioPackStore.ValidateDatabase(packPath);
            var staging = databasePath + ".importing";
            try
            {
                File.Copy(packPath, staging, overwrite: true);
                if (File.Exists(databasePath)) File.Delete(databasePath);
                File.Move(staging, databasePath);
            }
            finally
            {
                if (File.Exists(staging)) File.Delete(staging);
            }
            return;
        }
        if (packPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            AudioPackConverter.ConvertZipToDatabase(packPath, databasePath, progress, cancellation);
            return;
        }
        throw new InvalidOperationException(Translations.Instance.ImportUnsupportedFormat);
    }

    /// <summary>
    /// Zip entries cannot escape the destination (zip-slip). Extraction is
    /// all-or-nothing: the archive unpacks into a staging directory inside
    /// the destination and only then moves into place — a half-extracted
    /// pack must never look installed (AudioPackInstalled checks for uk/).
    /// Every platform extracts entry by entry, which is what per-entry
    /// progress reporting rides on: <paramref name="progress"/> receives
    /// (entries done, entries total), reported every ProgressStride entries.
    /// <paramref name="cancellation"/> is checked between entries (and before
    /// the move), so cancelling mid-extract leaves the destination untouched.
    /// </summary>
    internal static void ExtractZip(string zipPath, string destinationDirectory,
        Action<int, int>? progress = null, CancellationToken cancellation = default)
    {
        Directory.CreateDirectory(destinationDirectory);
        var root = Path.GetFullPath(destinationDirectory);
        using var archive = System.IO.Compression.ZipFile.OpenRead(zipPath);

        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.Length == 0) continue;
            if (EntryEscapesDestination(entry.FullName))
                throw new InvalidOperationException(
                    string.Format(Translations.Instance.ZipSlipFormat, entry.FullName));
        }
        var total = archive.Entries.Count(entry => entry.FullName.Length > 0);

        // Sweep staging directories a crashed run may have left behind.
        // Deletion rides the \\?\ prefix like extraction: staging from older
        // builds can hold reserved-device-name entries (us/con.mp3) that a
        // plain Win32 delete misses on Windows 10 — current extraction maps
        // such names (us/_con.mp3), but the sweep must survive their legacy.
        const string stagingPrefix = ".stupiddict-extracting-";
        foreach (var stale in Directory.EnumerateDirectories(root, stagingPrefix + "*"))
            Directory.Delete(ToExtendedPath(stale), recursive: true);

        var staging = Path.Combine(root, stagingPrefix + Guid.NewGuid().ToString("N"));
        try
        {
            const int progressStride = 256;
            var done = 0;
            progress?.Invoke(0, total);
            foreach (var entry in archive.Entries)
            {
                cancellation.ThrowIfCancellationRequested();
                if (entry.FullName.Length == 0) continue;
                ExtractEntry(entry, staging);
                done++;
                if (done == total || done % progressStride == 0)
                    progress?.Invoke(done, total);
            }

            // Materialize before moving: moving entries out of staging while
            // lazily enumerating it races the enumerator (the pack has two).
            cancellation.ThrowIfCancellationRequested();
            foreach (var entry in Directory.EnumerateFileSystemEntries(staging).ToArray())
                MoveIntoPlace(entry, Path.Combine(root, Path.GetFileName(entry)));
        }
        finally
        {
            if (Directory.Exists(staging))
                Directory.Delete(ToExtendedPath(staging), recursive: true);
        }
    }

    /// <summary>See Assets/ZipSafety for the gate and its rationale.</summary>
    internal static bool EntryEscapesDestination(string entryName) =>
        Assets.ZipSafety.EntryEscapesDestination(entryName);

    // Entry-by-entry extraction on every platform. Windows writes through
    // the \\?\ prefix, which skips Win32 path normalization so a staged name
    // stays byte-identical to what JoinEntryPath joined (reserved DOS device
    // names are mapped away before materializing; other literal oddities a
    // hand-made zip may carry — trailing dots/spaces, which normal paths
    // silently rewrite — remain exactly as written and reachable, because
    // every delete in this file rides the same prefix). On Windows 11 the
    // prefix behaves identically. Unix needs no prefix (nothing normalizes
    // behind our back there) but shares the loop so per-entry progress works.
    private static void ExtractEntry(System.IO.Compression.ZipArchiveEntry entry, string stagingDirectory)
    {
        var target = JoinEntryPath(stagingDirectory, entry.FullName);
        if (entry.FullName.EndsWith("/"))
        {
            Directory.CreateDirectory(ToExtendedPath(target));
            return;
        }
        Directory.CreateDirectory(ToExtendedPath(Path.GetDirectoryName(target)!));
        using var source = entry.Open();
        using var destination = File.Create(ToExtendedPath(target));

        // ZipFile does not verify entry CRCs while streaming — verified on
        // .NET 10, a corrupted-but-decodable entry (a flipped byte inside a
        // stored entry) reads back silently — so the CRC is computed over the
        // decompressed bytes as they pass and compared with the central
        // directory's published value. This is the per-entry integrity
        // backstop the checksum-less reuse path stands on (B-008), and the
        // signal the download flow purges a damaged zip on: an
        // InvalidDataException here means the archive bytes are bad, and
        // reusing the same file can never extract differently.
        var buffer = new byte[1 << 16];
        var crc = Crc32.InitialState;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            destination.Write(buffer, 0, read);
            crc = Crc32.Update(crc, buffer.AsSpan(0, read));
        }
        if (Crc32.Value(crc) != entry.Crc32)
            throw new InvalidDataException(string.Format(
                Translations.Instance.ZipCrcMismatchFormat, entry.FullName));
    }

    // The slip gate has rejected rooted names and ".." segments, so joining
    // the remaining literal segments can only land inside staging. String
    // joining is deliberate: Path.Combine + GetFullPath would re-normalize,
    // and the whole point of the \\?\ prefix below is that nothing normalized
    // touches the name. '.' segments are dropped (\\?\ does not normalize
    // them away); '\' counts as a separator (a literal backslash cannot be
    // part of a Windows filename anyway).
    // Reserved DOS device names are mapped before materializing (con.mp3 →
    // _con.mp3): "con" is a real headword, and a reserved name on disk is
    // unreachable through ordinary Win32 paths on pre-Win11 Windows. The
    // pack player maps on lookup, so the pair stays consistent.
    private static string JoinEntryPath(string stagingDirectory, string entryName)
    {
        var segments = entryName.Split('/', '\\')
            .Where(segment => segment.Length > 0 && segment != ".")
            .Select(Assets.ReservedDeviceNames.MapSegment);
        return stagingDirectory + Path.DirectorySeparatorChar
            + string.Join(Path.DirectorySeparatorChar, segments);
    }

    // Extended-length prefix, Windows only (Unix paths must stay untouched).
    // Requires an absolute backslash path — the staging directory is built
    // from Path.GetFullPath output. Internal so tests can exercise the
    // reserved-name edge cases the same way production would have to: a raw
    // reserved-name file (us/con.mp3, as older builds left on disk) exists
    // only through this prefix — an ordinary Win32 path redirects it to the
    // CON device, and reading CON blocks on the console forever.
    internal static string ToExtendedPath(string path)

    {
        if (!OperatingSystem.IsWindows()) return path;
        if (path.StartsWith(@"\\?\")) return path;
        if (path.StartsWith(@"\\")) // UNC keeps working under the prefix via \\?\UNC\
            return @"\\?\UNC\" + path[2..];
        return @"\\?\" + path;
    }

    // Same-volume renames, so moving is cheap and the destination never holds
    // half-written data. Existing entries are replaced wholesale, matching the
    // overwriteFiles behaviour this replaced. Deletion goes through the \\?\
    // prefix: the target directory being replaced (e.g. audio/us on Windows 10)
    // can hold reserved-device-name entries that a plain Win32 delete misses.
    private static void MoveIntoPlace(string source, string target)
    {
        if (File.Exists(target))
            File.Delete(ToExtendedPath(target));
        else if (Directory.Exists(target))
            Directory.Delete(ToExtendedPath(target), recursive: true);
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
        ResultRendered?.Invoke();
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
                ResultsPanel.Children.Add(BuildWordLinksLine(line.Pos, line.Words));
        }

        if (result.Antonyms.Count > 0)
        {
            ResultsPanel.Children.Add(Text(Translations.Instance.Antonyms, fontSize: 12, brushKey: Palette.TextMuted, margin: new Thickness(2, 14, 0, 5)));
            foreach (var line in result.Antonyms)
                ResultsPanel.Children.Add(BuildWordLinksLine(line.Pos, line.Words));
        }

        if (result.RelatedWords.Count > 0)
        {
            ResultsPanel.Children.Add(Text(Translations.Instance.RelatedWords, fontSize: 12, brushKey: Palette.TextMuted, margin: new Thickness(2, 14, 0, 5)));
            ResultsPanel.Children.Add(BuildRelatedWordsLine(result.RelatedWords));
        }
    }

    private SelectableTextBlock ThesaurusText(string value, Thickness? margin = null) => new()
    {
        Text = value,
        FontSize = 15,
        LineHeight = 22,
        TextWrapping = TextWrapping.Wrap,
        Foreground = Palette.Get(Palette.TextSecondary, ActualThemeVariant),
        Margin = margin ?? new Thickness(0),
    };

    // Same WrapPanel pattern as the related-words line: a text run sandwiched
    // directly between two InlineUIContainer links keeps its width but has its
    // glyphs drawn about a line too low (the commas stray out of the text flow),
    // and a line break landing on a link clips the word instead of wrapping.
    // Entries are atomic, so a word never separates from its comma on a wrap.
    private WrapPanel BuildWordLinksLine(string pos, IReadOnlyList<string> words)
    {
        var panel = new WrapPanel { Margin = new Thickness(2, 0, 0, 0), Classes = { "word-links" } };
        for (var i = 0; i < words.Count; i++)
        {
            var entry = new StackPanel { Orientation = Orientation.Horizontal };
            if (i == 0 && pos.Length > 0)
                entry.Children.Add(ThesaurusText(pos + " "));
            entry.Children.Add(CreateLinkSurface(words[i], 15, lineHeight: 22));
            if (i < words.Count - 1)
                entry.Children.Add(ThesaurusText(",", new Thickness(0, 0, 4, 0)));
            panel.Children.Add(entry);
        }
        return panel;
    }

    // Per-entry blocks in a WrapPanel instead of one wrapping text line: when a
    // line break lands on an InlineUIContainer link, Avalonia 11.3 overflows it
    // past the viewport instead of wrapping (the word clips away while its gloss
    // renders on). Whole entries are atomic so breaks only fall between them.
    // Gaps are margins rather than space runs — a space next to the CJK gloss
    // shapes far wider than a Latin space.
    private WrapPanel BuildRelatedWordsLine(IReadOnlyList<RelatedWord> relatedWords)
    {
        var panel = new WrapPanel { Classes = { "related-words" } };
        foreach (var related in relatedWords)
        {
            var entry = new StackPanel { Orientation = Orientation.Horizontal };
            var surface = CreateLinkSurface(related.Word, 15, lineHeight: 22);
            entry.Children.Add(surface);
            if (related.Gloss.Length > 0)
            {
                surface.Margin = new Thickness(0, 0, 4, 0);
                entry.Children.Add(ThesaurusText(related.Gloss));
            }
            entry.Children.Add(ThesaurusText(";", new Thickness(0, 0, 5, 0)));
            panel.Children.Add(entry);
        }
        return panel;
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
        ResultRendered?.Invoke();
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

    /// <summary>
    /// The Border is the click surface: hit testing on an inline TextBlock
    /// only covers its currently shaped glyphs (and desyncs inside
    /// InlineUIContainer), so clicks in glyph gaps used to fall through.
    /// </summary>
    private Border CreateLinkSurface(string word, double fontSize, double? lineHeight = null)
    {
        var link = new TextBlock { Text = word, FontSize = fontSize, Classes = { "wordlink" } };
        if (lineHeight is { } height) link.LineHeight = height;
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
        return surface;
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
