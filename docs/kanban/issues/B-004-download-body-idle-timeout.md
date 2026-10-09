---
id: B-004
title: 下载 body 无空闲超时，服务器停发即永久挂起
type: bug
priority: P1
size: S
status: done
created: 2026-10-09
updated: 2026-10-09
blocked: none
---

## Goal

下载传输中途停发（连接在但不发字节）时，在有限时间内超时并走既有回退链，而不是永久挂在旧进度。

## Background

2026-10-09 扫描确认：

- `Assets/AssetDownloadService.cs:143-149`：15 秒 `firstByte` CTS 只传给 `SendAsync`，覆盖到响应头为止；`ResponseHeadersRead` 下 `HttpClient.Timeout` 不治理 body 流（已对照 dotnet/runtime 源码确认）。
- 读循环 `:169-174` 的 `ReadAsync`/`WriteAsync` 只挂用户取消令牌。
- 失败场景：移动网络切换、代理进程死亡、镜像半死 → 下载永久停在旧进度，无错误无超时，只有手动取消能解锁。
- 修法方向：读循环换 linked CTS 加空闲超时（如 60 秒无字节抛出）；超时异常需与用户取消区分——用户取消上抛（参照现有 `catch (OperationCanceledException) when (cancellation.IsCancellationRequested)` 语义），空闲超时进 attempt 回退链（续传可恢复）。

## Acceptance Criteria

- [x] body 停发在超时后进入下一 attempt 而非永久挂起
- [x] 用户取消语义不变（上抛，不误吞）
- [x] 正常慢速下载（有字节流）不受空闲超时影响；断点续传行为不变

## Subtasks

- [x] 读循环加 per-read 空闲超时（linked CTS，超时后 Dispose 恢复）
- [x] 区分超时 OCE 与用户取消 OCE
- [x] 补 ScriptedDownloader 层测试

## Dependencies

- none

## Test Cases

### TC-001: 停发流超时进回退链

Steps:
1. 注入前 N 字节后永久 stall 的流（ScriptedDownloader/自造 Stream）
2. 空闲超时设短（测试常量），跑 `DownloadAsync`

Expected:
- 超时抛出且 attempt 链推进到下一源；`.part` 保留可续传

Result: 通过（`StalledBodyTimesOutAndFallsThroughToNextSource`）。自造 `StalledStream`（前 4 字节后 `Task.Delay(Infinite, token)`，语义与 SocketsHttpHandler 半死连接一致）注入经 `HttpMessageHandler` 缝；空闲窗 200ms；断言第 2 源请求带 `Range: bytes=4-`（`.part` 保留续传）、成品为完整字节、无 `.part` 残留。

### TC-002: 用户取消不被超时逻辑误吞

Steps:
1. 下载中触发用户取消令牌

Expected:
- OCE 原样上抛，不进入 fallback

Result: 通过（`UserCancellationDuringStalledBodySurfacesAsCancellation`）。TCS 等到读循环真实停滞后再 `Cancel()`（确定性落在 read 内而非 headers 上），断言 `OperationCanceledException`（TaskCanceledException 子类）上抛且仅 1 次请求（未走回退链）；该测试的空闲窗设 1 小时，证明确为用户取消而非超时。

## Development Log

根因（与卡面一致）：`FirstByteTimeout` CTS 只传给 `SendAsync`（覆盖到响应头为止），`ResponseHeadersRead` 下 body 流不受任何超时治理；读循环只挂用户令牌。修法（`AssetDownloadService.DownloadFromAsync`）：

- 读循环每次 `ReadAsync` 挂 linked CTS（`CancelAfter(60s)`，每次迭代 `using` 即超时后 Dispose 恢复）；默认常量 `BodyIdleTimeout = 60s`。
- 区分两类 OCE：`catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)` 把空闲超时转成 `HttpRequestException`（普通 attempt 失败 → `DownloadAsync` 既有 catch-all 进回退链）；用户取消时过滤器不匹配、OCE 原样上抛，`DownloadAsync` 顶部 `catch … when (cancellation.IsCancellationRequested) { throw; }` 语义不动。超时异常是纯诊断消息（所有源耗尽后用户仍见 `AllSourcesFailed`），故未进 Translations 词池。
- 测试缝：新增 `internal AssetDownloadService(Func<HttpMessageHandler>? handlerFactory, TimeSpan? bodyIdleTimeout)`，仿 `UpdateChecker(HttpMessageHandler, …)` 惯例注入脚本化 handler（代理路由对注入 handler 不生效，生产默认构造一字未动）；超时可注入保证测试确定性。生产读循环里 `WriteAsync` 仍只挂用户令牌（卡面只要求 body 读空闲）。
- 正常慢速下载不受影响：超时只在「一次 Read 都没等到字节」时触发；有字节流就不断续窗。断点续传行为由 TC-001 的 Range 断言与 B-006 的对齐回归共同钉住。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/StupidDict.App/Assets/AssetDownloadService.cs`
- How to run/verify: `dotnet test StupidDict.slnx`
- Results: 2026-10-09 全套通过——Core 45/45，App 164/164（最终计数，含审查收尾的降级测试）（含本卡新增 2 条 + 回归），build 0 警告 0 错误（net10.0 与 net10.0-windows 双 TFM）。
