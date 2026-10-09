---
id: B-014
title: 单实例监听端 ReadLine 无超时，异常本地客户端可废 activate 通道
type: bug
priority: P3
size: S
status: done
created: 2026-10-09
updated: 2026-10-09
blocked: none
---

## Goal

监听端对连上后不发数据的本地连接限时断开，本会话内后续 `activate` 信号不失效。

## Background

2026-10-09 扫描确认（`SingleInstance.cs:173-179`）：

- `WaitForConnectionAsync` 返回后 `reader.ReadLine()` 无限等待——本地某进程连上管道后既不写也不关，listener 线程永久阻塞在该连接，本会话内后续所有二次启动的 activate 信号全部落空（二次启动静默退出、窗口不前置）。
- 单实例互斥本身不受影响（靠锁文件）；仅本地进程可触发，正常双击连上即写一行。

## Acceptance Criteria

- [x] 连接后限时（如 5s）不发送 `activate` 行即断开，listener 继续服务下一连接
- [x] 正常二次启动 activate 路径不变；macOS 管道名长度约束（Id 23 字符）不动

## Subtasks

- [x] `ReadLine` 换 `ReadLineAsync` + CTS 超时（或等价）
- [x] 超时连接 Dispose 后循环继续
- [x] 测试：用既有 internal 重载注入随机锁路径/短管道名

## Dependencies

- none

## Test Cases

### TC-001: 静默连接不废通道

Steps:
1. 客户端连接管道但不发送任何数据
2. 等超时（测试注入短超时）后再发一次正常 `activate`

Expected:
- 第二次 activate 成功送达（窗口前置信号触发）

## Development Log

- 超时值论证（2026-10-09）：activate 是本地进程间通知，合法客户端只有本应用二次启动——`NotifyRunningInstance` 是 Connect(100-150ms) → WriteLine 一行 → dispose，毫秒级送达。5s（卡面建议值）比任何真实送达慢几个数量级，同时把「静默/卡死本地连接占住单槽位管道」的窗口封顶。方向权衡：过短会误杀合法但极慢的二次启动（丢的只是前置 courtesy，二次启动照旧退出——但窗口不前置看起来像静默无响应）；过长则拉长 DoS 窗口。取 5s。
- 修法：`Listen` 里 `WaitForConnectionAsync` 之后挂 linked CTS（`CreateLinkedTokenSource(_cancellation.Token)`）+ `CancelAfter(ReadLineTimeout)`，`ReadLine` 换 `StreamReader.ReadLineAsync(token).GetAwaiter().GetResult()`。CTS 在 WaitForConnection 之后才 arm——计时从「客户端拿到话筒」起算，不含管道空闲等待期。新增 `internal TimeSpan ReadLineTimeout { get; set; } = 5s` 作测试缝（生产路径不动，测试注入 300ms）。
- 取消语义区分（修法要害）：原 `catch (OperationCanceledException) { return; }` 会把超时 OCE 一并吞成「监听线程退出」——超时反而永久废通道，比原 bug 更糟。改为 `when (_cancellation.IsCancellationRequested)` 过滤：shutdown OCE 走 return（既有语义不变），超时 OCE 落进新增的专用空 catch（注释留痕）→ finally `server.Dispose()` → 循环继续。Dispose 关停路径不变（token 取消 + `_connected` 双保险，join 语义原样）。
- 超时后清理：finally 原样 Dispose（ReadLineAsync 已因取消完成，无悬挂任务），循环顶 `TryCreateServer` 新建实例；`NotifyRunningInstance` 的 10 次重试（100-150ms 间隔）天然覆盖「旧实例刚断、新实例刚建」的竞窗。锁认领/必退出/fail-open 语义逐条核对未动；`Id`/`ComputeId`/`PipeName`（23 字符）一字未改，测试沿用 `UniqueName()` 短名。
- 测试（SingleInstanceTests +1）：`SilentClientConnectionDoesNotPoisonTheActivationChannel`——客户端连接后静默握住 500ms（跨过注入的 300ms 超时；固定停留时长是测试本体——连接必须哑过 deadline，不是等条件），松手后 `NotifyRunningInstance` 断言 `ActivationRequested` 触发。红检：临时把 `CancelAfter` 改 `Timeout.InfiniteTimeSpan`（等价还原原 bug），测试红（Notify 10 次重试全连不上 → WaitUntil 3s 超时）；恢复后连跑 3 轮绿。既有 5 条单实例测试（含 `NotifyRunningInstanceRaisesActivation` 正常路径、`SecondAcquireIsRejectedWhileFirstHolds`、目录抢占 fail-open）原样绿。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/StupidDict.App/SingleInstance.cs`、`tests/StupidDict.App.Tests/SingleInstanceTests.cs`
- How to run/verify: `dotnet test StupidDict.slnx`（App.Tests `SingleInstanceTests`；测试短管道名符合 macOS socket 104 字节预算）
- Results: 2026-10-09 全套 Core 46/46 + App 195/195 全绿；红检（超时改无限）1 红、修复后连跑 3 轮绿；双 TFM（net10.0 / net10.0-windows）构建 0 警告 0 错误。真机人工项：真实双开冒烟（二次启动窗口前置）为既有行为回归，本会话无 GUI 会话未冒烟（headless 用例已覆盖通知→激活全链）。
