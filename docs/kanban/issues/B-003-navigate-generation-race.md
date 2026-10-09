---
id: B-003
title: Navigate 不推进 _searchGeneration，在途查询反噬导航页
type: bug
priority: P1
size: S
status: done
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

- [x] Navigate 前的在途查询完成时被代数判活丢弃（不渲染、不 Push、不动按钮）
- [x] 搜索框与当前页始终一致
- [x] 正常后退/前进、重复查询折叠语义不受影响（`DoubleClickingHeadwordDoesNotDuplicateHistory` 等回归）

## Subtasks

- [x] `Navigate` 中推进 `_searchGeneration`
- [x] 补 headless 回归测试

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

### 根因确认

`MainWindow.Navigate` 渲染导航页、回写搜索框、更新按钮，但全程不推进 `_searchGeneration`；而 `RunSearch` 在 `await _service.LookupAsync` 之后以 `generation != _searchGeneration` 判活。于是在途查询跨过一次导航完成时，代数仍相等，迟到结果走完 `RenderResult → _navigator.Push → UpdateNavButtons` 全链：覆盖刚导航到的页面、截断 `GoBack` 刚恢复的前向历史，且搜索框还停在导航词上（此后回车重查的是框里的旧词）。`ShowEmptyState` 早已做 `_searchGeneration++`，唯独 `Navigate` 漏了——修法即把导航纳入同一代数契约，不削弱「新查询使旧结果作废」的既有语义。

### 修法

- `Navigate`（`src/StupidDict.App/MainWindow.axaml.cs`）：入口处推进 `_searchGeneration`（null 早退不动代数——页面没切换就无需作废任何东西）。查询语义零改动：`LookupAsync`/Tier/整页缓存导航/后退不写最近搜索/同页折叠全部不经此路径，Core 侧未动。
- 测试缝：`DictionaryService` 是 `sealed` 类且 `LookupAsync` 非 virtual，测试无法注入慢查询；按卡面许可加了 `internal Func<string, Task<LookupResult>>? LookupOverride`（`RunSearch` 里 `LookupOverride?.Invoke(query) ?? _service.LookupAsync(query)`），生产路径为 null 时行为与原先逐字节一致。

### 回归测试（TC-001）

`InFlightLookupDoesNotClobberNavigatedPage`（`tests/StupidDict.App.Tests/HeadlessTests.cs`），确定性不靠 Sleep 碰运气：

- `LookupOverride` 指向 `TaskCompletionSource<LookupResult>`（`RunContinuationsAsynchronously`）把查询停在半空，完成时机由测试显式 `SetResult` 控制；续接经 Avalonia SyncContext 落回 UI 线程，`RunJobs` 泵走——与本 harness 所有异步结果的送达机制一致。
- 三段式：①投递对照——同缝先投递一次不被导航打断的迟到结果，证明「缝送达」机制本身工作，排除负面断言空过；②竞态——"hello" 在途时 ⌘[ 后退，再 `SetResult`，断言页面/搜索框/前向钮/`ResultRendered` 计数全部保持导航态；③管线存活——drop 之后再发真实查询照常送达，代数作废没有误伤后续查询。

### 验证用例有效性反证

临时注释 `Navigate` 的 `_searchGeneration++` 重跑该测试：失败于 `Assert.Equal("catch", Actual "cat")`（在途结果把导航页覆盖回去了），随后恢复——测试确实钉住本 bug，不是恒绿。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/StupidDict.App/MainWindow.axaml.cs`
- How to run/verify: `dotnet test StupidDict.slnx`
- Results:
  - `dotnet build StupidDict.slnx`：0 Warning / 0 Error（`TreatWarningsAsErrors` 下干净）。
  - 修复态 `dotnet test StupidDict.slnx`：Core.Tests 45/45 通过；App.Tests 130/130 通过（含新增 `InFlightLookupDoesNotClobberNavigatedPage`，原 129 + 1）。
  - 反证态（去掉代数推进）：新测试失败（页面被在途结果覆盖，Expected "catch" / Actual "cat"），已恢复。
