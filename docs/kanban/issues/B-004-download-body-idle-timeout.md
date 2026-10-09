---
id: B-004
title: 下载 body 无空闲超时，服务器停发即永久挂起
type: bug
priority: P1
size: S
status: todo
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

- [ ] body 停发在超时后进入下一 attempt 而非永久挂起
- [ ] 用户取消语义不变（上抛，不误吞）
- [ ] 正常慢速下载（有字节流）不受空闲超时影响；断点续传行为不变

## Subtasks

- [ ] 读循环加 per-read 空闲超时（linked CTS，超时后 Dispose 恢复）
- [ ] 区分超时 OCE 与用户取消 OCE
- [ ] 补 ScriptedDownloader 层测试

## Dependencies

- none

## Test Cases

### TC-001: 停发流超时进回退链

Steps:
1. 注入前 N 字节后永久 stall 的流（ScriptedDownloader/自造 Stream）
2. 空闲超时设短（测试常量），跑 `DownloadAsync`

Expected:
- 超时抛出且 attempt 链推进到下一源；`.part` 保留可续传

### TC-002: 用户取消不被超时逻辑误吞

Steps:
1. 下载中触发用户取消令牌

Expected:
- OCE 原样上抛，不进入 fallback

## Development Log

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/StupidDict.App/Assets/AssetDownloadService.cs`
- How to run/verify: `dotnet test StupidDict.slnx`
- Results: 未运行（待修复会话）
