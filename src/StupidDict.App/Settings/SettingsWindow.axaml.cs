using Avalonia.Controls;
using Avalonia.Interactivity;
using System.Diagnostics;

namespace StupidDict.App.Settings;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly UpdateChecker? _updateChecker;

    public SettingsWindow(AppSettings settings, UpdateChecker? updateChecker = null)
    {
        InitializeComponent();
        _settings = settings;
        _updateChecker = updateChecker;
        ThemeComboBox.SelectedIndex = (int)settings.Theme;
        ThemeComboBox.SelectionChanged += OnThemeSelectionChanged;
        VersionText.Text = $"版本 {UpdateChecker.CurrentVersion.TrimStart('v')}";
    }

    private void OnThemeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ThemeComboBox.SelectedIndex is { } index && index >= 0)
            _settings.Theme = (AppTheme)index;
    }

    private async void OnCheckUpdateClick(object? sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        OpenReleaseButton.IsVisible = false;
        UpdateStatusText.Text = "正在检查更新…";
        var checker = _updateChecker ?? new UpdateChecker();
        var result = await checker.CheckAsync();
        UpdateStatusText.Text = result.Outcome switch
        {
            UpdateCheckOutcome.UpToDate => $"已是最新版本（{checker.Version}）",
            UpdateCheckOutcome.UpdateAvailable => $"发现新版本 {result.LatestVersion}，当前 {checker.Version}",
            _ => $"检查失败：{result.Error}",
        };
        OpenReleaseButton.IsVisible = result.Outcome == UpdateCheckOutcome.UpdateAvailable;
        CheckUpdateButton.IsEnabled = true;
    }

    private void OnOpenReleaseClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(UpdateChecker.ReleasesPageUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = $"无法打开浏览器：{ex.Message}";
        }
    }
}
