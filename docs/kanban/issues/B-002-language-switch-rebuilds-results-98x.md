---
id: B-002
title: 切语言一次触发约 98 次结果页全量重建（UI 冻结）
type: bug
priority: P1
size: S
status: todo
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

- [ ] 结果页可见时切语言，`_rebuildResults` 只执行一次
- [ ] SettingsWindow 的选项 Label/数据状态联动每类只做一次
- [ ] 既有语言/主题测试全绿（含 `LanguageSwitchRefreshesSettingsComboOptionsAndKeepsSelection`）

## Subtasks

- [ ] MainWindow 订阅回调按 PropertyName 过滤（或等价：语言真正变化时只重放一次）
- [ ] SettingsWindow 同型收敛
- [ ] 回归既有测试

## Dependencies

- none

## Test Cases

### TC-001: 重建次数收敛

Steps:
1. headless 下渲染结果页，`SetLanguage(English)`
2. 以可观测副作用计数（如某个由 `_rebuildResults` 重挂载的控件的重建计数，或注入计数缝）

Expected:
- 重建恰好 1 次（修复前 ~98 次）

## Development Log

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/StupidDict.App/Localization/Translations.cs`、`src/StupidDict.App/MainWindow.axaml.cs`、`src/StupidDict.App/Settings/SettingsWindow.axaml.cs`
- How to run/verify: `dotnet test StupidDict.slnx`（App 测试走 AvaloniaHeadless，断言中文需显式钉语言）
- Results: 未运行（待修复会话）
