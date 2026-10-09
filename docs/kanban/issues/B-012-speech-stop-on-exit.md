---
id: B-012
title: 应用退出无停播出口，孤儿音频进程继续播
type: bug
priority: P2
size: M
status: todo
created: 2026-10-09
updated: 2026-10-09
blocked: none
---

## Goal

退出应用时立即停止正在播放的发音，Unix 下不再留下把整句播完的孤儿子进程。

## Background

2026-10-09 扫描确认：

- `Speech/SpeechPlayback.cs` 全链（`CompositeSpeechPlayer`/`SystemTtsPlayer`）无 `IDisposable`/Stop 出口；`App.axaml.cs` 与 `MainWindow` 关闭路径都不停播。
- Unix：`ProcessPlayer`（:36-48）`Process.Start` 后不再管，父进程退出不杀子进程 → 关了应用 `afplay`/`say`/`espeak` 继续播完整段。
- Windows：`SpeechSynthesizer` 从不 Dispose；MCI alias 靠进程退出兜底（尚可接受但同样无主动清理）。

约束（AGENTS.md）：MCI `close` 必须在 UI 线程发起（STA）——`MainWindow.Closed`/`OnClosing` 在 UI 线程，OK；播放器一律失败返回 false 不外抛，清理代码同样要吞异常 best-effort。

## Acceptance Criteria

- [ ] 退出应用立即无声（三平台）
- [ ] 清理路径不抛异常（best-effort，不影响关窗）
- [ ] 正常播放完成语义不受影响；`ISpeechPlayer` 缝的既有测试（`RecordingSpeechPlayer`）回归绿

## Subtasks

- [ ] `ISpeechPlayer`（或 `SpeechPlayback`）加 `Stop`/`Dispose`
- [ ] `MainWindow.Closed`（或 App 生命周期 Shutdown）接入调用
- [ ] Unix `ProcessPlayer` Kill+Dispose；Windows `SpeechSynthesizer.Dispose` + MCI close
- [ ] headless 断言 + 平台手工冒烟

## Dependencies

- none

## Test Cases

### TC-001: 关窗触发停播

Steps:
1. headless 注入 `RecordingSpeechPlayer`，播放后关窗

Expected:
- Stop 被调用（记录可见）

## Development Log

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/StupidDict.App/Speech/`、`src/StupidDict.App/MainWindow.axaml.cs`
- How to run/verify: `dotnet test StupidDict.slnx`；三平台手工「播放中关窗」冒烟
- Results: 未运行（待修复会话）
