using Avalonia.Controls;
using Avalonia.Interactivity;
using StupidDict.App.Localization;
using System.Diagnostics;

namespace StupidDict.App.Settings;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly UpdateChecker? _updateChecker;

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
        ThemeComboBox.SelectedIndex = (int)settings.Theme;
        ThemeComboBox.SelectionChanged += OnThemeSelectionChanged;
        LanguageComboBox.SelectedIndex = (int)settings.Language;
        LanguageComboBox.SelectionChanged += OnLanguageSelectionChanged;
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
