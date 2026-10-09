---
id: B-006
title: Range 续传无 If-Range/Content-Range 校验，换包可拼出损坏 zip
type: bug
priority: P2
size: S
status: todo
created: 2026-10-09
updated: 2026-10-09
blocked: none
---

## Goal

跨版本的断点续传不再产出拼接损坏的 zip：服务器返回的 206 必须验证起始偏移，不符则丢弃 `.part` 从零下载。

## Background

2026-10-09 扫描确认（`Assets/AssetDownloadService.cs:146-159`）：

- `resumeFrom > 0` 时带 `Range` 头，收到 206 即按「服务器内容与 `.part` 同源」续接（`append = statusCode == PartialContent && resumeFrom > 0`），未带 `If-Range`/ETag，也未比对 `Content-Range` 首字节是否等于 `resumeFrom`。
- 仓库已有「远程换包」真实前例（data-1 → data-2 语义）。残留 `.part` + 远端已换包 → 206 把两版字节拼在一起，成品 zip 损坏。
- 下游缓解：SHA-256 校验能捕获并 purge 重下（MainWindow 侧 purge-once 自愈）；但 checksum 拉不到时（离线、或 B-007 的格式不识别）损坏 zip 直接落盘，并接入 B-008 的复用通道。

## Acceptance Criteria

- [ ] 206 的 `Content-Range` 首字节偏移 ≠ `resumeFrom` 时，删 `.part` 从零重下（或视作该源失败进回退链）
- [ ] 正常 206 续传、200 忽略 Range、416 清 `.part` 三个既有路径行为不变

## Subtasks

- [ ] 解析 `Content-Range` 并校验起始偏移
- [ ] 不符路径的处理与测试
- [ ] （评估，可缓）If-Range/ETag 缓存——远端 checksum 通常已兜，留痕决定做/不做

## Dependencies

- none（与 B-007、B-008 同链路但可独立修）

## Test Cases

### TC-001: 错位 Content-Range 触发重下

Steps:
1. ScriptedDownloader 返回起始偏移 ≠ resumeFrom 的 206
2. 跑续传路径

Expected:
- `.part` 被丢弃，从零重下（或进回退链），最终产物完整

### TC-002: 正常续传回归

Steps:
1. 既有续传测试全量回归

Expected:
- 全绿

## Development Log

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/StupidDict.App/Assets/AssetDownloadService.cs`
- How to run/verify: `dotnet test StupidDict.slnx`
- Results: 未运行（待修复会话）
