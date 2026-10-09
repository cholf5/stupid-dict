---
id: B-011
title: macOS 首次 TTS 回退在 UI 线程同步跑 say -v ?（读无超时 + 孤儿进程）
type: bug
priority: P2
size: M
status: done
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

- [x] macOS 首次 TTS 回退不在 UI 线程同步等待子进程（枚举异步化/后台化，未就绪时用默认 voice 或跳过枚举——方案在会话内定夺并留痕）
- [x] 子进程读取有超时，超时分支 `Kill()`
- [x] ProxyDetector 子进程超时 Kill
- [x] Windows MCI 路径与既有发音测试不受影响

## Subtasks

- [x] MacVoices 后台化 + 缓存策略定案
- [x] 读超时 + Kill（SystemTtsPlayer）
- [x] Kill（ProxyDetector）
- [x] 测试（进程缝注入假延迟进程）

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

2026-10-09 修复会话（本机 macOS；测试不触碰真实 `say`/`scutil`，进程级用例以 /bin/sleep、/bin/sh 作替身探针）：

- **方案定案（枚举后台化 + 未就绪用默认 voice）**：`ISpeechPlayer.Play` 是同步缝（复合链首个 true 即胜出），把整条 Play 异步化会动播放链契约，不做。改为：`SystemTtsPlayer.MacVoicesNow()` 首次被调（= 首次 macOS 回退播放）以 `Task.Run` 在线程池懒跑一次 `say -v ?`，此后缓存复用；未就绪时返回 null → `VoiceFor` 返回 null → `PlayMac` 走 `say <word>`（默认 voice）——与「枚举失败 → 空表 → 默认 voice」的既有行为完全同形，只是把原先同步等的 3s 从 UI 线程挪走。缓存放进 Task 本身（跑一次定终身，含空表/超时结果），不重试重跑：超时后每次播放再起探针只会制造新进程。播放第 2 次起（用户两次点击间隔远大于探针耗时）即用上枚举表。
- **读超时 + Kill（共享化）**：两处同型问题抽成 `SubprocessOutput.ReadWithTimeout(process, timeoutMs)`（src/StupidDict.App/SubprocessOutput.cs，根命名空间，Speech 与 Assets 的共同祖先）：`ReadToEndAsync().Wait(timeout)`，超时分支 `Kill(entireProcessTree: true)`（测试实证裸 `Kill` 不够——shell 型探针的孙进程仍握着管道，read 永不完成；整树 Kill 让管道关闭、缓冲字节照常交回解析），随后 `WaitForExit` 兜底再 Kill；read 故障（管道断裂）同样 Kill；返回缓冲内容或 null，解析责任在调用方。kill 后等待与解析全程 try/catch，探针进程卫生绝不反噬播放链。
- **SystemTtsPlayer 改造**：`EnumerateMacVoices` 跑在线程池，经 `SubprocessOutput` 有界读 + 解析（解析抽 `ParseVoiceList` internal static 纯函数）；选表抽 `SelectMacVoice` internal static 纯函数（偏好列表 → 目标 locale → 另一英文 locale → null，逐条保留原语义，含「`Daniel (Enhanced)` 只能走 locale 兜底命中」的既有怪癖并用测试钉住）。并发用 `_macVoicesGate` 锁 + `??=` 保证探针至多启动一次。
- **ProxyDetector.RunScutil**：裸 `ReadToEnd()` + `WaitForExit(3000)`（无 Kill）改为 `SubprocessOutput.ReadWithTimeout(process, 3000)`，超时/挂死即整树 Kill；返回条件 `process.HasExited && ExitCode == 0` 原样保留（被 Kill 的探针 ExitCode 非 0 → 照旧视为无代理）。
- **缝**：`SystemTtsPlayer` 新增 internal 构造注入 `ProcessStarter? voiceProbeOverride + int voiceProbeTimeoutMs`（仿 B-004/B-005 internal 构造注入惯例；public 无参构造不动，`SpeechPlayback.Create` 零改动），`MacVoicesNow` internal 供测试驱动。
- **约束逐条核对**：Windows MCI 路径零触碰（PlayWindows/`#if WINDOWS` 未动，仍由 UI 线程发起）；「失败返回 false 不外抛」保持——`EnumerateMacVoices` 全程 try/catch、任务永不 fault，`MacVoicesNow` 对非 RanToCompletion 一律回 null（默认 voice）；`File.Exists("/usr/bin/say")` 守卫原位保留。
- 邻接留痕（未顺手修）：`ProcessPlayer` 的 afplay/espeak 长播放本就不受超时治理（那是播放不是探测，Kill 属 B-012 退出出口职责）；`ProxyDetector.PortOpen` 的 `ConnectAsync.Wait(300)` 是同步套接字等待，但已在线程池 attempt 线程上、300ms 有界，未动。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/StupidDict.App/Speech/SystemTtsPlayer.cs`、`src/StupidDict.App/Assets/ProxyDetector.cs`、`src/StupidDict.App/SubprocessOutput.cs`（新增）
- How to run/verify: `dotnet test StupidDict.slnx`；macOS 手工点未覆盖词的发音冒烟
- Results: 2026-10-09 会话实测。TC-001/TC-002 由 `SubprocessOutputTests`（4 条：挂死 sleep 探针 300ms 期限内被杀、`HasExited == true`；被杀探针缓冲输出照常交回；正常退出探针输出完整；静默挂死探针干净 Kill）+ `SystemTtsPlayerTests`（9 条：ParseVoiceList 表驱动 3 条、SelectMacVoice 语义 4 条含 Enhanced 变体怪癖钉子、探针缝 2 条——延迟探针首查返回 null 即「未就绪用默认 voice」契约 + 全程恰一次启动 + 就绪后缓存复用；探针起不来降级空表不抛）覆盖，`dotnet test … --filter SubprocessOutput|SystemTtsPlayerTests|ProxyDetector` 32/32 绿；全套 Core 45/45 + App 191/191 绿，`dotnet build src/StupidDict.App -f net10.0-windows` 0 警告 0 错误（MCI 路径未动，MciStateMachineTests 5/5 原样绿）。测试期实证两处：裸 `Kill()` 漏孙进程握管道（已改整树 Kill）、AmericanPreferences 首选 Samantha 非 Alex（测试预期修正，生产语义未动）。macOS 手工冒烟（挂住的 say 现场复现）留待真机：headless 不可复现 say 挂死，替身探针已覆盖同型机制。
