using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using StupidDict.App.Settings;

namespace StupidDict.App.Localization;

/// <summary>
/// Singleton dictionary of every user-visible UI string: zh is the source
/// dictionary (keys = property names), en only holds overrides, missing keys
/// fall back to Chinese. XAML binds {Binding Prop, Source={x:Static
/// loc:Translations.Instance}} (property names checked at compile time).
/// Switching languages raises PropertyChanged for every property so all live
/// bindings refresh; the rendered result pages re-render via MainWindow's
/// rebuild closure. Transient status lines (download/update progress) do not
/// retro-refresh and keep the language they appeared in.
/// </summary>
public sealed class Translations : INotifyPropertyChanged
{
    // Static fields initialize in declaration order: the dictionaries must
    // come before Instance (its constructor needs them).
    private static readonly string[] AllPropertyNames =
        typeof(Translations).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToArray();

    private static readonly Dictionary<string, string> Zh = new()
    {
        [nameof(SearchWatermark)] = "输入单词或中文，按 Enter 查询",
        [nameof(NavBackTip)] = "后退 (⌘[)",
        [nameof(NavForwardTip)] = "前进 (⌘])",
        [nameof(SettingsTip)] = "设置 (⌘,)",
        [nameof(Tagline)] = "完全离线 · 无账号 · 零配置",
        [nameof(DownloadPrompt)] = "首次使用需要词典数据（约 600 MB）。直接下载，或导入本地已有文件。",
        [nameof(StartDownload)] = "开始下载",
        [nameof(PickLocalFile)] = "选择本地文件…",
        [nameof(Cancel)] = "取消",
        [nameof(Recent)] = "最近",
        [nameof(Verifying)] = "校验中…",
        [nameof(Extracting)] = "解压中…",
        [nameof(Importing)] = "导入中…",
        [nameof(DownloadCancelled)] = "已取消下载。可以直接下载，或选择本地已有文件。",
        [nameof(DownloadFailedFormat)] = "下载失败：{0}",
        [nameof(ImportFailedFormat)] = "导入失败：{0}",
        [nameof(ImportMissingDb)] = "文件里没有 dictionary.db",
        [nameof(AllSourcesFailed)] = "所有下载源都失败了。请检查网络，或手动下载后导入。",
        [nameof(ChecksumFailed)] = "下载文件校验失败，已删除损坏文件。",
        [nameof(ZipSlipFormat)] = "压缩包内出现非法路径：{0}",
        [nameof(DownloadingDictionaryFormat)] = "正在下载词典 {0:F0} / {1:F0} MB",
        [nameof(DownloadingDictionaryUnsizedFormat)] = "正在下载词典 {0:F0} MB",
        [nameof(DownloadingAudioPack)] = "正在下载发音包（约 1 GB，一次性）",
        [nameof(DownloadingAudioPackFormat)] = "正在下载发音包 {0:F0} / {1:F0} MB",
        [nameof(DownloadingAudioPackUnsizedFormat)] = "正在下载发音包 {0:F0} MB",
        [nameof(AudioPackCancelled)] = "发音包下载已取消。未覆盖的单词会用系统语音朗读。",
        [nameof(AudioPackFailedFormat)] = "发音包下载失败：{0}",
        [nameof(AudioPackDownloadButton)] = "下载",
        [nameof(Retry)] = "重试",
        [nameof(Cancelling)] = "正在取消…",
        [nameof(NoLocalPronunciation)] = "该词没有本地发音。下载发音包可获得离线英/美真人发音。",
        [nameof(NoResultFormat)] = "没有找到 “{0}”",
        [nameof(DidYouMean)] = "你是不是要找",
        [nameof(TryShorter)] = "试试更短的拼写，或换个说法。",
        [nameof(EnglishDefinitions)] = "英英释义",
        [nameof(Synonyms)] = "近义词",
        [nameof(Antonyms)] = "反义词",
        [nameof(RelatedWords)] = "联想词",
        [nameof(RelatedEntries)] = "相关词条",
        [nameof(OtherEntries)] = "其他词条",
        [nameof(LookupErrorFormat)] = "查询 “{0}” 时出错",
        [nameof(UkPhoneticPrefix)] = "英 {0}",
        [nameof(UsPhoneticPrefix)] = "美 {0}",
        [nameof(UkTip)] = "英音",
        [nameof(UsTip)] = "美音",
        [nameof(PickerTitle)] = "选择 dictionary.zip 或 dictionary.db",
        [nameof(FileTypeDictionary)] = "词典数据",
        [nameof(SettingsTitle)] = "设置",
        [nameof(SectionGeneral)] = "通用",
        [nameof(ThemeLabel)] = "主题",
        [nameof(ThemeHint)] = "跟随系统、日间或夜间，更改立即生效",
        [nameof(FollowSystem)] = "跟随系统",
        [nameof(ThemeLight)] = "日间",
        [nameof(ThemeDark)] = "夜间",
        [nameof(LanguageLabel)] = "语言",
        [nameof(LanguageHint)] = "跟随系统、简体中文或 English，更改立即生效",
        [nameof(LangChinese)] = "简体中文",
        [nameof(LangEnglish)] = "English",
        [nameof(SectionShortcuts)] = "快捷键",
        [nameof(ShortcutLookup)] = "查你输入的词；有选中候选时查该候选",
        [nameof(ShortcutEscape)] = "关闭候选列表；再按清空输入",
        [nameof(ShortcutArrows)] = "浏览补全候选；输入为空时浏览最近搜索",
        [nameof(ShortcutBackForward)] = "后退 / 前进（查询历史）",
        [nameof(ShortcutFocusSearch)] = "聚焦搜索框",
        [nameof(ShortcutSettings)] = "打开设置",
        [nameof(SectionAbout)] = "关于",
        [nameof(AboutBlurb)] = "完全离线的英汉词典。查询不联网，联网只发生在首次下载词典与发音包的时候。",
        [nameof(AuthorLabel)] = "作者",
        [nameof(AuthorName)] = "周尔复",
        [nameof(LicenseLabel)] = "开源许可",
        [nameof(RepoLabel)] = "仓库地址",
        [nameof(CheckUpdate)] = "检查更新",
        [nameof(OpenReleasePage)] = "打开发布页",
        [nameof(CheckingUpdate)] = "正在检查更新…",
        [nameof(UpToDateStatus)] = "已是最新版本（{0}）",
        [nameof(UpdateAvailableStatus)] = "发现新版本 {0}，当前 {1}",
        [nameof(UpdateCheckFailed)] = "检查失败：{0}",
        [nameof(BrowserOpenFailed)] = "无法打开浏览器：{0}",
    };

