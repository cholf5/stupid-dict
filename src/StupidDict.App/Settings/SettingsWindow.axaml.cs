using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using StupidDict.App.Localization;
using System.ComponentModel;
using System.Diagnostics;

namespace StupidDict.App.Settings;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly UpdateChecker? _updateChecker;

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

    public SettingsWindow(AppSettings settings, UpdateChecker? updateChecker = null)
    {
        InitializeComponent();
        _settings = settings;
        _updateChecker = updateChecker;
        var t = Translations.Instance;
        _themeOptions = [new(t.FollowSystem), new(t.ThemeLight), new(t.ThemeDark)];
        _languageOptions = [new(t.FollowSystem), new(t.LangChinese), new(t.LangEnglish)];
        ThemeComboBox.ItemsSource = _themeOptions;
        LanguageComboBox.ItemsSource = _languageOptions;
        ThemeComboBox.SelectedIndex = (int)settings.Theme;
        ThemeComboBox.SelectionChanged += OnThemeSelectionChanged;
        LanguageComboBox.SelectedIndex = (int)settings.Language;
        LanguageComboBox.SelectionChanged += OnLanguageSelectionChanged;
        Translations.Instance.PropertyChanged += OnTranslationsPropertyChanged;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // 键盘焦点必须落进对话框内控件：Avalonia 的 Window / TabControl 默认
        // Focusable=false，打开后全局 FocusedElement 仍停留在主窗口搜索框，
        // Esc / ⌘, 会被路由回主窗口（模态下它已禁用）。照 inpaint
        // ConfirmWindow 的做法在 OnOpened 里显式聚焦（首个可聚焦控件）。
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

    /// <summary>语言切换后更新选项文案；选项实例与 ItemsSource 全程不变，选区无扰。</summary>
    private void OnTranslationsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        var t = Translations.Instance;
        _themeOptions[0].Label = t.FollowSystem;
        _themeOptions[1].Label = t.ThemeLight;
        _themeOptions[2].Label = t.ThemeDark;
        _languageOptions[0].Label = t.FollowSystem;
        _languageOptions[1].Label = t.LangChinese;
        _languageOptions[2].Label = t.LangEnglish;
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

    private async void OnCheckUpdateClick(object? sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        OpenReleaseButton.IsVisible = false;
        UpdateStatusText.Text = Translations.Instance.CheckingUpdate;
        var checker = _updateChecker ?? new UpdateChecker();
        var result = await checker.CheckAsync();
        UpdateStatusText.Text = result.Outcome switch
        {
            UpdateCheckOutcome.UpToDate =>
                string.Format(Translations.Instance.UpToDateStatus, checker.Version),
            UpdateCheckOutcome.UpdateAvailable =>
                string.Format(Translations.Instance.UpdateAvailableStatus, result.LatestVersion, checker.Version),
            _ => string.Format(Translations.Instance.UpdateCheckFailed, result.Error),
        };
        OpenReleaseButton.IsVisible = result.Outcome == UpdateCheckOutcome.UpdateAvailable;
        CheckUpdateButton.IsEnabled = true;
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
