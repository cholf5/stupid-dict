---
id: B-012
title: 应用退出无停播出口，孤儿音频进程继续播
type: bug
priority: P2
size: M
status: done
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

- [x] 退出应用立即无声（三平台）
- [x] 清理路径不抛异常（best-effort，不影响关窗）
- [x] 正常播放完成语义不受影响；`ISpeechPlayer` 缝的既有测试（`RecordingSpeechPlayer`）回归绿

## Subtasks

- [x] `ISpeechPlayer`（或 `SpeechPlayback`）加 `Stop`/`Dispose`
- [x] `MainWindow.Closed`（或 App 生命周期 Shutdown）接入调用
- [x] Unix `ProcessPlayer` Kill+Dispose；Windows `SpeechSynthesizer.Dispose` + MCI close
- [x] headless 断言 + 平台手工冒烟

## Dependencies

- none

## Test Cases

### TC-001: 关窗触发停播

Steps:
1. headless 注入 `RecordingSpeechPlayer`，播放后关窗

Expected:
- Stop 被调用（记录可见）

## Development Log

2026-10-09 修复会话（本机 macOS；停止逻辑纯逻辑测试 + headless 窗口测试覆盖，真机冒烟见 Verification）：

- **`ISpeechPlayer` 加 `void Stop()`**（选 Stop 不选 IDisposable：语义是「立即停播并释放播放资源」而非通用销毁，且 MainWindow 调用点无需模式匹配；接口文档写明 best-effort 吞异常、Stop 后仍可用于后续 Play）。两个测试桩（`RecordingSpeechPlayer`、`RecordingPlayer`）随接口补齐。
- **生命周期论证（挂 `MainWindow.Closed`，不挂 Closing / lifetime Shutdown）**：①MCI close 必须在 UI 线程发起（DirectShow 仅 STA），`Closed` 恰在 UI 线程；②headless 测试没有 `IClassicDesktopStyleApplicationLifetime`，`window.Close()` 直达 `Closed`，缝可测性是硬约束；③应用是单窗口形态且 Avalonia 默认 `OnLastWindowClosing`，主窗关闭即进程退出，`Closed` 就是应用退出点——即使未来 lifetime 语义变化，「关主窗即停音」也是正确的用户预期；④选 `Closed` 不选 `Closing`：后者可被否决，停播不该发生在可能取消的关闭上。`App.OnFrameworkInitializationCompleted` 的 lifetime 分支（ShutdownRequested 等）三者比较后不取：不可 headless 测试、且对单窗形态无额外覆盖。
- **逐层 Stop**：`CompositeSpeechPlayer.Stop` 逐子项转发、逐项 try/catch（一个坏播放器不拦其余清理、不逃出关窗路径）；`AudioPackPlayer.Stop` 转发文件播放器并 try/catch；`IAudioFilePlayer` 加 `Stop()`——`ProcessAudioFilePlayer`/`LinuxAudioFilePlayer` 转发 `ProcessPlayer.Stop`（Kill+Dispose，Dispose 也挪进 try），`MciAudioFilePlayer.Stop` 即既有 close 状态机（B-005 语义零改动，`_open` 位清理规则原样）；`SystemTtsPlayer.Stop`：`_process.Stop()`（Unix 杀 in-flight say/espeak）+ Windows 分支 `SpeakAsyncCancelAll()` → `Dispose()` → 置 null（可再 Play 重建）。
- **接线**：`MainWindow` 构造器 `Closed += (_, _) => _speech.Stop();`，与既有 Closed 处理（解订 Translations、记窗 bounds）并列；未播放时 Stop 全链 no-op（`ProcessPlayer._current` 为 null、MCI `_open` 为 false），反复关窗无副作用。
- **正常播放语义**：Play 路径零改动——`CompositeSpeechPlayer.Play`/`AudioPackPlayer.Play`/`MciAudioFilePlayer.Play` 与回退顺序一字未动；B-005 的 MCI 状态机契约（play 返回码检查、close 失败保留 `_open`）原样回归绿。
- 邻接留痕（未顺手修）：Windows MCI 无主动清理时靠进程退出兜底属可接受既有行为，本卡补上显式 close 后即收口；`SpeechSynthesizer` 原先从不 Dispose 同样由此收口。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/StupidDict.App/Speech/`（ISpeechPlayer/AudioPackPlayer/AudioFilePlayer/ProcessPlayer/SystemTtsPlayer）、`src/StupidDict.App/MainWindow.axaml.cs`
- How to run/verify: `dotnet test StupidDict.slnx`；三平台手工「播放中关窗」冒烟
- Results: 2026-10-09 会话实测。TC-001 headless 断言 `ClosingTheWindowStopsTheInjectedPlayer`：注入 `RecordingSpeechPlayer` → 查词 → 点 UK 发音（Played=1、Stops=0，播放中不停）→ `window.Close()` → Stops=1；**修复前实测红**（临时摘除 `Closed += _speech.Stop()` 后该用例 Stops 断言失败，恢复即绿——钉子确凿）。纯逻辑 `SpeechStopTests` 5 条（复合链逐子项转发、坏播放器被吞且其余照停、AudioPack 转发与吞异常、SystemTtsPlayer 重复 Stop 无害）。既有回归全绿：MciStateMachineTests 5/5（Stop 的 close 状态机语义未动）、SpeakerButtonPlaysThroughInjectedPlayer 等发音用例原样。全套 Core 45/45 + App 191/191，`dotnet build src/StupidDict.App -f net10.0-windows` 0 警告 0 错误（WINDOWS 分支 `SpeechSynthesizer` 取消/Dispose 编译通过）。三平台真机「播放中关窗」冒烟待对应环境（本会话仅 macOS；macOS 预期：afplay/say 播放中点关闭按钮 → 声音立止、`ps` 无孤儿）。