    private static readonly Dictionary<string, string> En = new()
    {
        [nameof(SearchWatermark)] = "Type an English word or Chinese, then press Enter",
        [nameof(NavBackTip)] = "Back (⌘[)",
        [nameof(NavForwardTip)] = "Forward (⌘])",
        [nameof(SettingsTip)] = "Settings (⌘,)",
        [nameof(Tagline)] = "Fully offline · No account · Zero setup",
        [nameof(DownloadPrompt)] = "First run needs the dictionary data (about 600 MB). Download it, or import a local copy.",
        [nameof(StartDownload)] = "Download",
        [nameof(PickLocalFile)] = "Choose a local file…",
        [nameof(Cancel)] = "Cancel",
        [nameof(Recent)] = "Recent",
        [nameof(Verifying)] = "Verifying…",
        [nameof(Extracting)] = "Extracting…",
        [nameof(Importing)] = "Importing…",
        [nameof(DownloadCancelled)] = "Download cancelled. You can download again or choose a local file.",
        [nameof(DownloadFailedFormat)] = "Download failed: {0}",
        [nameof(ImportFailedFormat)] = "Import failed: {0}",
        [nameof(ImportMissingDb)] = "The file does not contain dictionary.db",
        [nameof(AllSourcesFailed)] = "Every download source failed. Check the network, or download and import manually.",
        [nameof(ChecksumFailed)] = "Checksum verification failed; the corrupted file was deleted.",
        [nameof(ZipSlipFormat)] = "Illegal path inside the archive: {0}",
        [nameof(DownloadingDictionaryFormat)] = "Downloading dictionary {0:F0} / {1:F0} MB",
        [nameof(DownloadingDictionaryUnsizedFormat)] = "Downloading dictionary {0:F0} MB",
        [nameof(DownloadingAudioPack)] = "Downloading the pronunciation pack (about 1 GB, one-time)",
        [nameof(DownloadingAudioPackFormat)] = "Downloading pronunciation pack {0:F0} / {1:F0} MB",
        [nameof(DownloadingAudioPackUnsizedFormat)] = "Downloading pronunciation pack {0:F0} MB",
        [nameof(AudioPackCancelled)] = "Pronunciation pack download cancelled. Words it does not cover fall back to the system voice.",
        [nameof(AudioPackFailedFormat)] = "Pronunciation pack download failed: {0}",
        [nameof(AudioPackDownloadButton)] = "Download",
        [nameof(Retry)] = "Retry",
        [nameof(Cancelling)] = "Cancelling…",
        [nameof(NoLocalPronunciation)] = "No local pronunciation for this word. Download the pronunciation pack for offline UK/US voices.",
        [nameof(NoResultFormat)] = "No results for “{0}”",
        [nameof(DidYouMean)] = "Did you mean",
        [nameof(TryShorter)] = "Try a shorter spelling or a different phrasing.",
        [nameof(EnglishDefinitions)] = "English definition",
        [nameof(Synonyms)] = "Synonyms",
        [nameof(Antonyms)] = "Antonyms",
        [nameof(RelatedWords)] = "Related words",
        [nameof(RelatedEntries)] = "Related entries",
        [nameof(OtherEntries)] = "Other entries",
        [nameof(LookupErrorFormat)] = "Error looking up “{0}”",
        [nameof(UkPhoneticPrefix)] = "UK {0}",
        [nameof(UsPhoneticPrefix)] = "US {0}",
        [nameof(UkTip)] = "British pronunciation",
        [nameof(UsTip)] = "American pronunciation",
        [nameof(PickerTitle)] = "Choose dictionary.zip or dictionary.db",
        [nameof(FileTypeDictionary)] = "Dictionary data",
        [nameof(SettingsTitle)] = "Settings",
        [nameof(SectionGeneral)] = "General",
        [nameof(ThemeLabel)] = "Theme",
        [nameof(ThemeHint)] = "Follow the system, light or dark; changes apply immediately",
        [nameof(FollowSystem)] = "System",
        [nameof(ThemeLight)] = "Light",
        [nameof(ThemeDark)] = "Dark",
        [nameof(LanguageLabel)] = "Language",
        [nameof(LanguageHint)] = "Follow the system, Simplified Chinese or English; changes apply immediately",
        [nameof(LangChinese)] = "Simplified Chinese",
        [nameof(SectionShortcuts)] = "Shortcuts",
        [nameof(ShortcutLookup)] = "Look up what you typed; the selected suggestion, if one is chosen",
        [nameof(ShortcutEscape)] = "Close the suggestion list; press again to clear the box",
        [nameof(ShortcutArrows)] = "Browse suggestions; recent searches when the box is empty",
        [nameof(ShortcutBackForward)] = "Back / Forward (lookup history)",
        [nameof(ShortcutFocusSearch)] = "Focus the search box",
        [nameof(ShortcutSettings)] = "Open settings",
        [nameof(SectionAbout)] = "About",
        [nameof(AboutBlurb)] = "A fully offline English-Chinese dictionary. Lookups never touch the network; the network is only used for the first dictionary and pronunciation pack download.",
        [nameof(AuthorLabel)] = "Author",
        [nameof(AuthorName)] = "Cholf",
        [nameof(LicenseLabel)] = "License",
        [nameof(RepoLabel)] = "Repository",
        [nameof(CheckUpdate)] = "Check for updates",
        [nameof(OpenReleasePage)] = "Open release page",
        [nameof(CheckingUpdate)] = "Checking for updates…",
        [nameof(UpToDateStatus)] = "Up to date ({0})",
        [nameof(UpdateAvailableStatus)] = "New version {0} available, current {1}",
        [nameof(UpdateCheckFailed)] = "Check failed: {0}",
        [nameof(BrowserOpenFailed)] = "Could not open the browser: {0}",
    };

