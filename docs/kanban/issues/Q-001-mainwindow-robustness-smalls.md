---
id: Q-001
title: MainWindow 杂项健壮性五小项（未观察任务/丢弃任务/裸 await/UI 闪烁/滚动入视）
type: chore
priority: P2
size: M
status: done
created: 2026-10-09
updated: 2026-10-09
blocked: none
---

## Goal

修掉 MainWindow 侧五个低频但真实的健壮性/体验小项，各自独立可验收。

## Background

2026-10-09 扫描确认（行号为当时时点）：

1. **`:115`、`:751` `_ = _service.WarmupAsync()` 未观察异常**——词典库损坏/被锁时 `WarmupAsync`（DictionaryService.cs:44，`Task.Run(() => _ = _commonWords.Words)`）的异常落入未观察 Task 被静默吞掉，预热无声失效，首个查询才在 `RenderError` 暴露。修法：`.ContinueWith` 记录或吞掉异常的续接（预热语义本就是 best-effort，关键是别留未观察 Task）。
2. **`:423-425` `_ = _settingsWindow.ShowDialog(this)` 丢弃任务**——`ShowDialog` 失败（owner 正在关闭、平台异常）时 `Closed` 不触发，`_settingsWindow` 保持非 null，此后 ⌘, 只会 `Activate()` 一个永不出现的窗口，设置入口到重启前不可用。修法：任务续接兜底（失败时清 `_settingsWindow` 并可状态行提示）。
3. **`:712-719`、`:873-880` 两个文件选择器 `await StorageProvider.OpenFilePickerAsync` 在 try 之外**——Linux 缺 portal、平台实现异常时抛，async void 未兜 → 进程崩。修法：与上轮 UpdateChecker 同族处理，选择器 await 也进 try/catch。
4. **`:222-228` + `:310-315` 点候选时空态面板闪现**——候选面板打开时 `ShowSuggestions` 已隐藏 `HintPanel`；点选候选 → `SetQueryText` → `TextChanged` 进 suppress 分支 → `HideSuggestions()` 且 `HintPanel.IsVisible = true`（此刻结果还没渲染）→ 直到查询完成才被覆盖，空态大水印闪现一帧到上百毫秒。修法方向：suppress 分支仅当 `ResultsPanel` 不可见时才显示 HintPanel，或选择路径直接隐藏。
5. **`MainWindow.axaml:79-91` 候选键盘高亮无滚动入视**——ScrollViewer `MaxHeight="280"`（外层 Padding 6 → 视口 ~268px）小于 8 项高度（~272-280px），`UpdateSuggestSelection`（:303-308）只切 `Classes` 无滚动入视，↓ 选到第 8 项可能半裁切（回车仍能正确选中，功能不坏）。修法：调 MaxHeight 或计算 offset 滚动入视。

## Acceptance Criteria

- [x] 五项各自落地且互不回归（1-3 是健壮性，4-5 是体验）
- [x] `dotnet test StupidDict.slnx` 全绿（App 126+，实测 136）

## Subtasks

- [x] WarmupAsync 异常观察
- [x] ShowDialog 任务兜底
- [x] 两个 OpenFilePickerAsync 进 try
- [x] 候选选中不闪空态
- [x] 候选高亮滚动入视

## Dependencies

- none

## Test Cases

### TC-001: 候选选中不闪空态

Steps:
1. headless 打开候选面板，`WaitUntil` 断言 HintPanel 隐藏
2. 点选候选

Expected:
- HintPanel 不再被 suppress 分支拉回可见

Result: 通过（`SelectingSuggestionDoesNotFlashEmptyStateHint`）。候选面板打开后 HintPanel 隐藏；用 `LookupOverride` TCS 把查询停在半空、`RaiseClick` 候选并 `RunJobs` 泵过 posted TextChanged，此刻断言 HintPanel 仍隐藏、结果页未出现、候选面板已收起；SetResult 后结果正常渲染。修复前实测红（suppress 分支拉回水印）。

### TC-002: 第 8 项高亮可见

Steps:
1. 构造 8 条候选，键盘 ↓ 到第 8 项

