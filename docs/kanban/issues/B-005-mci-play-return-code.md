---
id: B-005
title: MCI play 返回码被忽略 + Stop 的 close 失败泄漏 alias
type: bug
priority: P1
size: S
status: done
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

- [x] `play` 返回非 0 时 `Stop()` 并 `return false`（回退 TTS）
- [x] `close` 返回非 0 时不产生永久 alias 泄漏（状态位与实际一致，可重试 close）
- [x] 无任何异常外抛；既有EntryPoint/大小写声明不动

## Subtasks

- [x] `Play` 检查 play 返回码
- [x] `Stop` 按 close 返回码维护 `_open`（close 失败保留 `_open=true` 以便重试，或等价方案）
- [x] `Send` internal 化（或注入缝）供状态机单测

## Dependencies

- none

## Test Cases

### TC-001: play 失败回退

Steps:
1. 注入 `Send` 缝使 play 返回非 0
2. 调 `Play(file)`

Expected:
- 返回 false（复合链回退 TTS），alias 已 close

Result: 通过。`PlayRefusalClosesAliasAndReportsFalse`（命令序列精确断言 open→play→close、fake winmm alias 归位）+ `CompositeFallsThroughToTtsWhenMciRefusesToPlay`（复合链级：pack 拒播 → TTS 收到 ("cat", British) 且 alias 已 close）；两用例修复前实测红。

### TC-002: close 失败不静默清状态

Steps:
1. open 成功后注入 close 返回非 0
2. 调 `Stop()` 后再次 `Play`

Expected:
- 状态与 winmm 实际一致，不出现「以为关了其实没关」的永久泄漏

Result: 通过。`FailedCloseKeepsTheStateBitSoTheNextPlayRetriesTheClose`（close 失败后 fake winmm alias 仍活、下次 Play 先重试 close 成功再 open，close 恰 2 次且先于第二次 open；修复前实测红——旧码先清位，重试 close 不发生、第二次 open 被 alias 占用拒绝）+ `PersistentlyFailingCloseNeverLetsPlayClaimSuccessOnAStuckAlias`（close 持续失败时 alias 卡死 → open 被拒 → Play 返回 false 回退 TTS，绝不谎报成功；约束修法形状的钉子，新旧码均绿属预期）。

（MCI 无法 headless 真跑，状态机用注入缝测；Windows 本机手工点 UK/US 冒烟——见 AGENTS.md Windows 本地测试约束。）

## Development Log

2026-10-09 修复会话（本机 macOS，全程未触碰真实 winmm）：

- **修法①（play 返回码）**：`Play` 在 open 成功后检查 `Send($"play {Alias}")`，非 0 时 `Stop()`（立即释放刚打开的 alias）并 `return false`，复合链照常回退 SystemTtsPlayer。根因是 mciSendString 以返回码报错、从不抛异常，原实现恒 `return true`——设备忙/waveout 耗尽时用户点发音无声且 TTS 兜底被跳过。
- **修法②（close 状态位）**：`Stop()` 改为「close 返回 0 才清 `_open`」；close 失败（返回非 0）保留 `_open=true`，下次 `Play` 起手的 `Stop()` 重试 close，状态位始终与 winmm 实际一致；`Send` 抛异常（interop 绑定本身坏）走同一策略。修复前先置 `_open=false` 再 close，close 一旦失败 alias 仍活着但状态位已清——后续 Stop 全部 no-op、同名 open 永远被拒，发音包链路静默失效到重启。
- **注入缝**：`Send` 由 private static 转实例方法，新增 internal 构造注入 `Func<string, int>`（仿 B-004 internal 构造注入惯例；生产默认构造不动，P/Invoke 声明 EntryPoint/ExactSpelling/CharSet 一字未动）。平台守卫改为 `_sendOverride is null && !OperatingSystem.IsWindows()`——守卫只护真实 winmm 调用（非 Windows 生产实例照旧短路返回 false），缝实例驱动同一命令状态机打在假 MCI 上，net10.0 中立 TFM 全平台可测。
- **alias 生命周期影响评估**：成功路径 Send 序列不变（open→play；close 仅发生在下次 Play 起手或显式 Stop）；失败路径新增至多一次 close（play 拒绝后立即释放刚开的 alias）。连续播放、快速连点、UK↔US 切换语义均不变——每次点击都是完整 Play（Stop→close→open→play）；close 瞬时失败时下一次点击先重试（成功即自愈），持续失败则该次 open 被拒、点击回退 TTS，绝不谎报成功；无命令堆积（每次 Play 至多多一发 close）、无状态漂移。
- **AGENTS.md 三条 MCI 约束逐条核对**：EntryPoint/ExactSpelling 钉死未动；线程模型未动（Play 仍由 UI 线程点击回调发起，未新增任何线程切换）；失败返回 false 不外抛保持（play 拒绝 → false；所有 Send 调用点仍在 try/catch 内，Stop 的 catch 吞掉并保留重试位）。
- 邻接留痕（未顺手修）：窗口关闭时没有显式 `Stop()`——播放中的 mpegvideo 设备随进程退出由 OS 回收，属既有行为；AGENTS.md「发音（Speech/）」段建议补一句 Stop 重试语义（见 Verification），是否同步留 PM/审查决定。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/StupidDict.App/Speech/AudioFilePlayer.cs`、`tests/StupidDict.App.Tests/MciStateMachineTests.cs`（新增）
- How to run/verify: `dotnet test StupidDict.slnx`；Windows 本机 `dotnet run -f net10.0-windows` 点发音冒烟
- Results: 2026-10-09 本机 macOS——Core 45/45 + App 172/172 全绿（App +5：MciStateMachineTests 5 用例，纯 [Fact] 缝注入、无 Avalonia UI 依赖）；双 TFM 构建 0 警告 0 错误。修复前实测红：临时复原两处 bug（保留缝）后 PlayRefusalClosesAliasAndReportsFalse / CompositeFallsThroughToTtsWhenMciRefusesToPlay / FailedCloseKeepsTheStateBitSoTheNextPlayRetriesTheClose 3 条判别性用例精确失败，恢复修复后全绿（防空过）。Windows 本机手工冒烟（点 UK/US 正常播 + 设备忙场景回退 TTS）待 Windows 环境执行，本卡代码级/测试级验证已闭合。
- AGENTS.md 同步建议（未动，待审）：「发音（Speech/）」段 MCI 句后可补「Stop 仅在 close 返回 0 时清 `_open`，失败保留以便下次 Play 重试——勿简化回无条件清位」。
