---
id: Q-001
title: MainWindow 杂项健壮性五小项（未观察任务/丢弃任务/裸 await/UI 闪烁/滚动入视）
type: chore
priority: P2
size: M
status: todo
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

- [ ] 五项各自落地且互不回归（1-3 是健壮性，4-5 是体验）
- [ ] `dotnet test StupidDict.slnx` 全绿（App 126+）

## Subtasks

- [ ] WarmupAsync 异常观察
- [ ] ShowDialog 任务兜底
- [ ] 两个 OpenFilePickerAsync 进 try
- [ ] 候选选中不闪空态
- [ ] 候选高亮滚动入视

## Dependencies

- none

## Test Cases

### TC-001: 候选选中不闪空态

Steps:
1. headless 打开候选面板，`WaitUntil` 断言 HintPanel 隐藏
2. 点选候选

Expected:
- HintPanel 不再被 suppress 分支拉回可见

### TC-002: 第 8 项高亮可见

Steps:
1. 构造 8 条候选，键盘 ↓ 到第 8 项

Expected:
- 第 8 项在视口内（布局几何断言）

（1-3 项异常路径各补一条注入缝测试或显式留痕不做。）

## Development Log

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/StupidDict.App/MainWindow.axaml.cs`、`MainWindow.axaml`
- How to run/verify: `dotnet test StupidDict.slnx`
- Results: 未运行（待修复会话）