Expected:
- 第 8 项在视口内（布局几何断言）

Result: 通过（`ArrowingToLastSuggestionKeepsItInsideViewport`）。种 8 个 "caa*" 词凑满 SuggestLimit；因生产 MaxHeight=280 与 8 行高度几乎持平、是否溢出取决于平台字体度量，测试把 SuggestScroll.MaxHeight 收到 150 制造确定性溢出（被测的滚动入视逻辑与上限取值无关）。↓×8 后断言第 8 项带 selected 类、其 top/bottom 全在视口内（`TranslatePoint` 对 ScrollViewer 内容的布局几何，同 `ResultsScrollReachesBottom` 纪律）；↑×7 后第 1 项回到视口顶部。修复前实测红（无滚动时第 8 项 bottom 越过视口）。

（1-3 项异常路径各补一条注入缝测试或显式留痕不做。→ 三条都补了：WarmupQuietlyObservesCorruptDictionaryFailure、FailedSettingsDialogReleasesReopenGuard、DictionaryPickerFailureSurfacesStatusInsteadOfCrashing / AudioPackPickerFailureSurfacesStatusInsteadOfCrashing。）

## Development Log

逐项（行号为修复前工作树时点）：

1. **WarmupAsync 未观察异常（原 :115/:751，现两处调用点）**：根因——`_ = _service.WarmupAsync()`，词典库损坏/被锁时 `WarmupAsync`（`Task.Run(() => _ = _commonWords.Words)`）的异常落进未观察 Task，预热无声失效且 UnobservedTaskException 兜底。修法：新增 `internal static Task WarmupQuietlyAsync(DictionaryService)` 观察型包裹（try/catch 吞掉、注释指明首查 RenderError 才是暴露点；`ConfigureAwait(false)`——续接无 UI 工作），两处调用点改走它。参数收 `service` 而非读字段，`FinishDictionarySetup` 换库后旧 warmup 在途也不会错绑。测试用损坏文件（非 SQLite 头的文本）注入：先 `Assert.ThrowsAnyAsync` 证明裸 warmup 确实 fault（SQLite Error 26 'file is not a database'，非空过），再断言包裹完成后不抛。
2. **ShowDialog 丢弃任务（原 :423-425）**：根因——`_ = _settingsWindow.ShowDialog(this)` 失败（owner 正在关闭、平台异常）时 `Closed` 不触发，`_settingsWindow` 悬挂非 null，此后 ⌘, 只 `Activate()` 幽灵窗口，设置入口到重启前不可用。修法：抽 `internal Task ShowSettingsDialogAsync(SettingsWindow dialog, Window owner)`，await 进 try，失败时 `ReferenceEquals` 守卫下清 `_settingsWindow`（防误清新开的实例）；`Closed` 正常路径不动。卡上「并可状态行提示」未做：主窗口没有通用状态行（仅有的两条状态文本在下载面板里，正常态不可见），释放守卫已让下一次 ⌘, 恢复可用，是实际要害。测试：owner 传 null 使 ShowDialog 在展示前确定性抛 ArgumentNullException（Avalonia `ShowCore` 的 modal null 检查，11.3.22 源码核实）；测试先走真实打开/关闭验证 Closed 路径，再把守卫人为指向幽灵窗口走失败路径，最后点击设置按钮确认入口复活。`_settingsWindow` 转 internal 供测试复现悬挂态。
3. **两个 OpenFilePickerAsync 在 try 之外（原 :712-719/:873-880）**：根因——async void 处理器里选择器 await 无兜底，Linux 缺 portal / 平台实现异常直接炸进程。修法：两个处理器抽成 `internal Task PickDictionaryAsync()/PickAudioPickAsync()`（async void 壳只 await），选择器 await 单独 try/catch，失败写各自状态行（词典→`DictionaryDownloadStatus`，发音包→`AudioPackStatus`），文案走新增 `Translations.PickerFailedFormat`（zh「无法打开文件选择器：{0}」/ en 两份都补）；导入段原有 try/catch 不动。注入缝说明：Avalonia 给 `IStorageProvider` 打了 `[NotClientImplementable]`（分析器在 `TreatWarningsAsErrors` 下会拦 fake 实现），改用与 B-003 `LookupOverride` 同族的委托缝 `internal Func<FilePickerOpenOptions, Task<IReadOnlyList<IStorageFile>>>? OpenFilePickerOverride`（生产 null 回退 `StorageProvider.OpenFilePickerAsync`）。两条测试各注入抛 `InvalidOperationException("no portal")` 的 Task，断言状态行文案且未导入任何文件。
4. **候选选中闪空态（原 :222-228 + :310-315）**：根因——`SelectSuggestion` → `HideSuggestions` → `SetQueryText` → posted TextChanged 进 suppress 分支 → `HintPanel.IsVisible = !ResultsPanel.IsVisible`，此刻结果未渲染 → 水印闪现到首帧渲染。修法：删掉 suppress 分支的 HintPanel 拉回——所有设置 suppress 的路径（候选/最近/chip/链接点击、Navigate）要么结果页已在屏、要么立刻起查询，拉回水印只有负作用；Enter 路径（`HideSuggestions` 不碰 HintPanel）本来就不拉回，此改与其对齐。卡上备选「选择路径直接隐藏」不取：那修不了根（suppress 分支对任何未来调用方仍是水印复活点），且两案可观测行为等价（逐路径核对过：recent 点击时水印本就可见、chip/链接/双击时结果页在屏，唯一行为变化就是空态闪现点）。
5. **候选高亮滚动入视（MainWindow.axaml:79-91）**：根因——`UpdateSuggestSelection` 只切 `Classes` 无滚动入视，MaxHeight=280 视口与 8 行高度几乎持平，字体度量偏高的平台第 8 项半裁切。修法：取卡上「计算 offset 滚动入视」而非调 MaxHeight——后者对度量变化脆弱；ScrollViewer 命名 `SuggestScroll`，新增 `ScrollSuggestionIntoView(Control)`：`TranslatePoint` 求选中项相对内容的 top/bottom，与 `Offset.Y`/`Viewport.Height` 比较后直接写 `Offset`（高亮与滚动同 tick，不用延迟的 BringIntoView 请求）；未布局（viewport≤0）时跳过，下一次方向键自愈。结果区 ScrollViewer 的 padding 坑不涉及（SuggestPanel 的 Padding 在外层 Border，ScrollViewer 自身无 Padding）。

