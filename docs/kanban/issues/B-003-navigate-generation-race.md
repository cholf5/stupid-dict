---
id: B-003
title: Navigate 不推进 _searchGeneration，在途查询反噬导航页
type: bug
priority: P1
size: S
status: todo
created: 2026-10-09
updated: 2026-10-09
blocked: none
---

## Goal

后退/前进导航后，先前在途查询完成时不再覆盖当前页、截断前向历史或造成搜索框与页面不一致。

## Background

2026-10-09 扫描确认（行号为当时时点）：

- `MainWindow.axaml.cs:481-487` `Navigate` 不碰 `_searchGeneration`；`RunSearch`（:377-406）在 `await` 后以 `generation != _searchGeneration` 判活（:400）。
- 失败链：输入 "hello" 回车（冷库 fuzzy 慢）→ 结果未到时按 ⌘[ 后退（页面切回上一词、搜索框回写、前向钮亮起）→ "hello" 结果到达时代数仍相等 → 页面被换回 hello、`Push` 截断刚恢复的前向历史、**搜索框显示旧词而页面是 hello**（此后回车重查的是框里的旧词）。
- 正确参照：`ShowEmptyState`（:506）正确做了 `_searchGeneration++`，唯独 `Navigate` 漏了。

## Acceptance Criteria

- [ ] Navigate 前的在途查询完成时被代数判活丢弃（不渲染、不 Push、不动按钮）
- [ ] 搜索框与当前页始终一致
- [ ] 正常后退/前进、重复查询折叠语义不受影响（`DoubleClickingHeadwordDoesNotDuplicateHistory` 等回归）

## Subtasks

- [ ] `Navigate` 中推进 `_searchGeneration`
- [ ] 补 headless 回归测试

## Dependencies

- none

## Test Cases

### TC-001: 导航后在途查询被丢弃

Steps:
1. headless 注入慢查询缝（或以可延迟的 Lookup 缝），发起查询
2. 查询完成前按 ⌘[ 后退
3. 等查询完成，`WaitUntil` 后断言页面仍为导航目标页

Expected:
- 页面/搜索框/前向历史均保持导航态，不被查询结果覆盖

## Development Log

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/StupidDict.App/MainWindow.axaml.cs`
- How to run/verify: `dotnet test StupidDict.slnx`
- Results: 未运行（待修复会话）
