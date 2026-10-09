---
id: B-011
title: macOS 首次 TTS 回退在 UI 线程同步跑 say -v ?（读无超时 + 孤儿进程）
type: bug
priority: P2
size: M
status: todo
created: 2026-10-09
updated: 2026-10-09
blocked: none
---

## Goal

macOS 首次发音回退不再阻塞 UI 线程，子进程读取有超时、超时有 Kill；ProxyDetector 的同型问题一并治理。

## Background

2026-10-09 扫描确认：

- `Speech/SystemTtsPlayer.cs:86-117` `MacVoices()`：首次调用同步 `Process.Start("/usr/bin/say", "-v ?")` + `ReadToEnd()`（**无超时**，say 挂住则 UI 永久冻结）+ `WaitForExit(3000)`（超时分支不 `Kill()`，孤儿残留）。触发链：`PlayWord`（async void，UI 线程）→ 复合链回退 `SystemTtsPlayer.PlayMac` → `VoiceFor` → `MacVoices`，全程 UI 线程；`File.Exists("/usr/bin/say")` 挡不住「存在但挂住」。缓存后仅首次。
- `Assets/ProxyDetector.cs:170-174` 同型（`ReadToEnd` 无超时 + 不 Kill）；该路径经上轮 ConfigureAwait 修复已落线程池（挂的是下载 attempt 线程，风险较低），但子进程卫生同样缺失。

约束（AGENTS.md）：Windows MCI 必须仍从 UI 线程发起（DirectShow 仅 STA）——修 macOS 线程亲和性时勿动 Windows 侧调用路径；播放器失败一律 `return false` 不外抛。

## Acceptance Criteria

- [ ] macOS 首次 TTS 回退不在 UI 线程同步等待子进程（枚举异步化/后台化，未就绪时用默认 voice 或跳过枚举——方案在会话内定夺并留痕）
- [ ] 子进程读取有超时，超时分支 `Kill()`
- [ ] ProxyDetector 子进程超时 Kill
- [ ] Windows MCI 路径与既有发音测试不受影响

## Subtasks

- [ ] MacVoices 后台化 + 缓存策略定案
- [ ] 读超时 + Kill（SystemTtsPlayer）
- [ ] Kill（ProxyDetector）
- [ ] 测试（进程缝注入假延迟进程）

## Dependencies

- none

## Test Cases

### TC-001: 挂住子进程不冻结

Steps:
1. 注入假 say 进程（stdout 不关闭）
2. 触发 TTS 回退

Expected:
- 超时后返回默认 voice（或 false 回退），无无限等待

### TC-002: 超时后无孤儿

Steps:
1. 同上

Expected:
- 子进程被 Kill（HasExited == true）

## Development Log

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/StupidDict.App/Speech/SystemTtsPlayer.cs`、`src/StupidDict.App/Assets/ProxyDetector.cs`
- How to run/verify: `dotnet test StupidDict.slnx`；macOS 手工点未覆盖词的发音冒烟
- Results: 未运行（待修复会话）