测试过程记录：初版 Warmup 测试用 `GetAwaiter().GetResult()` 在 headless UI 线程阻塞等待线程池任务，整个 testhost 挂死（sample 采样主线程 Monitor.Wait）——改为 `async Task` + `[AvaloniaFact]`（headless xunit 支持）后 14ms 过；Core.Tests 旁证同样损坏文件场景裸跑 25ms fault，排除 SQLite 因素。Arrowing 测试两处自身算术错（↓ 按 `Count-1` 按了 7 次到不了 index 7；↑ 按了 2 次回不了顶）已修。五个修复各自做了「临时还原→红→恢复→绿」的防空过验证（见 Verification）。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/StupidDict.App/MainWindow.axaml.cs`、`MainWindow.axaml`、`Localization/Translations.cs`、`tests/StupidDict.App.Tests/HeadlessTests.cs`
- How to run/verify: `dotnet test StupidDict.slnx`
- Results: 2026-10-09 本机 macOS（arm64，.NET 10.0.101）实测 **Core.Tests 45/45 + App.Tests 136/136 全绿**（App +6：TC-001、TC-002、warmup、ShowDialog、两个 picker；此前 App 130）。`dotnet build StupidDict.slnx` 0 警告 0 错误。
- 防空过（临时还原单项修复→红→恢复→绿，全部实测）：TC-001 修复前红（HintPanel 被 suppress 分支拉回可见）；TC-002 修复前红（第 8 项 bottom 越过视口）；warmup 测试还原包裹后红（裸 warmup 抛 SQLite Error 26 'file is not a database'）；ShowDialog 测试移除守卫释放后红（Assert.Null 失败）；词典 picker 测试移除 catch 后红（InvalidOperationException "no portal" 逃出，发音包 picker 同构同缝）。
