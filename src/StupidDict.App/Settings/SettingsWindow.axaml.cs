using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using StupidDict.App.Assets;
using StupidDict.App.Localization;
using StupidDict.App.Translation;
using StupidDict.App.Update;
using System.ComponentModel;
using System.Diagnostics;

namespace StupidDict.App.Settings;

public partial class SettingsWindow : Window
{
    /// <summary>翻译页签位置：通用之后、快捷键之前（MainWindow 错误卡直达用）。</summary>
    internal const int TranslationTabIndex = 1;

    /// <summary>关于页签位置：托盘菜单「检查更新」直达用（更新检查的结果只在那里渲染）。</summary>
    internal const int AboutTabIndex = 4;

    private readonly AppSettings _settings;
    private readonly UpdateChecker? _updateChecker;
    // 自更新服务：手动检查与下载/重启的共享状态机（App 创建，MainWindow 传下来）。
    // 测试注入 fake checker/downloader；null 时退回旧的「只检查」行为。
    private readonly UpdateService? _updateService;
    private readonly Action? _showUpdatePrompt;
    private readonly AppLocations _locations;
    private readonly Action<string> _openDataDirectory;
    private readonly Func<ITranslationService>? _translationServiceFactory;
    private readonly Action<bool> _applyLaunchAtLogin;
    private ITranslationService? _translationService;

    // 选项实例一次创建、跨语言复用，顺序与枚举下标一一对应；ItemsSource 全程不换
    // （重建会异步清空选区并把旧选中项经双向绑定推回），切语言只更新 Label
    private readonly OptionItem[] _themeOptions;
    private readonly OptionItem[] _languageOptions;

    /// <summary>带修饰键的键帽随平台：macOS ⌘，其余 Ctrl（与 OnPreviewKeyDown 的双认一致）。</summary>
    public static string ModKeycap => OperatingSystem.IsMacOS() ? "⌘" : "Ctrl";
    public static string BackForwardKeycap => $"{ModKeycap} [ / {ModKeycap} ]";
    public static string FocusSearchKeycap => $"{ModKeycap} K";
    public static string SettingsKeycap => $"{ModKeycap} ,";

    /// <summary>About 页展示的版本，与更新检查同源（csproj &lt;Version&gt;）。</summary>
    public static string AppVersion => UpdateChecker.CurrentVersion.TrimStart('v');

    // 无参构造仅为满足 XAML 运行时加载器（AVLN3001），生产与测试都走带 settings 的构造
    public SettingsWindow() : this(new AppSettings()) { }