    public static Translations Instance { get; } = new();

    private Dictionary<string, string> _strings;

    private Translations() =>
        _strings = StringsFor(Resolve(AppLanguage.System, CultureInfo.CurrentUICulture));

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The language in effect (System already resolved to a concrete language).</summary>
    public AppLanguage CurrentLanguage { get; private set; }

    public void SetLanguage(AppLanguage language)
    {
        var resolved = Resolve(language, CultureInfo.CurrentUICulture);
        _strings = StringsFor(resolved);
        CurrentLanguage = resolved;
        foreach (var name in AllPropertyNames)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>System follows the UI culture's two-letter code: zh → Simplified Chinese, everything else (en, ja, …) → English. Internal for tests.</summary>
    internal static AppLanguage Resolve(AppLanguage preference, CultureInfo uiCulture) =>
        preference switch
        {
            AppLanguage.SimplifiedChinese => AppLanguage.SimplifiedChinese,
            AppLanguage.English => AppLanguage.English,
            _ => uiCulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase)
                ? AppLanguage.SimplifiedChinese
                : AppLanguage.English,
        };

    private static Dictionary<string, string> StringsFor(AppLanguage language)
    {
        var merged = new Dictionary<string, string>(Zh);
        if (language != AppLanguage.SimplifiedChinese)
            foreach (var (key, value) in En)
                merged[key] = value;
        return merged;
    }

