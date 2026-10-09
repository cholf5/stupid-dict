# AGENTS.md

cholf5/stupid-dict：.NET 10 + Avalonia 桌面离线英汉词典（Windows / macOS / Linux）。定位是"傻瓜词典"——零配置、零账号、查询全离线；仅有的设置是主题和界面语言，联网只发生在首次资产下载和设置里手动检查更新。改功能前先读 README 的「明确不做的功能」，新增设置项原则上禁止，优先自动决定。

## 构建 / 运行

- 需要 .NET 10 SDK。`dotnet build StupidDict.slnx`；`dotnet run --project src/StupidDict.App -f net10.0`。应用启动即用，但词典数据不在仓库里（见「词典数据」）——没有 `dictionary.db` 时主窗口显示下载面板，这是正常路径不是报错。
- 测试 `dotnet test StupidDict.slnx`：`tests/StupidDict.Core.Tests`（纯 .NET，查询/导航/历史语义）+ `tests/StupidDict.App.Tests`（xunit 2 + Avalonia.Headless.XUnit，`[AvaloniaFact]` 走 headless UI）。需要 Avalonia 平台的参数化测试必须用 `[AvaloniaTheory]`——裸 `[Theory]` 不走 headless 引导，一碰控件就炸。App 工程已 `InternalsVisibleTo StupidDict.App.Tests`。CI（ubuntu，`.github/workflows/dotnet-desktop.yml`）跑同一套测试，并预装 `fonts-noto-cjk`——裸 runner 没有 CJK 字体，fontconfig 回退逐次全盘扫描会把文本密集的 UI 测试拖慢一个数量级，且 CJK 首次整形晚于首帧布局会让结果页在滚动目标设定后重新测量变高（见 `ResultsScrollReachesBottom` 的重滚循环）；CI 测试步骤带逐测试输出（`console;verbosity=normal`），别让「缓慢推进」看起来像挂死。审计门禁：`TreatWarningsAsErrors` 下 NuGetAudit 告警即 restore 失败，本地审计库滞后时本地绿不算数；唯一的 suppress 例外（GHSA-2m69-gcr7-jv3q）记录在 `Directory.Build.props`，新增压制照此办理。完整症状、排查过程与工具坑见 `docs/pitfalls/2026-10-09-ci-first-green.md`。
- headless 入口 `TestAppBuilder`：`UseHeadlessDrawing = false` + `UseSkia()`，与 inpaint 的经验一致——headless 自绘的位图语义和生产 Skia 不一致。headless 生命周期**不是** `IClassicDesktopStyleApplicationLifetime`，因此 `App.OnFrameworkInitializationCompleted` 里加载设置、创建主窗口的整段代码在测试里不会执行；测试自建 `MainWindow`。UI 测试的等待姿势是 `WaitUntil(() => 条件)`（内部泵 `Dispatcher.UIThread.RunJobs`），不要 `Thread.Sleep` 硬等。截图断言仅 `File.Exists`（SaveScreenshot 落到 `$TMPDIR`，供人工目检，不做像素断言）。
- **测试永不触碰真实用户数据**：数据目录经 `AppLocations` 注入临时副本（`NewLocations`），设置经 `SettingsService.Load/Save` 的 path 参数注入，`MainWindow` 不传 settings 时默认 `new AppSettings()` 而不是读盘；动过全局主题的测试在 `finally` 里 `App.ApplyTheme(AppTheme.System)` 还原（Application 变体是进程级单例）。
- 多目标 `net10.0;net10.0-windows`：windows TFM 只为解锁 `System.Speech`（TTS 回退），发布 Windows 用 `-f net10.0-windows`，其余平台 `-f net10.0`。
- 发版：`scripts/release.sh x.y.z [--skip-test] [--watch]`——校验（main、工作树干净、三段数字版本、本地/远端 tag 不存在）→ 本地 `dotnet test` → sed 提升 `src/StupidDict.App/StupidDict.App.csproj` 的 `<Version>` 并提交 → 打 `v` tag push → CI 测试 + 打包三平台 + 建 GitHub Release；`--watch` 轮询 CI（按 tag 指向的 commit SHA 过滤 run，防止重发时抓到旧 run）并核对产物。**tag 与 csproj 不一致会被 CI 拒绝**；重发同版本必须先删 tag（`git push origin :refs/tags/vX` + `git tag -d vX`）。Agent 收到「发版 x.y.z」即跑该脚本（带 `--watch`），成功后汇报 Release 链接与产物清单。**release.sh 是 POSIX sh，echo 里紧邻全角字符的变量必须写 `${VAR}`**——macOS 的 bash 3.2 会把多字节字符并入变量名报 unbound variable。
- `scripts/package.sh`：本地打包（`dist/` 下 `StupidDict-{rid}.zip`、`StupidDict-{rid}-with-dictionary.zip`、`dictionary.zip`/`audio-pack.zip` + `.sha256`）。CI 打包只出应用包。**数据资产与 App 版本解耦**：`dictionary.zip`/`audio-pack.zip` 放在独立 prerelease `data-1`，应用端 `ReleaseAssets.DataTag` 钉住该 tag 下载（prerelease 保证永不参与 `releases/latest`、不干扰更新检查），一次发布基本不动；数据要更新就发 `data-2` 并 bump 该常量，随下个 App 版生效。首次发布：`scripts/package.sh` 产资产后 `gh release create data-1 … --prerelease`（见 README「打包与发布」）。