    public SettingsWindow(AppSettings settings, UpdateChecker? updateChecker = null,
        AppLocations? locations = null, Action<string>? openDataDirectory = null,
        Func<ITranslationService>? translationServiceFactory = null,
        Action<bool>? applyLaunchAtLogin = null, int initialTabIndex = 0,
        UpdateService? updateService = null, Action? showUpdatePrompt = null)
    {
        InitializeComponent();
        // Each window owns its HWND, so the settings dialog needs its own
        // caption declaration (WindowsCaptionTheme).
        WindowsCaptionTheme.Attach(this);
        _settings = settings;
        _updateChecker = updateChecker;
        _updateService = updateService;
        _showUpdatePrompt = showUpdatePrompt;
        // 数据区块对注入的布局对磁盘求值；测试注入临时副本，绝不碰真实数据。
        _locations = locations ?? AppLocations.Default;
        _openDataDirectory = openDataDirectory ?? OpenDirectoryInFileManager;
        _translationServiceFactory = translationServiceFactory;
        // 开机自启注册缝：测试注入 recorder，绝不写真实注册表/用户文件。
        _applyLaunchAtLogin = applyLaunchAtLogin ?? AutoStart.Apply;
        var t = Translations.Instance;
        _themeOptions = [new(t.FollowSystem), new(t.ThemeLight), new(t.ThemeDark)];
        _languageOptions = [new(t.FollowSystem), new(t.LangChinese), new(t.LangEnglish)];
        ThemeComboBox.ItemsSource = _themeOptions;
        LanguageComboBox.ItemsSource = _languageOptions;
        ThemeComboBox.SelectedIndex = (int)settings.Theme;
        ThemeComboBox.SelectionChanged += OnThemeSelectionChanged;
        LanguageComboBox.SelectedIndex = (int)settings.Language;
        LanguageComboBox.SelectionChanged += OnLanguageSelectionChanged;
        LaunchAtLoginCheckBox.IsChecked = settings.LaunchAtLogin;
        LaunchAtLoginCheckBox.IsCheckedChanged += OnLaunchAtLoginToggle;
        // Close-to-tray is a pure setting bit: MainWindow reads it at close
        // time, so wiring the shared instance (persisted by WireSettings) is
        // the whole job. No system side effect, hence no injected seam.
        MinimizeToTrayCheckBox.IsChecked = settings.MinimizeToTrayOnClose;
        MinimizeToTrayCheckBox.IsCheckedChanged += OnMinimizeToTrayToggle;
        Translations.Instance.PropertyChanged += OnTranslationsPropertyChanged;
        DataDownloadPageButton.Tag = ReleaseAssets.DataReleasePageUrl;
        RefreshDataStatus();
        // 已下载好的更新在打开设置时就露出来（用户可能刚在对话框里下载完又跑回来看版本）。
        RefreshUpdateButtons();
        // 翻译页签是 draft：加载已保存值，点保存才写回共享 AppSettings（那里
        // 属性变更即落盘），避免每敲一个字符持久化一次密钥。
        LoadTranslationDraft();
        TranslationApiKeyBox.PasswordChar = '●';
        RefreshTranslationCacheSize();
        // 错误卡直达：打开时落在翻译页签（OnOpened 的聚焦随之切到地址框）。
        if (initialTabIndex >= 0 && initialTabIndex < SettingsTabs.ItemCount)
            SettingsTabs.SelectedIndex = initialTabIndex;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // 键盘焦点必须落进对话框内控件：Avalonia 的 Window / TabControl 默认
        // Focusable=false，打开后全局 FocusedElement 仍停留在主窗口搜索框，
        // Esc / ⌘, 会被路由回主窗口（模态下它已禁用）。照 inpaint
        // ConfirmWindow 的做法在 OnOpened 里显式聚焦（首个可聚焦控件）。
        if (SettingsTabs.SelectedIndex == TranslationTabIndex)
            TranslationApiUrlBox.Focus();
        else
            ThemeComboBox.Focus();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;
        // Esc / ⌘, 关闭对话框——⌘, 开关的关闭半边（打开半边在
        // MainWindow.OnPreviewKeyDown）。气泡阶段处理：ComboBox 弹层打开时
        // 先吃掉 Esc（关下拉并标记 handled），窗口只在没人要这个键时才关。
        var cmdCtrl = (e.KeyModifiers & (KeyModifiers.Meta | KeyModifiers.Control)) != 0;
        switch (e.Key)
        {
            case Key.Escape:
            case Key.OemComma when cmdCtrl:
                e.Handled = true;
                Close();
                break;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        // Translations 是进程级单例，解订避免关闭后的窗口被事件钉住不释放
        Translations.Instance.PropertyChanged -= OnTranslationsPropertyChanged;
    }

    /// <summary>语言切换后更新选项文案；选项实例与 ItemsSource 全程不变，选区无扰。
    /// SetLanguage 逐属性 raise 的风暴里只在 CurrentLanguage 一拍收敛执行一次
    /// （~98 遍降为 1 遍，含 RefreshDataStatus 的两次磁盘 stat）。</summary>
    private void OnTranslationsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(Translations.CurrentLanguage))
            return;
        var t = Translations.Instance;
        _themeOptions[0].Label = t.FollowSystem;
        _themeOptions[1].Label = t.ThemeLight;
        _themeOptions[2].Label = t.ThemeDark;
        _languageOptions[0].Label = t.FollowSystem;
        _languageOptions[1].Label = t.LangChinese;
        _languageOptions[2].Label = t.LangEnglish;
        // 安装状态是稳定事实而非瞬态进度，跟随语言一起重算刷新。
        RefreshDataStatus();
        // 缓存大小同属稳定事实，跟随语言刷新；TranslationStatusText 是瞬态，
        // 保持出现时的语言。
        RefreshTranslationCacheSize();
    }

    private void OnThemeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ThemeComboBox.SelectedIndex is { } index && index >= 0)
            _settings.Theme = (AppTheme)index;
    }

    private void OnLanguageSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (LanguageComboBox.SelectedIndex is { } index && index >= 0)
            _settings.Language = (AppLanguage)index;
    }

    private void OnLaunchAtLoginToggle(object? sender, RoutedEventArgs e)
    {
        var enabled = LaunchAtLoginCheckBox.IsChecked == true;
        // 先写共享设置（WireSettings 落盘），再注册系统启动项；注册失败不回滚
        // 设置——下次启动的收敛会重试。
        _settings.LaunchAtLogin = enabled;
        _applyLaunchAtLogin(enabled);
    }

    private void OnMinimizeToTrayToggle(object? sender, RoutedEventArgs e)
    {
        // An explicit settings choice answers the one-time close prompt: the
        // user already said what they want, asking on the next close would only
        // be noise (and could contradict the checkbox they just set).
        _settings.MinimizeToTrayOnClose = MinimizeToTrayCheckBox.IsChecked == true;
        _settings.CloseToTrayAsked = true;
    }

    // ---- 翻译页签：draft 加载/保存 + 测试连接 + 缓存 ----

    private void LoadTranslationDraft()
    {
        TranslationApiUrlBox.Text = _settings.TranslationApiUrl;
        TranslationApiKeyBox.Text = _settings.TranslationApiKey;
        TranslationModelBox.Text = _settings.TranslationModel;
        TranslationAutoCheckBox.IsChecked = _settings.TranslationAutoOnMiss;
    }

    private ITranslationService? TranslationService =>
        _translationService ??= _translationServiceFactory?.Invoke();

    private void OnTranslationSaveClick(object? sender, RoutedEventArgs e)
    {
        var url = TranslationApiUrlBox.Text?.Trim() ?? "";
        var model = TranslationModelBox.Text?.Trim() ?? "";
        if (url.Length == 0 || model.Length == 0)
        {
            TranslationStatusText.Text = Translations.Instance.TranslationUrlAndModelRequired;
            return;
        }
        if (!TranslationEndpoint.TryCreate(url, out var endpoint, out _))
        {
            TranslationStatusText.Text = Translations.Instance.TranslationInvalidUrl;
            return;
        }

        // 设计文档：保存时归一化——存 base（scheme://host/path），完整
        // /chat/completions 端点剥回 base，请求时再拼回。
        _settings.TranslationApiUrl = endpoint!.BaseUri.ToString();
        _settings.TranslationApiKey = TranslationApiKeyBox.Text ?? "";
        _settings.TranslationModel = model;
        _settings.TranslationAutoOnMiss = TranslationAutoCheckBox.IsChecked == true;
        TranslationService?.ResetCircuit();
        TranslationStatusText.Text = Translations.Instance.TranslationSaved;
        RefreshTranslationCacheSize();
    }

    private async void OnTranslationTestClick(object? sender, RoutedEventArgs e)
    {
        var service = TranslationService;
        if (service is null) return;

        // 用 draft 值测试，不要求先保存；测试连接发固定 "Hello"，不带用户内容。
        var config = new TranslationConfig(
            TranslationApiUrlBox.Text?.Trim() ?? "",
            TranslationApiKeyBox.Text ?? "",
            TranslationModelBox.Text?.Trim() ?? "",
            TranslationAutoCheckBox.IsChecked == true);
        TranslationTestButton.IsEnabled = false;
        TranslationStatusText.Text = Translations.Instance.TranslationTesting;
        try
        {
            var result = await service.TestAsync(config, CancellationToken.None);
            TranslationStatusText.Text = result.Success
                ? Translations.Instance.TranslationTestSucceeded
                : string.Format(Translations.Instance.TranslationTestFailedFormat, result.Failure);
        }
        catch (Exception ex)
        {
            // async void 点击回调绝不外抛；TestAsync 已把 HTTP/网络失败折进
            // 结果，这里只兜非网络异常。
            TranslationStatusText.Text =
                string.Format(Translations.Instance.TranslationTestFailedFormat, ex.Message);
        }
        finally
        {
            TranslationTestButton.IsEnabled = true;
        }
    }

    private void OnTranslationClearCacheClick(object? sender, RoutedEventArgs e)
    {
        TranslationService?.ClearCache();
        RefreshTranslationCacheSize();
        TranslationStatusText.Text = Translations.Instance.TranslationCacheCleared;
    }

    private void RefreshTranslationCacheSize()
    {
        // 缓存大小是纯展示：绝不能为了取数把服务/缓存文件创建出来——未配置
        // 的应用不碰用户数据目录（README/AGENTS 承诺）。服务没建过时读磁盘
        // 文件长度：未配置即文件不存在 → 0；配置过但本会话还没动过翻译时，
        // 文件长度就是如实的占用。
        var bytes = _translationService?.GetCacheSizeBytes()
            ?? (File.Exists(_locations.TranslationCacheDatabasePath)
                ? new FileInfo(_locations.TranslationCacheDatabasePath).Length
                : 0);
        TranslationCacheSizeText.Text =
            string.Format(Translations.Instance.TranslationCacheFormat, bytes / 1024.0 / 1024.0);
    }

    // ---- 关于页数据区块：词典/发音包安装状态 + 打开数据目录 ----

    /// <summary>对磁盘求值；与 MainWindow.AudioPackInstalled 同语义（db 或遗留散文件任一即算已安装）。</summary>
    private void RefreshDataStatus()
    {
        var t = Translations.Instance;
        DictionaryDataStatus.Text = File.Exists(_locations.DictionaryDatabasePath)
            ? t.AssetInstalled : t.AssetNotInstalled;
        AudioPackDataStatus.Text = File.Exists(_locations.AudioPackDatabasePath) ||
            Directory.Exists(Path.Combine(_locations.AudioDirectory, "uk"))
            ? t.AssetInstalled : t.AssetNotInstalled;
    }

    private void OnOpenDataDirectoryClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            _openDataDirectory(_locations.DataDirectory);
        }
        catch (Exception ex)
        {
            DataActionStatus.Text = string.Format(Translations.Instance.OpenDataDirectoryFailed, ex.Message);
        }
    }

    /// <summary>打开目录与 OnOpenLink 打开浏览器同机制：系统 shell 分派（macOS open、Linux xdg-open、Windows ShellExecute）。</summary>
    private static void OpenDirectoryInFileManager(string path) =>
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    private async void OnCheckUpdateClick(object? sender, RoutedEventArgs e) => await RunUpdateCheckAsync();

    /// <summary>Starts a check from outside the dialog (tray menu opens the About
    /// tab, then calls this): the status line, the button state and the release
    /// link stay owned by the one handler below.</summary>
    internal void BeginUpdateCheck() => _ = RunUpdateCheckAsync();

    private async Task RunUpdateCheckAsync()
    {
        CheckUpdateButton.IsEnabled = false;
        OpenReleaseButton.IsVisible = false;
        DownloadUpdateButton.IsVisible = false;
        UpdateStatusText.Text = Translations.Instance.CheckingUpdate;
        UpdateCheckResult result;
        try
        {
            // 手动检查走共享服务（结果同步给自更新对话框），没有服务时退回
            // 纯检查器（只读的旧测试路径）。
            result = _updateService is { } service
                ? await service.CheckNowAsync()
                : await (_updateChecker ?? new UpdateChecker()).CheckAsync();
        }
        catch (Exception ex)
        {
            // CheckAsync already turns HTTP/network failures into results; this
            // only backs the unlikely non-network escape — an async void click
            // handler must never throw.
            result = new UpdateCheckResult(UpdateCheckOutcome.Failed, Error: ex.Message,
                ErrorKind: UpdateCheckErrorKind.Network);
        }
        var version = _updateService?.CurrentVersion ?? (_updateChecker ?? new UpdateChecker()).Version;
        UpdateStatusText.Text = result.Outcome switch
        {
            UpdateCheckOutcome.UpToDate =>
                string.Format(Translations.Instance.UpToDateStatus, version),
            UpdateCheckOutcome.UpdateAvailable =>
                string.Format(Translations.Instance.UpdateAvailableStatus, result.LatestVersion, version),
            _ => string.Format(Translations.Instance.UpdateCheckFailed, result.Error),
        };
        OpenReleaseButton.IsVisible = result.Outcome == UpdateCheckOutcome.UpdateAvailable;
        CheckUpdateButton.IsEnabled = true;
        RefreshUpdateButtons();
    }

    /// <summary>
    /// 下载/重启入口的显隐：只在共享服务里确有新版本或已就绪时出现。
    /// 下载与重启的进度、状态机都在自更新对话框里（设置是模态的，点下去就关掉
    /// 自己把话筒交给它），这里不重复实现一套进度 UI。
    /// </summary>
    private void RefreshUpdateButtons()
    {
        var state = _updateService?.State;
        DownloadUpdateButton.IsVisible = state is UpdateState.Available;
        RestartUpdateButton.IsVisible = state is UpdateState.ReadyToRestart;
        // 共享服务里已经有结论时，打开设置也要看得见（比如上次检查发现的版本还没下载）。
        if (state is UpdateState.Available)
        {
            UpdateStatusText.Text = string.Format(Translations.Instance.UpdateAvailableStatus,
                _updateService?.LastResult?.LatestVersion, _updateService?.CurrentVersion);
        }
        else if (state is UpdateState.ReadyToRestart)
        {
            UpdateStatusText.Text = string.Format(Translations.Instance.UpdateStatusReadyFormat,
                _updateService?.LastResult?.LatestVersion);
        }
    }

    private void OnDownloadUpdateClick(object? sender, RoutedEventArgs e) => HandOffToUpdatePrompt();

    private void OnRestartUpdateClick(object? sender, RoutedEventArgs e) => HandOffToUpdatePrompt();

    /// <summary>
    /// 模态对话框会挡住主窗口上的自更新对话框：先关自己、再（Post 到关闭处理之后）
    /// 把话筒交给它——同步 Invoke 时主窗口还处在模态禁用状态，弹窗会以禁用主窗为 owner。
    /// </summary>
    private void HandOffToUpdatePrompt()
    {
        var show = _showUpdatePrompt;
        Close();
        if (show is not null)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(show);
        }
    }

    private void OnOpenLink(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string url }) return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = string.Format(Translations.Instance.BrowserOpenFailed, ex.Message);
        }
    }
}