    private string Get([CallerMemberName] string? key = null) =>
        _strings.TryGetValue(key!, out var value) ? value : key!;

    // ---- 主窗口 ----

    /// <summary>搜索框水印与空态提示共用同一条文案。</summary>
    public string SearchWatermark => Get();
    public string NavBackTip => Get();
    public string NavForwardTip => Get();
    public string SettingsTip => Get();
    public string Tagline => Get();
    public string DownloadPrompt => Get();
    public string StartDownload => Get();
    public string PickLocalFile => Get();
    public string Cancel => Get();
    public string Recent => Get();

    // ---- 下载 / 导入状态行（瞬态：不随语言切换回溯刷新）----

    public string Verifying => Get();
    public string Extracting => Get();
    public string Importing => Get();
    public string DownloadCancelled => Get();
    public string DownloadFailedFormat => Get();
    public string ImportFailedFormat => Get();
    public string ImportMissingDb => Get();
    /// <summary>异常消息即用户可见文案（经下载状态行展示），因此进词池。</summary>
    public string AllSourcesFailed => Get();
    public string ChecksumFailed => Get();
    public string ZipSlipFormat => Get();
    /// <summary>{0} 已接收 MB，{1} 总 MB。</summary>
    public string DownloadingDictionaryFormat => Get();
    public string DownloadingDictionaryUnsizedFormat => Get();
    public string DownloadingAudioPack => Get();
    public string DownloadingAudioPackFormat => Get();
    public string DownloadingAudioPackUnsizedFormat => Get();
    public string AudioPackCancelled => Get();
    public string AudioPackFailedFormat => Get();
    public string AudioPackDownloadButton => Get();
    public string Retry => Get();
    public string Cancelling => Get();
    public string NoLocalPronunciation => Get();

    // ---- 查询结果渲染 ----

    public string NoResultFormat => Get();
    public string DidYouMean => Get();
    public string TryShorter => Get();
    public string EnglishDefinitions => Get();
    public string Synonyms => Get();
    public string Antonyms => Get();
    public string RelatedWords => Get();
    public string RelatedEntries => Get();
    public string OtherEntries => Get();
    public string LookupErrorFormat => Get();
    /// <summary>{0} 为 /…/ 包好的音标。</summary>
    public string UkPhoneticPrefix => Get();
    public string UsPhoneticPrefix => Get();
    public string UkTip => Get();
    public string UsTip => Get();
    public string PickerTitle => Get();
    public string FileTypeDictionary => Get();

    // ---- 设置窗口 ----

    public string SettingsTitle => Get();
    public string SectionGeneral => Get();
    public string ThemeLabel => Get();
    public string ThemeHint => Get();
    public string FollowSystem => Get();
    public string ThemeLight => Get();
    public string ThemeDark => Get();
    public string LanguageLabel => Get();
    public string LanguageHint => Get();
    public string LangChinese => Get();
    public string LangEnglish => Get();
    public string SectionShortcuts => Get();
    public string ShortcutLookup => Get();
    public string ShortcutEscape => Get();
    public string ShortcutArrows => Get();
    public string ShortcutBackForward => Get();
    public string ShortcutFocusSearch => Get();
    public string ShortcutSettings => Get();
    public string SectionAbout => Get();
    public string AboutBlurb => Get();
    public string AuthorLabel => Get();
    public string AuthorName => Get();
    public string LicenseLabel => Get();
    public string RepoLabel => Get();
    public string CheckUpdate => Get();
    public string OpenReleasePage => Get();

    // ---- 检查更新（瞬态结果行不经此刷新，保持出现时的语言）----

    public string CheckingUpdate => Get();
    public string UpToDateStatus => Get();
    public string UpdateAvailableStatus => Get();
    public string UpdateCheckFailed => Get();
    public string BrowserOpenFailed => Get();
}
