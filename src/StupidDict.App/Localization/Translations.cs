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
        [nameof(AppName)] = "傻瓜词典",
        [nameof(SearchWatermark)] = "输入单词或中文，按 Enter 查询",
        [nameof(NavBackTip)] = "后退 (⌘[)",
        [nameof(NavForwardTip)] = "前进 (⌘])",
        [nameof(SettingsTip)] = "设置 (⌘,)",
        [nameof(ClearSearchTip)] = "清空搜索框",
        [nameof(Tagline)] = "完全离线 · 无账号 · 零配置",
        [nameof(DownloadPrompt)] = "首次使用需要词典数据（约 600 MB）。直接下载，或导入本地已有文件。",
        [nameof(StartDownload)] = "开始下载",
        [nameof(PickLocalFile)] = "选择本地文件…",
        [nameof(Cancel)] = "取消",
        [nameof(Recent)] = "最近",
        [nameof(Verifying)] = "校验中…",
        [nameof(Extracting)] = "解压中…",
        [nameof(ExtractingFilesFormat)] = "正在解压 {0:N0} / {1:N0} 个文件…",
        [nameof(Importing)] = "导入中…",
        [nameof(Converting)] = "正在转换发音包",
        [nameof(ConvertingFilesFormat)] = "正在转换 {0:N0} / {1:N0} 个条目…",
        [nameof(DownloadCancelled)] = "已取消下载。可以直接下载，或选择本地已有文件。",
        [nameof(DownloadFailedFormat)] = "下载失败：{0}",
        [nameof(ImportFailedFormat)] = "导入失败：{0}",
        [nameof(ImportMissingDb)] = "文件里没有 dictionary.db",
        [nameof(ImportMissingPack)] = "文件里没有发音包（应包含 uk/、us/ 目录）",
        [nameof(ImportInvalidDatabase)] = "不是有效的发音包数据库",
        [nameof(ImportUnsupportedFormat)] = "只支持发音包数据库 audio-pack.db 或发音包压缩包 audio-pack.zip",
        [nameof(PickerTitleAudioPack)] = "选择发音包（audio-pack.db / audio-pack.zip）",
        [nameof(FileTypeAudioPack)] = "发音包",
        [nameof(OpenDownloadPage)] = "用浏览器打开下载页",
        [nameof(AllSourcesFailed)] = "所有下载源都失败了。请检查网络，或手动下载后导入。",
        [nameof(ChecksumFailed)] = "下载文件校验失败，已删除损坏文件。",
        [nameof(ChecksumRedownloading)] = "校验失败，已删除损坏文件，正在重新下载…",
        [nameof(ExtractFailedFormat)] = "解压失败：{0}",
        [nameof(AudioPackImportFailedFormat)] = "发音包导入失败：{0}",
        [nameof(ExtractCorruptPurged)] = "压缩包数据损坏，已删除；重试将重新下载。",
        [nameof(ZipCrcMismatchFormat)] = "压缩包数据损坏：条目 {0} 的 CRC 校验不符",
        [nameof(ZipSlipFormat)] = "压缩包内出现非法路径：{0}",
        [nameof(DownloadingDictionaryFormat)] = "正在下载词典 {0:F0} / {1:F0} MB",
        [nameof(DownloadingDictionaryUnsizedFormat)] = "正在下载词典 {0:F0} MB",
        [nameof(DownloadingAudioPack)] = "正在下载发音包（约 600 MB，一次性）",
        [nameof(DownloadingAudioPackFormat)] = "正在下载发音包 {0:F0} / {1:F0} MB",
        [nameof(DownloadingAudioPackUnsizedFormat)] = "正在下载发音包 {0:F0} MB",
        [nameof(DownloadResumeFormat)] = "断点续传 {0:F0} / {1:F0} MB",
        [nameof(DownloadResumeUnsizedFormat)] = "断点续传 {0:F0} MB",
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
        [nameof(PickerFailedFormat)] = "无法打开文件选择器：{0}",
        [nameof(FileTypeDictionary)] = "词典数据",
        [nameof(SettingsTitle)] = "设置",
        [nameof(SectionGeneral)] = "通用",
        [nameof(ThemeLabel)] = "主题",
        [nameof(FollowSystem)] = "跟随系统",
        [nameof(ThemeLight)] = "日间",
        [nameof(ThemeDark)] = "夜间",
        [nameof(LanguageLabel)] = "语言",
        [nameof(LangChinese)] = "简体中文",
        [nameof(LangEnglish)] = "English",
        [nameof(LaunchAtLoginLabel)] = "开机自启",
        [nameof(SectionShortcuts)] = "快捷键",
        [nameof(ShortcutLookup)] = "查你输入的词；有选中候选时查该候选",
        [nameof(ShortcutEscape)] = "关闭候选列表；再按清空输入；设置窗口内关闭设置",
        [nameof(ShortcutArrows)] = "浏览补全候选；输入为空时浏览最近搜索",
        [nameof(ShortcutBackForward)] = "后退 / 前进（查询历史）",
        [nameof(ShortcutFocusSearch)] = "聚焦搜索框",
        [nameof(ShortcutSettings)] = "打开 / 关闭设置",
        [nameof(SectionData)] = "数据目录",
        [nameof(SectionAbout)] = "关于",
        [nameof(AboutBlurb)] = "完全离线的英汉词典。查询不联网，联网只发生在首次下载词典与发音包的时候。",
        [nameof(AuthorLabel)] = "作者",
        [nameof(AuthorName)] = "周尔复",
        [nameof(LicenseLabel)] = "开源许可",
        [nameof(RepoLabel)] = "仓库地址",
        [nameof(DictionaryDataLabel)] = "词典",
        [nameof(AudioPackDataLabel)] = "发音包",
        [nameof(AssetInstalled)] = "已安装",
        [nameof(AssetNotInstalled)] = "未安装",
        [nameof(OpenDataDirectory)] = "打开数据目录",
        [nameof(DataDirectoryHint)] = "下载的词典与发音包保存在这个目录；随安装包携带的在应用所在目录。",
        [nameof(OpenDataDirectoryFailed)] = "打开目录失败：{0}",
        [nameof(CheckUpdate)] = "检查更新",
        [nameof(OpenReleasePage)] = "打开发布页",
        [nameof(CheckingUpdate)] = "正在检查更新…",
        [nameof(UpToDateStatus)] = "已是最新版本（{0}）",
        [nameof(UpdateAvailableStatus)] = "发现新版本 {0}，当前 {1}",
        [nameof(UpdateCheckFailed)] = "检查失败：{0}",
        [nameof(BrowserOpenFailed)] = "无法打开浏览器：{0}",
        [nameof(SectionTranslation)] = "翻译",
        [nameof(TranslationApiUrlLabel)] = "API 地址",
        [nameof(TranslationApiUrlPlaceholder)] = "例如 https://api.openai.com/v1",
        [nameof(TranslationApiKeyLabel)] = "API 密钥",
        [nameof(TranslationModelLabel)] = "模型",
        [nameof(TranslationModelPlaceholder)] = "例如 gpt-4o-mini 或 qwen2.5:7b",
        [nameof(TranslationEndpointHint)] = "填服务地址即可，如 https://api.openai.com/v1、https://api.deepseek.com、http://localhost:11434（Ollama）或完整的 /chat/completions 端点；只支持 OpenAI 兼容接口。",
        [nameof(TranslationAutoLabel)] = "未命中的句子自动翻译",
        [nameof(TranslationSave)] = "保存",
        [nameof(TranslationTest)] = "测试连接",
        [nameof(TranslationTesting)] = "正在测试连接…",
        [nameof(TranslationSaved)] = "已保存。",
        [nameof(TranslationInvalidUrl)] = "API 地址无效：需要 http/https 地址，不能带账号信息或查询参数；公网地址必须用 https。",
        [nameof(TranslationUrlAndModelRequired)] = "请填写 API 地址和模型。",
        [nameof(TranslationTestSucceeded)] = "连接成功。",
        [nameof(TranslationTestFailedFormat)] = "测试失败：{0}",
        [nameof(TranslationPrivacy)] = "查词始终离线。只有词典未命中的句子才会发送到你配置的 API；应用不收集、不转存任何内容。密钥以明文保存在本机设置文件中。",
        [nameof(TranslationExamples)] = "示例：OpenAI https://api.openai.com/v1 · DeepSeek https://api.deepseek.com · Ollama http://localhost:11434 · OpenRouter https://openrouter.ai/api/v1",
        [nameof(TranslationCacheFormat)] = "翻译缓存：{0:N1} MB",
        [nameof(TranslationCacheCleared)] = "缓存已清除。",
        [nameof(TranslationClearCache)] = "清除缓存",
        [nameof(TranslationInProgress)] = "正在翻译…",
        [nameof(TranslateSentence)] = "翻译这句话",
        [nameof(TranslateWord)] = "翻译这个词",
        [nameof(TranslationOpenSettings)] = "配置翻译",
        [nameof(TranslationNotConfigured)] = "配置翻译 API 后可以翻译整句；查词不受影响。",
        [nameof(TranslationLocalFormat)] = "本地模型（{0}）",
        [nameof(TranslationCloudFormat)] = "机器翻译（{0}）",
        [nameof(TranslationFailedFormat)] = "翻译失败：{0}",
        [nameof(TranslationAuthFailed)] = "密钥无效或无权限，请检查设置",
        [nameof(TranslationEndpointFailed)] = "地址或模型名不对，请检查设置",
        [nameof(TranslationRateLimited)] = "请求过于频繁或额度不足，稍后再试",
        [nameof(TranslationServerFailed)] = "服务端错误，稍后再试",
        [nameof(TranslationTimeout)] = "连接超时",
        [nameof(TranslationNetworkFailed)] = "网络错误，请检查连接",
        [nameof(TranslationInvalidResponse)] = "服务返回了无法解析的结果",
        [nameof(TranslationIncomplete)] = "译文可能不完整",
        [nameof(TranslationRefused)] = "服务拒绝了这次翻译请求",
    };

    private static readonly Dictionary<string, string> En = new()
    {
        [nameof(AppName)] = "Stupid Dict",
        [nameof(SearchWatermark)] = "Type an English word or Chinese, then press Enter",
        [nameof(NavBackTip)] = "Back (⌘[)",
        [nameof(NavForwardTip)] = "Forward (⌘])",
        [nameof(SettingsTip)] = "Settings (⌘,)",
        [nameof(ClearSearchTip)] = "Clear search box",
        [nameof(Tagline)] = "Fully offline · No account · Zero setup",
        [nameof(DownloadPrompt)] = "First run needs the dictionary data (about 600 MB). Download it, or import a local copy.",
        [nameof(StartDownload)] = "Download",
        [nameof(PickLocalFile)] = "Choose a local file…",
        [nameof(Cancel)] = "Cancel",
        [nameof(Recent)] = "Recent",
        [nameof(Verifying)] = "Verifying…",
        [nameof(Extracting)] = "Extracting…",
        [nameof(ExtractingFilesFormat)] = "Extracting {0:N0} of {1:N0} files…",
        [nameof(Importing)] = "Importing…",
        [nameof(Converting)] = "Converting the audio pack",
        [nameof(ConvertingFilesFormat)] = "Converting {0:N0} of {1:N0} entries…",
        [nameof(DownloadCancelled)] = "Download cancelled. You can download again or choose a local file.",
        [nameof(DownloadFailedFormat)] = "Download failed: {0}",
        [nameof(ImportFailedFormat)] = "Import failed: {0}",
        [nameof(ImportMissingDb)] = "The file does not contain dictionary.db",
        [nameof(ImportMissingPack)] = "The file does not contain the pronunciation pack (uk/ and us/ directories expected)",
        [nameof(ImportInvalidDatabase)] = "Not a valid pronunciation pack database",
        [nameof(ImportUnsupportedFormat)] = "Only an audio pack database (audio-pack.db) or archive (audio-pack.zip) can be imported",
        [nameof(PickerTitleAudioPack)] = "Choose an audio pack (audio-pack.db / audio-pack.zip)",
        [nameof(FileTypeAudioPack)] = "Pronunciation pack",
        [nameof(PickerFailedFormat)] = "Could not open the file picker: {0}",
        [nameof(OpenDownloadPage)] = "Open the download page in a browser",
        [nameof(AllSourcesFailed)] = "Every download source failed. Check the network, or download and import manually.",
        [nameof(ChecksumFailed)] = "Checksum verification failed; the corrupted file was deleted.",
        [nameof(ChecksumRedownloading)] = "Checksum failed; the corrupted file was deleted. Downloading again…",
        [nameof(ExtractFailedFormat)] = "Extraction failed: {0}",
        [nameof(AudioPackImportFailedFormat)] = "Failed to import the audio pack: {0}",
        [nameof(ExtractCorruptPurged)] = "The archive data is corrupted; it was deleted and retrying downloads again.",
        [nameof(ZipCrcMismatchFormat)] = "The archive data is corrupted: entry {0} failed its CRC check",
        [nameof(ZipSlipFormat)] = "Illegal path inside the archive: {0}",
        [nameof(DownloadingDictionaryFormat)] = "Downloading dictionary {0:F0} / {1:F0} MB",
        [nameof(DownloadingDictionaryUnsizedFormat)] = "Downloading dictionary {0:F0} MB",
        [nameof(DownloadingAudioPack)] = "Downloading the pronunciation pack (about 600 MB, one-time)",
        [nameof(DownloadingAudioPackFormat)] = "Downloading pronunciation pack {0:F0} / {1:F0} MB",
        [nameof(DownloadingAudioPackUnsizedFormat)] = "Downloading pronunciation pack {0:F0} MB",
        [nameof(DownloadResumeFormat)] = "Resuming download {0:F0} / {1:F0} MB",
        [nameof(DownloadResumeUnsizedFormat)] = "Resuming download {0:F0} MB",
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
        [nameof(FollowSystem)] = "System",
        [nameof(ThemeLight)] = "Light",
        [nameof(ThemeDark)] = "Dark",
        [nameof(LanguageLabel)] = "Language",
        [nameof(LangChinese)] = "Simplified Chinese",
        [nameof(LaunchAtLoginLabel)] = "Launch at login",
        [nameof(SectionShortcuts)] = "Shortcuts",
        [nameof(ShortcutLookup)] = "Look up what you typed; the selected suggestion, if one is chosen",
        [nameof(ShortcutEscape)] = "Close the suggestion list; press again to clear the box; closes the settings window",
        [nameof(ShortcutArrows)] = "Browse suggestions; recent searches when the box is empty",
        [nameof(ShortcutBackForward)] = "Back / Forward (lookup history)",
        [nameof(ShortcutFocusSearch)] = "Focus the search box",
        [nameof(ShortcutSettings)] = "Open / close settings",
        [nameof(SectionData)] = "Data folder",
        [nameof(SectionAbout)] = "About",
        [nameof(AboutBlurb)] = "A fully offline English-Chinese dictionary. Lookups never touch the network; the network is only used for the first dictionary and pronunciation pack download.",
        [nameof(AuthorLabel)] = "Author",
        [nameof(AuthorName)] = "Cholf",
        [nameof(LicenseLabel)] = "License",
        [nameof(RepoLabel)] = "Repository",
        [nameof(DictionaryDataLabel)] = "Dictionary",
        [nameof(AudioPackDataLabel)] = "Audio pack",
        [nameof(AssetInstalled)] = "Installed",
        [nameof(AssetNotInstalled)] = "Not installed",
        [nameof(OpenDataDirectory)] = "Open data folder",
        [nameof(DataDirectoryHint)] = "Downloaded dictionary and audio pack live in this folder; bundled ones live next to the application.",
        [nameof(OpenDataDirectoryFailed)] = "Could not open the folder: {0}",
        [nameof(CheckUpdate)] = "Check for updates",
        [nameof(OpenReleasePage)] = "Open release page",
        [nameof(CheckingUpdate)] = "Checking for updates…",
        [nameof(UpToDateStatus)] = "Up to date ({0})",
        [nameof(UpdateAvailableStatus)] = "New version {0} available, current {1}",
        [nameof(UpdateCheckFailed)] = "Check failed: {0}",
        [nameof(BrowserOpenFailed)] = "Could not open the browser: {0}",
        [nameof(SectionTranslation)] = "Translation",
        [nameof(TranslationApiUrlLabel)] = "API URL",
        [nameof(TranslationApiUrlPlaceholder)] = "e.g. https://api.openai.com/v1",
        [nameof(TranslationApiKeyLabel)] = "API key",
        [nameof(TranslationModelLabel)] = "Model",
        [nameof(TranslationModelPlaceholder)] = "e.g. gpt-4o-mini or qwen2.5:7b",
        [nameof(TranslationEndpointHint)] = "Enter the service base URL, e.g. https://api.openai.com/v1, https://api.deepseek.com, http://localhost:11434 (Ollama), or a full /chat/completions endpoint. Only OpenAI-compatible APIs are supported.",
        [nameof(TranslationAutoLabel)] = "Auto-translate sentences that miss the dictionary",
        [nameof(TranslationSave)] = "Save",
        [nameof(TranslationTest)] = "Test connection",
        [nameof(TranslationTesting)] = "Testing connection…",
        [nameof(TranslationSaved)] = "Saved.",
        [nameof(TranslationInvalidUrl)] = "Invalid API URL: an http/https URL is required without user info or query parameters; public addresses must use https.",
        [nameof(TranslationUrlAndModelRequired)] = "Fill in the API URL and the model.",
        [nameof(TranslationTestSucceeded)] = "Connection OK.",
        [nameof(TranslationTestFailedFormat)] = "Test failed: {0}",
        [nameof(TranslationPrivacy)] = "Lookups stay offline. Only sentences that miss the dictionary are sent to the API you configured; the app collects and forwards nothing else. The key is stored in plain text in the local settings file.",
        [nameof(TranslationExamples)] = "Examples: OpenAI https://api.openai.com/v1 · DeepSeek https://api.deepseek.com · Ollama http://localhost:11434 · OpenRouter https://openrouter.ai/api/v1",
        [nameof(TranslationCacheFormat)] = "Translation cache: {0:N1} MB",
        [nameof(TranslationCacheCleared)] = "Cache cleared.",
        [nameof(TranslationClearCache)] = "Clear cache",
        [nameof(TranslationInProgress)] = "Translating…",
        [nameof(TranslateSentence)] = "Translate this sentence",
        [nameof(TranslateWord)] = "Translate this word",
        [nameof(TranslationOpenSettings)] = "Configure translation",
        [nameof(TranslationNotConfigured)] = "Configure a translation API to translate whole sentences; lookups are unaffected.",
        [nameof(TranslationLocalFormat)] = "Local model ({0})",
        [nameof(TranslationCloudFormat)] = "Machine translation ({0})",
        [nameof(TranslationFailedFormat)] = "Translation failed: {0}",
        [nameof(TranslationAuthFailed)] = "The key is invalid or unauthorized — check Settings",
        [nameof(TranslationEndpointFailed)] = "Wrong URL or model name — check Settings",
        [nameof(TranslationRateLimited)] = "Rate limited or out of quota — try again later",
        [nameof(TranslationServerFailed)] = "Server error — try again later",
        [nameof(TranslationTimeout)] = "Connection timed out",
        [nameof(TranslationNetworkFailed)] = "Network error — check your connection",
        [nameof(TranslationInvalidResponse)] = "The service returned an unparseable response",
        [nameof(TranslationIncomplete)] = "The translation may be incomplete",
        [nameof(TranslationRefused)] = "The service refused this translation request",
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

    /// <summary>应用名：标题栏、空态标题与设置 About 卡共用。</summary>
    public string AppName => Get();
    /// <summary>搜索框水印与空态提示共用同一条文案。</summary>
    public string SearchWatermark => Get();
    public string NavBackTip => Get();
    public string NavForwardTip => Get();
    public string SettingsTip => Get();
    public string ClearSearchTip => Get();
    public string Tagline => Get();
    public string DownloadPrompt => Get();
    public string StartDownload => Get();
    public string PickLocalFile => Get();
    public string Cancel => Get();
    public string Recent => Get();

    // ---- 下载 / 导入状态行（瞬态：不随语言切换回溯刷新）----

    public string Verifying => Get();
    public string Extracting => Get();
    /// <summary>{0} 已完成条目数，{1} 总条目数。</summary>
    public string ExtractingFilesFormat => Get();
    public string Importing => Get();
    public string Converting => Get();
    /// <summary>{0} 已完成条目数，{1} 总条目数。</summary>
    public string ConvertingFilesFormat => Get();
    public string DownloadCancelled => Get();
    public string DownloadFailedFormat => Get();
    public string ImportFailedFormat => Get();
    public string ImportMissingDb => Get();
    /// <summary>异常消息即用户可见文案（经下载状态行展示），因此进词池。</summary>
    public string AllSourcesFailed => Get();
    public string ChecksumFailed => Get();
    public string ChecksumRedownloading => Get();
    public string ExtractFailedFormat => Get();
    public string AudioPackImportFailedFormat => Get();
    /// <summary>解压发现压缩包字节损坏并已清除（重试会重新下载）。</summary>
    public string ExtractCorruptPurged => Get();
    /// <summary>{0} CRC 校验不符的条目名。</summary>
    public string ZipCrcMismatchFormat => Get();
    public string ZipSlipFormat => Get();
    public string ImportMissingPack => Get();
    /// <summary>手动导入的数据库文件未通过完整性校验。</summary>
    public string ImportInvalidDatabase => Get();
    /// <summary>手动导入了既非 .db 也非 .zip 的文件。</summary>
    public string ImportUnsupportedFormat => Get();
    public string PickerTitleAudioPack => Get();
    public string FileTypeAudioPack => Get();
    public string OpenDownloadPage => Get();
    /// <summary>{0} 已接收 MB，{1} 总 MB。</summary>
    public string DownloadingDictionaryFormat => Get();
    public string DownloadingDictionaryUnsizedFormat => Get();
    public string DownloadingAudioPack => Get();
    public string DownloadingAudioPackFormat => Get();
    public string DownloadingAudioPackUnsizedFormat => Get();
    /// <summary>{0} 已接收 MB，{1} 总 MB；断点续传的第二遍下载进度。</summary>
    public string DownloadResumeFormat => Get();
    public string DownloadResumeUnsizedFormat => Get();
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
    /// <summary>文件选择器自身打不开时的两个导入入口共用。</summary>
    public string PickerFailedFormat => Get();
    public string FileTypeDictionary => Get();

    // ---- 设置窗口 ----

    public string SettingsTitle => Get();
    public string SectionGeneral => Get();
    public string ThemeLabel => Get();
    public string FollowSystem => Get();
    public string ThemeLight => Get();
    public string ThemeDark => Get();
    public string LanguageLabel => Get();
    public string LangChinese => Get();
    public string LangEnglish => Get();
    public string LaunchAtLoginLabel => Get();
    public string SectionShortcuts => Get();
    public string ShortcutLookup => Get();
    public string ShortcutEscape => Get();
    public string ShortcutArrows => Get();
    public string ShortcutBackForward => Get();
    public string ShortcutFocusSearch => Get();
    public string ShortcutSettings => Get();
    /// <summary>数据页签：词典/发音包安装状态 + 打开数据目录 + 下载页直链。</summary>
    public string SectionData => Get();
    public string SectionAbout => Get();
    public string AboutBlurb => Get();
    public string AuthorLabel => Get();
    public string AuthorName => Get();
    public string LicenseLabel => Get();
    public string RepoLabel => Get();
    /// <summary>关于页数据区块：安装状态行的行标签（词典 / 发音包）。</summary>
    public string DictionaryDataLabel => Get();
    public string AudioPackDataLabel => Get();
    public string AssetInstalled => Get();
    public string AssetNotInstalled => Get();
    public string OpenDataDirectory => Get();
    /// <summary>覆盖 exe 同目录优先规则造成的两种资产布局差异。</summary>
    public string DataDirectoryHint => Get();
    /// <summary>{0} 为异常消息。</summary>
    public string OpenDataDirectoryFailed => Get();
    public string CheckUpdate => Get();
    public string OpenReleasePage => Get();

    // ---- 检查更新（瞬态结果行不经此刷新，保持出现时的语言）----

    public string CheckingUpdate => Get();
    public string UpToDateStatus => Get();
    public string UpdateAvailableStatus => Get();
    public string UpdateCheckFailed => Get();
    public string BrowserOpenFailed => Get();

    // ---- 翻译（BYO OpenAI 兼容 API）----

    public string SectionTranslation => Get();
    public string TranslationApiUrlLabel => Get();
    public string TranslationApiUrlPlaceholder => Get();
    public string TranslationApiKeyLabel => Get();
    public string TranslationModelLabel => Get();
    public string TranslationModelPlaceholder => Get();
    public string TranslationEndpointHint => Get();
    public string TranslationAutoLabel => Get();
    public string TranslationSave => Get();
    public string TranslationTest => Get();
    public string TranslationTesting => Get();
    public string TranslationSaved => Get();
    public string TranslationInvalidUrl => Get();
    public string TranslationUrlAndModelRequired => Get();
    public string TranslationTestSucceeded => Get();
    /// <summary>{0} 为失败原因短诊断或异常消息。</summary>
    public string TranslationTestFailedFormat => Get();
    public string TranslationPrivacy => Get();
    public string TranslationExamples => Get();
    /// <summary>{0} 为缓存字节数折算的 MB。</summary>
    public string TranslationCacheFormat => Get();
    public string TranslationCacheCleared => Get();
    public string TranslationClearCache => Get();

    // ---- 翻译卡（结果页）----

    public string TranslationInProgress => Get();
    public string TranslateSentence => Get();
    public string TranslateWord => Get();
    public string TranslationOpenSettings => Get();
    public string TranslationNotConfigured => Get();
    /// <summary>{0} 为模型名；loopback 端点用 TranslationLocalFormat。</summary>
    public string TranslationCloudFormat => Get();
    public string TranslationLocalFormat => Get();
    /// <summary>{0} 为按失败类别映射的短文案（Translation*Failed 族）。</summary>
    public string TranslationFailedFormat => Get();
    public string TranslationAuthFailed => Get();
    public string TranslationEndpointFailed => Get();
    public string TranslationRateLimited => Get();
    public string TranslationServerFailed => Get();
    public string TranslationTimeout => Get();
    public string TranslationNetworkFailed => Get();
    public string TranslationInvalidResponse => Get();
    public string TranslationIncomplete => Get();
    public string TranslationRefused => Get();
}
