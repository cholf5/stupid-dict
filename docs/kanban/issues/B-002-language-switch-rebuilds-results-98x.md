---
id: B-002
title: 切语言一次触发约 98 次结果页全量重建（UI 冻结）
type: bug
priority: P1
size: S
status: done
created: 2026-10-09
updated: 2026-10-09
blocked: none
---

## Goal

语言切换时代码渲染的结果页只重建一次（现为约 98 次），设置窗口的语言联动同样收敛。

## Background

2026-10-09 扫描确认：

- `Localization/Translations.cs:246-247`：`SetLanguage` 对 `AllPropertyNames`（97 个 `public string` 属性 + `CurrentLanguage` ≈ 98 个）逐一**同步** raise PropertyChanged。
- `MainWindow.axaml.cs:434-439`：`OnTranslationsChanged` 不看 `e.PropertyName`，每次 raise 都执行 `UpdateSearchBoxLineMetrics()` + `if (ResultsPanel.IsVisible) _rebuildResults?.Invoke()` → 结果页可见时**同步全量重建 ~98 遍**（每遍 1–5ms，厚页合计数百毫秒可感卡顿）。
- `SettingsWindow.axaml.cs:94-105`：`OnTranslationsPropertyChanged` 同型——每次 raise 6 次 Label 赋值 + `RefreshDataStatus()`（2 次磁盘 stat）。
- 正确参照：`App.axaml.cs:23-27` 的订阅按 `e.PropertyName == nameof(Translations.AppName)` 过滤。

约束（AGENTS.md）：XAML 绑定靠逐属性 raise 即时刷新，**这条机制不动**；瞬态状态行不回溯刷新是既定语义，勿顺手改。

## Acceptance Criteria

- [x] 结果页可见时切语言，`_rebuildResults` 只执行一次（TC-001；实测修复前 98 次 → 修复后 1 次）
- [x] SettingsWindow 的选项 Label/数据状态联动每类只做一次（OptionItem.Label 计数：一次切换恰 6 次赋值，修复前 588；RefreshDataStatus 同处理器收敛 98 → 1）
- [x] 既有语言/主题测试全绿（含 `LanguageSwitchRefreshesSettingsComboOptionsAndKeepsSelection`）

## Subtasks

- [x] MainWindow 订阅回调按 PropertyName 过滤（落地为按 `nameof(Translations.CurrentLanguage)` 单拍收敛，选型理由见 Dev Log）
- [x] SettingsWindow 同型收敛
- [x] 回归既有测试

## Dependencies

- none

## Test Cases

### TC-001: 重建次数收敛

Steps:
1. headless 下渲染结果页，`SetLanguage(English)`
2. 以可观测副作用计数（如某个由 `_rebuildResults` 重挂载的控件的重建计数，或注入计数缝）

Expected:
- 重建恰好 1 次（修复前 ~98 次）

Result: ✅ `LanguageSwitchRebuildsResultPageExactlyOnce`——修复前失败（**Actual: 98**）、修复后通过（恰 1 次），来回两切各 +1 一并钉住；配套 `LanguageSwitchOnEmptyStateDoesNotRebuildResults` 钉空态零重建（`ResultsPanel.IsVisible` 门）与 HintText 绑定仍即时换语言。

## Development Log

### 2026-10-09

- 先立计数缝后修（TC-001 允许「注入计数缝」）：`MainWindow` 加 internal `ResultRendered`（`RenderResult`/`RenderError` 末尾各触发一次 = 重建闭包每执行一次的可观测副作用，生产恒为 null 零成本）。修复前实测一次 `SetLanguage`：结果页重建 **98** 次（= 97 个 public string 属性 + CurrentLanguage，与卡上 ≈98 吻合）；SettingsWindow 联动跑 98 遍 = **588** 次 Label 赋值（6×98）+ 196 次磁盘 stat。
- 修法（订阅侧，`Translations.SetLanguage` 逐属性 raise 一字未动）：两个处理器都加 `e.PropertyName != nameof(Translations.CurrentLanguage)` 早退。选 `CurrentLanguage` 而非照抄 App.axaml.cs 的 `AppName` 过滤：它是风暴里语义上唯一表示「生效语言变了」的属性（public instance 属性，被 `AllPropertyNames` 反射扫进 raise；`SetLanguage` 在任何 raise 前已写完 `_strings`/`CurrentLanguage`，故该拍上一切取值已一致），不押注某个具体字符串属性永远存在；`AppName` 之所以是 App.axaml.cs 的正确参照，是因为它消费的本来就是 AppName。瞬态状态行不回溯刷新的既定语义未触碰（状态行本来就不经这两个处理器刷新）。
- 测试 +3（`tests/StupidDict.App.Tests/HeadlessTests.cs`，语言钉 zh、切过还原的惯例照旧）：`LanguageSwitchRebuildsResultPageExactlyOnce`（TC-001，来回两切各恰 +1，并断言页面确实换英文/词头仍在）；`LanguageSwitchOnEmptyStateDoesNotRebuildResults`（空态零重建 + HintText 绑定即时换英文——同时证明逐属性 raise 机制完好）；`SettingsWindowLanguageSwitchConsolidatesRebindWork`（走真实链路 combo → WireSettings → SetLanguage，以 `OptionItem.Label` 的 INPC raise 做生产可观测计数：一次切换恰 6 次 = 选项 Label 各赋值一次；数据状态行翻成 "Not installed"——RefreshDataStatus 与 Label 同在一个收敛处理器内，6 次计数同时证明它只跑 1 次）。前两个对修复前代码失败（98/588 咬合确认），修复后全绿。
- 既有回归全绿：`LanguageSwitchRefreshesSettingsComboOptionsAndKeepsSelection`、`LanguageSwitchLiveRetitlesAndRerendersResults`、`SearchBoxLineHeightFollowsWatermarkScript`（依赖 `UpdateSearchBoxLineMetrics` 切换后仍执行——现每拍 1 次而非 98 次）、`ThemeSwitchAppliesVariantAndReRendersResults`。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/StupidDict.App/MainWindow.axaml.cs`、`src/StupidDict.App/Settings/SettingsWindow.axaml.cs`、`tests/StupidDict.App.Tests/HeadlessTests.cs`（`src/StupidDict.App/Localization/Translations.cs` 零改动）
- How to run/verify: `dotnet test StupidDict.slnx`（App 测试走 AvaloniaHeadless，断言中文需显式钉语言）
- Results:
  - 修复前（仅加计数缝、未加过滤）跑三个新测试：`LanguageSwitchRebuildsResultPageExactlyOnce` **Actual: 98**（期望 1）、`SettingsWindowLanguageSwitchConsolidatesRebindWork` **Actual: 588**（期望 6）；空态测试修复前即通过（IsVisible 门本就存在）。
  - 修复后全套 `dotnet test StupidDict.slnx`：**Core 45/45 + App 129/129**（126 + 3 新增）全绿，含 `LanguageSwitchRefreshesSettingsComboOptionsAndKeepsSelection`。
  - 一次语言切换的副作用计数（修复前 → 修复后）：结果页重建 98 → 1；SettingsWindow 选项 Label 重赋 588 → 6；`RefreshDataStatus` 98 → 1（磁盘 stat 196 → 2）。
