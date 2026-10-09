---
id: B-005
title: MCI play 返回码被忽略 + Stop 的 close 失败泄漏 alias
type: bug
priority: P1
size: S
status: todo
created: 2026-10-09
updated: 2026-10-09
blocked: none
---

## Goal

Windows MCI 播放失败时正确回退系统 TTS，close 失败不造成 alias 永久泄漏。

## Background

2026-10-09 扫描确认（`Speech/AudioFilePlayer.cs`，行号为当时时点）：

- `:66`：`Send($"play {Alias}")` 返回码丢弃、恒 `return true` → `CompositeSpeechPlayer` 认为成功、不回退 `SystemTtsPlayer` → 设备忙/waveout 耗尽时用户点发音无声。
- `:85-87`：`Stop()` 先置 `_open = false` 再发 `close`；`mciSendString` 以返回码报错（非异常，catch 接不到），close 失败时 alias 在 winmm 中未释放且状态位已清 → 下次同名 `open` 失败 → 发音包链路静默失效到重启。

约束（AGENTS.md，勿违反）：
- 发音必须在 UI 线程发起（`type mpegvideo` 底层 DirectShow 仅 STA）；
- 播放器失败一律 `return false` 不允许外抛；
- winmm P/Invoke 必须 `EntryPoint`/`ExactSpelling` 钉死 `mciSendStringW`。

## Acceptance Criteria

- [ ] `play` 返回非 0 时 `Stop()` 并 `return false`（回退 TTS）
- [ ] `close` 返回非 0 时不产生永久 alias 泄漏（状态位与实际一致，可重试 close）
- [ ] 无任何异常外抛；既有EntryPoint/大小写声明不动

## Subtasks

- [ ] `Play` 检查 play 返回码
- [ ] `Stop` 按 close 返回码维护 `_open`（close 失败保留 `_open=true` 以便重试，或等价方案）
- [ ] `Send` internal 化（或注入缝）供状态机单测

## Dependencies

- none

## Test Cases

### TC-001: play 失败回退

Steps:
1. 注入 `Send` 缝使 play 返回非 0
2. 调 `Play(file)`

Expected:
- 返回 false（复合链回退 TTS），alias 已 close

### TC-002: close 失败不静默清状态

Steps:
1. open 成功后注入 close 返回非 0
2. 调 `Stop()` 后再次 `Play`

Expected:
- 状态与 winmm 实际一致，不出现「以为关了其实没关」的永久泄漏

（MCI 无法 headless 真跑，状态机用注入缝测；Windows 本机手工点 UK/US 冒烟——见 AGENTS.md Windows 本地测试约束。）

## Development Log

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/StupidDict.App/Speech/AudioFilePlayer.cs`
- How to run/verify: `dotnet test StupidDict.slnx`；Windows 本机 `dotnet run -f net10.0-windows` 点发音冒烟
- Results: 未运行（待修复会话）