## 目录与分层

- 依赖只允许向下：`StupidDict.Core`（查询/历史，零 UI 概念，SQLite）← `StupidDict.App`（Avalonia + 资产下载 + 发音 + 设置）。没有中间层、没有 MVVM 框架。
- `src/StupidDict.DataBuilder`：从 ECDICT SQLite / WordNet / CMUdict 构建 `dictionary.db`（词头 + 中文反向索引 zh_index + 词形映射 word_form + WordNet 词库 + 美音音标）。`src/StupidDict.AudioPackBuilder`：Piper TTS 批量生成发音包。两者只在构建数据时跑，不在发版链路上。
- 资源查找顺序（`AppPaths`，词典与发音包同规）：可执行文件同目录优先（打包携带）→ 用户数据目录（macOS `~/Library/Application Support/StupidDict/`、Windows `%APPDATA%`、Linux `~/.local/share`）。历史库永远在用户数据目录。

## 查询语义（Core，改动前先跑 Core.Tests）

- 匹配顺序 `Exact → Normalized Exact → Word Form（cats→cat）→ Prefix → Fuzzy`，结果带 `Tier`；未命中给「你是不是要找」建议但**绝不自动改写用户的词**。中英判定 `DictionaryService.IsChineseQuery`（CJK 区间），中文查询走 zh_index 反向索引。
- `LookupAsync`/`SuggestAsync` 都是 `Task.Run` 包同步实现；UI 侧用 generation 代数丢弃过期结果（`_searchGeneration`/`_suggestGeneration`），迟到结果不渲染。
- 输入补全：120ms 防抖（`SuggestDebounce`），1–2 字母只查常用词内存索引（`CommonWordIndex`，启动 `WarmupAsync` 预热），3 字母起全量前缀；`Enter` 永远优先查输入框原文，选中候选才查候选（headless 测试 `EnterWithSuggestionsOpenStillQueriesTypedText` 钉住了这个语义）。
- 历史导航 `LookupNavigator`：前进/后退整页缓存、不重新查询、后退不写最近搜索；重复查当前页折叠不重复入栈（`DoubleClickingHeadwordDoesNotDuplicateHistory`）。最近搜索 30 条去重，由 `RecentSearchStore` 落 SQLite。

## 资产下载（Assets/）

