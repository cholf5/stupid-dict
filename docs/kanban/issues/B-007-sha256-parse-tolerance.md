---
id: B-007
title: .sha256 解析只容空格分隔，常见变体静默降级为不校验
type: bug
priority: P2
size: S
status: todo
created: 2026-10-09
updated: 2026-10-09
blocked: none
---

## Goal

`.sha256` 文件的常见格式变体（tab 分隔、openssl dgst）都能正确解析出哈希，格式完全不识别时才按「无 checksum」降级。

## Background

2026-10-09 扫描确认（`Assets/AssetDownloadService.cs:193-194`）：

- 现行解析 `text.Split(' ')[0].Trim()`，仅容 `hex name` 空格分隔。
- tab 变体（`hex\t*file`，BSD `sha256sum -t` 风格——tab 在 token 内部，Trim 剥不掉）与 `SHA256(file)= hex`（`openssl dgst`）解析出的 token 长度非 64 → 该源判为「无 checksum」→ 下游按 `expected == null` 走「跳过校验、靠逐条 CRC 兜底」。
- 后果：完整性防线**静默**失效，无任何告警区分「远端没发布 checksum」与「发布了但格式不认识」；并联动 B-008 的复用信任链。
- 已正确容错的部分（不要动坏）：CRLF、前后空白、大写十六进制、长度 ≠ 64 的拒绝。

## Acceptance Criteria

- [ ] `hex name`、`hex\t*file`、`SHA256(file)= hex`、大写 hex、CRLF 均能取出 64 位 hex
- [ ] 无 64 位 hex 的内容仍返回 null（维持「无 checksum 跳过校验」语义）
- [ ] 解析逻辑抽纯函数便于单测

## Subtasks

- [ ] 解析改正则/分词容错（首行取 `\b[0-9a-fA-F]{64}\b` 或等价）
- [ ] 抽静态纯函数
- [ ] 多格式夹具单测

## Dependencies

- none

## Test Cases

### TC-001: 多格式解析表

Steps:
1. 对 5+ 种格式夹具逐个断言解析结果

Expected:
- 表驱动全过（空格/tab/openssl/大写/CRLF/无哈希）

## Development Log

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/StupidDict.App/Assets/AssetDownloadService.cs`
- How to run/verify: `dotnet test StupidDict.slnx`
- Results: 未运行（待修复会话）
