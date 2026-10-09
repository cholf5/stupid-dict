---
id: B-009
title: 词典下载面板清理两处：删除 zip 失败误报解压失败 + 取消/失败进度条残留
type: bug
priority: P2
size: S
status: todo
created: 2026-10-09
updated: 2026-10-09
blocked: none
---

## Goal

词典下载成功路径不再被 zip 删除失败误报；取消/失败后进度条被清理，与发音包面板行为对称。

## Background

2026-10-09 扫描确认（`MainWindow.axaml.cs`，行号为当时时点）：

1. **`:556-557` `File.Delete(zipPath)` 在 `FinishDictionarySetup()` 之前**：解压全部成功后仅删 zip 失败（Windows AV/索引器短暂占用刚写完的文件）→ 落通用 catch（:568-571）显示「解压失败」，事实是解压已完成但 `_dictionaryAvailable` 未置位、服务未重建、下载面板仍挂着。修法：删除包 try-catch best-effort（残留 zip 无害——成功路径的 zip 留盘只影响下次复用校验，会通过），或把删除挪到 `FinishDictionarySetup()` 之后。
2. **`:544` `DictionaryDownloadBar.IsVisible = true` 后无任何路径再置回 false**：finally（:572-579）只恢复三个按钮。取消在 47% 时状态行写「已取消」而进度条停在 47%；失败路径同理残留。发音包侧 :788-798 的取消分支显式 `AudioPackBar.IsVisible = false`，是正确参照。

## Acceptance Criteria

- [ ] zip 删除失败不影响「已装好」的最终状态（setup 完成、面板消失、状态文案正确）
- [ ] 取消/失败后 `DictionaryDownloadBar` 隐藏
- [ ] 发音包面板既有清理行为不受影响

## Subtasks

- [ ] 词典成功路径删除 best-effort 化
- [ ] finally 补 `DictionaryDownloadBar.IsVisible = false`
- [ ] 补两条 headless 测试

## Dependencies

- none

## Test Cases

### TC-001: 删除失败仍完成 setup

Steps:
1. 注入删除失败缝（占用/只读），走词典下载成功路径

Expected:
- `FinishDictionarySetup` 正常执行，状态非「解压失败」

### TC-002: 取消后进度条隐藏

Steps:
1. 下载中取消

Expected:
- `DictionaryDownloadBar.IsVisible == false`，状态行「已取消」

## Development Log

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/StupidDict.App/MainWindow.axaml.cs`
- How to run/verify: `dotnet test StupidDict.slnx`
- Results: 未运行（待修复会话）