- 下载链按相位回退（`AssetDownloadService.BuildAttempts`）：①平台默认代理（`HttpClient.DefaultProxy`，浏览器同款路由：Windows 走 WinINET 注册表/PAC、unix 走环境变量）过全部源 → ②绕开一切代理直连镜像（防失效系统代理拖死镜像，GitHub 直连不在这一相）→ ③显式检测代理（`ProxyDetector`：环境变量 → Windows 注册表（`ParseWindowsProxyServer` 可单测）→ macOS `scutil --proxy` → Clash/V2Ray/Surge 常见端口）各过前两源。托管只有 GitHub + 镜像前缀（ghfast.top、gh-proxy.com、ghproxy.net，写死在代码里方便更新；R2/manifest 方案评估过被否——域名要长期续费）。断点续传；校验失败删净 zip/.part 自动从零重下一次（仅一次）；失败文案分阶段（下载/校验/解压），续传的进度条显示「断点续传」；解压全有或全无（staging 目录 + 原子进位）。人工兜底：词典「选择本地文件…」导入 zip 或裸 db；发音包有同款导入按钮（`ImportAudioPack` 先校验 uk/、us/ 再解压）；两个下载 UI 都带「用浏览器打开下载页」直链（浏览器走 VPN 是最可靠的通道）。`.sha256` 校验远端没有校验文件时跳过，不阻塞。
- **zip 解压两条铁律**：防 zip-slip（条目路径逃出目标目录即抛，`MainWindow.ExtractZip`）；全有或全无——半解压的发音包会因 `uk/` 目录存在被误判为已安装。临时下载目录 `stupiddict-downloads`，成功后删 zip。
- 首启流程：无 `dictionary.db` → 主界面被下载面板接管（查词被禁），就绪后热重建 `DictionaryService` 并排队下载发音包；发音包失败/取消不阻塞任何功能（未覆盖的词回退系统语音）。

## 发音（Speech/）

- 播放链 `SpeechPlayback.Create`：`AudioPackPlayer`（audio/uk、audio/us 按词命名的 MP3；文件播放器按平台 afplay / MCI / Linux 播放器）→ 失败回退 `SystemTtsPlayer`（系统 TTS）。播放器统一走 `ISpeechPlayer` 缝，测试注入 `RecordingSpeechPlayer`。
- 发音包未安装且未在下载时，点 UK/US 会在底部条给出下载入口（`PlayWord`）；按钮闪烁 ✕ 1.5s 表示无法发音，不弹窗。

## UI 约定（App）

- **代码后置渲染，无 MVVM**：查询结果整页由 `MainWindow` 用 C# 构建（`Text()`/`BuildLinkText()`/`BuildChips()`），不用 XAML 数据模板。文本一律 `SelectableTextBlock`（可选中复制），默认挂 `OnResultTextPointerPressed`（`handledEventsToo: true`——要抢在控件自己的拖选处理之前拿到双击）实现双击取词：hit-test 命中字符 → 扩到词边界 → 选中并查询。
- **词链接的点击面是 Border 包 TextBlock（`CreateLinkSurface`）**，不是 Run+点击：多 run SelectableTextBlock 的字符级命中测试在 Avalonia 11.3 不可靠（点击落到邻字、偶发 GlyphRun 内部异常），Border 做整词点击面热区完整。**近义/反义/联想三行（`BuildWordLinksLine`/`BuildRelatedWordsLine`）一律按词条整块放 `WrapPanel`，别放回流式文本**——两个 Avalonia 11.3 的坑：①换行点落在 InlineUIContainer 上时不换行而是原位溢出视口，链接词整个被裁掉而相邻文本照常渲染；②普通 Run 直接夹在两个 InlineUIContainer 之间时宽度占位但字形画低约一行（逗号集体出逃）。词条内间距用 margin 不用空格 run——挨着 CJK 文本的空格整形出来远宽于拉丁空格（拆成独立 Run 也救不了）。完整症状、复现方法与排查教训见 `docs/pitfalls/2026-10-08-avalonia-inline-links.md`。
- **颜色只从 `Settings/Palette.cs` 的语义 token 取**：XAML 用 `{DynamicResource Token}`（自动跟随主题），代码渲染用 `Palette.Get(token, ActualThemeVariant)`。token 的日/夜值在 `App.axaml` 的 `ThemeDictionaries`（Light 是原始纸面色板，Dark 是配套暖炭色）。**加颜色先加 token，禁止再写 hex 字面量**（CI 之外没有检查，靠自觉 + code review）。
- 主题切换时 XAML 样式自动换色，但代码渲染的结果页不会——`MainWindow` 订阅 `ActualThemeVariantChanged`，经 `_rebuildResults` 闭包重放当前页渲染；新增渲染路径时记得维护这个闭包（`RenderResult`/`RenderError` 已接，`ShowEmptyState` 置空）。
- **Fluent 的 TextBox 聚焦态会覆盖模板化 Border 的背景/描边**（`:focus` 状态样式赢过无状态覆盖）：日间是白底看不出来，夜间是一块黑底蓝环。搜索框的无边框化因此有两条样式（基础 + `:focus`），见 `MainWindow.axaml`；给其他输入框去边框时同理，两条都要。
- **UI 字符串一律走 `Localization/Translations.Instance`，禁止再写字面量**：zh 是源词典（键 = `nameof` 属性名），en 只放覆盖项、缺键回退中文，`Get` 兜底返回键名（漏翻译肉眼可见）。XAML 绑定 `{Binding Prop, Source={x:Static loc:Translations.Instance}}`（编译期查属性名），代码里 `Translations.Instance.Prop`，带参文案用 `string.Format`（`{0}` 占位）。新增字符串 zh/en 两份都要补；en 漏了会显示中文（能发现），zh 漏了会显示属性名（一眼假）。设计文档惯例：`docs/plans/YYYY-MM-DD-<topic>-design.md`，写背景/目标、做了什么、评估过什么、为何不做（YAGNI 显式留痕）。
- 快捷键在 `OnPreviewKeyDown` 隧道阶段处理，修饰键 `(Meta | Control)` 双认——macOS 上 ⌘ 是 `Meta`，与物理 `Ctrl` 互不匹配（Avalonia `KeyGesture` 精确相等），双认让 ⌘K / ⌘[ / ⌘] / ⌘, 在两个平台键位一致。新增快捷键照这个写法，别用 XAML `KeyBindings`（会只认其一）。

## 设置、主题与本地化

- 设置有两项：主题（`AppTheme.System/Light/Dark`，默认跟随系统）和界面语言（`AppLanguage.System/SimplifiedChinese/English`，默认跟随系统）。`AppSettings` 是共享单实例，`App.WireSettings` 启动时接线：任何属性变更立即应用（Theme → `ApplyTheme` 映射 `RequestedThemeVariant`；Language → `Translations.SetLanguage`）并原子落盘。`SettingsWindow` 只改实例，不拥有持久化；窗口是**模态对话框**（`ShowDialog`，打开期间主窗口禁用）、单实例（`Closed` 清引用；重复打开守卫只在 headless 可达，`Activate()` 聚焦已有对话框）。`⌘/Ctrl + ,` 是开关：打开半边在 `MainWindow.OnPreviewKeyDown`（隧道阶段），关闭半边（Esc / `⌘/Ctrl + ,`）在 `SettingsWindow.OnKeyDown` **气泡阶段**处理——ComboBox 弹层打开时先吃掉 Esc，窗口只在没人要这个键时才关；`OnOpened` 显式聚焦 `ThemeComboBox`，否则 Avalonia 的全局焦点仍留在主窗口搜索框，按键路由不进对话框。入口两个：搜索框右侧 ⚙ 和 `⌘/Ctrl + ,`。
- 主窗口尺寸/最大化状态**自动记忆**（无设置 UI，属「优先自动决定」范畴）：记在 `AppSettings.WindowWidth/WindowHeight/WindowMaximized`，`MainWindow` 构造时恢复（WindowState 在 Show 前赋值即可生效，styled property 经 CreatePlatformImplBinding 推给平台）、`Closed` 时写回共享实例，持久化走 `WireSettings` 既有链路（新增的三个 case 只落盘、无需应用）。**最大化期间 `Width/Height` 会被 Avalonia `HandleResized` 无条件改写成最大化尺寸**（11.3 无 RestoreBounds），因此用 Normal 态的 `Bounds` 追踪 last-normal 尺寸、最大化关闭时存它；`Opened` 时按屏幕工作区钳制尺寸。位置刻意不记（屏幕外窗口风险，评估见 `docs/plans/2026-10-09-window-size-persistence-design.md`）。
- 下拉选项用跨语言稳定的 `OptionItem` 实例数组（`Settings/OptionItem.cs`）+ `ItemTemplate` 绑 `Label`，切语言只改 Label、**不重建 ItemsSource**（重建会异步清空选区并把旧选中项经双向绑定推回）；`ItemsSource` 须先于 `SelectedIndex` 就位。**不能让 `ComboBoxItem.Content` 直绑 Translations**——Avalonia 11.3 选中框对选中项内容做快照、事后变化不回显（切语言后闭合下拉框仍是旧语言）。窗口订阅 `Translations.PropertyChanged` 刷新 Label，`OnClosed` 解订；回归测试 `LanguageSwitchRefreshesSettingsComboOptionsAndKeepsSelection`。弹层内容在 PopupRoot，进不了 ComboBox 的 `GetVisualDescendants()`，下拉项文案测试按 Label 断言。
- 设置窗口是 TabControl 四页签（照 inpaint 结构裁剪：通用=主题/语言、快捷键=只读键帽表、数据目录=词典/发音包安装状态+打开数据目录+下载页直链、关于=版本/作者/许可/仓库/检查更新）：**页签内容按选中实例化，未选中页签的控件不在可视树（绑定仍活跃）**，测试要取未选中页签的控件必须先切 `SettingsTabs.SelectedIndex`；页签内控件的 x:Name 字段在解析期就注册，code-behind 构造器可直接赋值。键帽带修饰键的随平台（macOS ⌘、其余 Ctrl），走 `SettingsWindow` 静态键帽属性 + `x:Static`，与 `OnPreviewKeyDown` 的 Meta/Ctrl 双认同源。About 页作者行、GitHub/X 图标按钮（公开品牌 Path）、HyperlinkButton 仓库链接照搬 inpaint（同一作者 cholf5）。数据目录页签是排障兜底不是管理界面（曾以区块形态放在关于页底部，关于页高度突涨后独立成页签）：完整资源管理页签与应用内检查数据更新均评估后 YAGNI（见 `docs/plans/2026-10-08-data-management-design.md`）；`SettingsWindow` 构造注入 `AppLocations`（`MainWindow.OpenSettings` 传自身 `_locations`，测试注入临时副本）与 `openDataDirectory` 缝，状态行按窗口打开时的磁盘求值、随语言切换重算。
- 本地化 = `Localization/Translations` 进程级单例（INotifyPropertyChanged）：`SetLanguage` 重拼词典后**逐属性 raise PropertyChanged**，所有在绑定的 XAML 即时刷新；代码渲染的结果页由 MainWindow 订阅该事件经 `_rebuildResults` 闭包重放（与主题切换同一机制）；**瞬态状态行（下载/更新进度、检查更新结果、版本行）不回溯刷新**，保持出现时的语言——新增瞬态文案默认接受这一点，别为它做刷新机制。跟随系统按 `CultureInfo.CurrentUICulture` 两位码解析：zh → 简体中文，其余一律 English。
- 涉及 Translations 的测试规则：程序集已 `[assembly: CollectionBehavior(DisableTestParallelization = true)]`（单例会被并行测试类互踩）；`HeadlessWindowTests` 构造器固定 `SetLanguage(SimplifiedChinese)`——**Instance 初值按机器 UI 文化解析，CI（en）与本机（zh）不同，断言中文的测试必须显式钉住**；凡是走 `WireSettings(new AppSettings())` 的测试同样会被 System 解析到机器文化，要显式 `Language = SimplifiedChinese`；切过语言的测试在 `finally` 还原。
- `SettingsService`：`settings.json` 在用户数据目录，枚举存名字（手改友好）、临时文件 + `File.Move` 原子替换；缺失/损坏/未知枚举值一律回退默认。`Load/Save` 的 path 参数供测试注入。
- 设置窗口 About 卡片的版本号与更新检查共用 `UpdateChecker.CurrentVersion`（= csproj `<Version>`，发版唯一改动点）。
- `AvaloniaUseCompiledBindingsByDefault=true` 已开：XAML 绑定路径写错编译期就报。

## 检查更新（UpdateChecker.cs）

- 请求 GitHub **网页端** `releases/latest`：关闭自动重定向、读 302 Location 抠 Release 页 tag。刻意不走 api.github.com——未认证 API 限流 60 次/小时且按 IP 计，代理/CGNAT 共享出口几乎必然 403。带 User-Agent `stupiddict-desktop/<ver>`；相对 Location 按请求地址补全。
- 版本比较：tag 去 `v` 前缀按三段数值比（段尾非数字后缀忽略、超长数字串按无法识别），失败永不抛异常，统一 `UpdateCheckResult(Failed)` + `ErrorKind` 归类（HTTP 错误收敛成 "HTTP 403" 短诊断）。入口只有设置里手动「检查更新」——启动联网违反产品承诺，**不要加自动启动检查**。发现新版给「打开发布页」逃生门。测试经 `UpdateChecker(HttpMessageHandler, currentVersion)` 注入假响应（`FakeHandler`），零真实网络；handler 是同步的，UI 测试点击后状态行立即是终态文案。

## 已知平台坑

- 图标三件套在 `src/StupidDict.App/Assets/`（png=1024 母版、ico=Windows/窗口、icns=macOS bundle），圆角一律烤在素材里（运行时图标不走系统圆角蒙版，Windows .ico 同样不加工），但**几何分两套**：png/icns 走 Apple 网格（1024 画布 + 830 内容块 + 97px 透明边距，半径 194 ≈ 内容的 23.4%——macOS Dock 语义就是要留边）；**ico 满幅出图**（Windows 对 exe/任务栏图标按 tile 原生尺寸原样渲染、无蒙版，Apple 边距烤进去整幅小一圈——2026-10-09 任务栏反馈，实测旧 ico 内容只占画布 83%；圆角同比例放大到 239/1024，不另造几何）。**换图标跑 `python3 scripts/make-icon.py <源图>`**（自动中心裁方、一次烤两套几何出三件套，并在 /tmp 生成候选 vs 现版在深/浅底 16/24/32/48px 的放大预览图；icns 那步依赖 macOS 的 iconutil）。接线：csproj `ApplicationIcon`（Windows exe）+ `AvaloniaResource`（avares 前缀是 AssemblyName `StupidDict` 不是项目名；icns 不内嵌，只随 package.sh 进 bundle）+ 两窗口 `Icon="avares://StupidDict/Assets/app-icon.ico"`。macOS Dock 图标不认 XAML `Window.Icon`：打包包由 Info.plist `CFBundleIconFile`（package.sh 拷 icns），裸 `dotnet run` 由 `MacDockIcon` 在 App 空闲优先级把内嵌扁平 PNG 设给 NSApplication（libobjc 手发消息，失败静默，只在桌面生命周期分支调用；运行时喂 PNG 是 Electron/SDL/Godot 的同款做法）。**注意 `NSRunningApplication.icon` 只读 LaunchServices 层、对运行时设置全盲，验证只能人眼看 Dock**（终端截屏需屏幕录制授权，通常没有）。回归测试 `WindowsCarryAppIconFromEmbeddedAssets`（接线）+ `WindowsTaskbarIconArtworkIsFullBleed`（ico 满幅几何，手解 ICO 目录取 256 帧经 SKBitmap 采样 alpha；SkiaSharp 经 Avalonia.Skia 传递引用）。完整症状与排查见 `docs/pitfalls/2026-10-09-windows-taskbar-ico-full-bleed.md`。
- `SelectableTextBlock` 无内置双击选词（Avalonia 11），`OnResultTextPointerPressed` 是自制实现；中文不响应双击（无空格分词，命中无意义）。
- **别给 ScrollViewer 挂 `Padding`**（11.3 把它转发给 ScrollContentPresenter：extent 扣掉 padding、viewport 不扣，最大 offset 够不着内容底部，结果页最后一行永远被裁）。内边距写在 ScrollViewer 的内容上（`MainWindow.axaml` 结果区 `<Panel Margin=…>`），margin 会被 extent 正确计入。验证滚动可达性用布局几何断言（最大 offset 下内容最低点 vs 视口底，回归测试 `ResultsScrollReachesBottom`），别信滚动后的 headless 截图——滚动位移走 compositor transform，headless 捕获会滞后一拍给出未滚动的旧帧。症状、数据与教训见 `docs/pitfalls/2026-10-08-scrollviewer-padding.md`。
- 发布 Windows 用 `-f net10.0-windows`，漏掉会静默丢 System.Speech 回退。
