---
id: B-007
title: .sha256 解析只容空格分隔，常见变体静默降级为不校验
type: bug
priority: P2
size: S
status: done
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

- [x] `hex name`、`hex\t*file`、`SHA256(file)= hex`、大写 hex、CRLF 均能取出 64 位 hex
- [x] 无 64 位 hex 的内容仍返回 null（维持「无 checksum 跳过校验」语义）
- [x] 解析逻辑抽纯函数便于单测

## Subtasks

- [x] 解析改正则/分词容错（首行取 `\b[0-9a-fA-F]{64}\b` 或等价）
- [x] 抽静态纯函数
- [x] 多格式夹具单测

## Dependencies

- none

## Test Cases

### TC-001: 多格式解析表

Steps:
1. 对 5+ 种格式夹具逐个断言解析结果

Expected:
- 表驱动全过（空格/tab/openssl/大写/CRLF/无哈希）

Result: 通过。`ParseChecksumReadsCommonPayloadShapes` 七种形态（sha256sum 双空格、单空格、BSD tab+CRLF、openssl dgst `SHA256(file)= hex`、二进制模式 `*file` 前缀、首部空行 CRLF、裸哈希）+ `ParseChecksumLowersHexCase`（大写归一小写，与旧行为一致）；`ParseChecksumReturnsNullForUnrecognizedPayloads` 九种拒绝形态（空串、全空白、纯文本、63/65/128 位 hex、非 hex 尾、hex 粘词、首行无哈希的多行文件）全部返回 null。

## Development Log

修法：`FetchChecksumAsync` 的内联解析（`text.Split(' ')[0].Trim()` + 长度判 64）抽为纯函数 `AssetDownloadService.ParseChecksum(string)`（`internal static`，经既有 InternalsVisibleTo 直测）。

- 实现：取首个非空行（剥 CRLF），`[GeneratedRegex(@"\b[0-9a-fA-F]{64}\b")]` 取第一个独立 64-hex token，小写返回。`\b` 词边界保证：128 位长串不截取（内部无边界）、tab 变体（tab 在 token 内部，Trim 剥不掉——旧码恰好死在这里）、openssl 变体（`(` `=` 非词字符构成边界）都能取出哈希。
- 只取首个非空行：多行 sha256sums.txt 的后续行是**别的文件**的哈希，绝不替本资产背书（与旧行为同口径——旧码也只看首 token）。
- 语义边界（与卡面确认一致）：**「已知变体解析失败」与「远端无校验文件」都不再混淆**——已知变体现在全部解析成功；格式完全不识别才返回 null，下游维持「无 checksum → 跳过校验、靠逐条 CRC 兜底」（B-008 已把该兜底做实）。未把「存在但解析不出」升级为硬失败：硬失败会让用户在 checksum 基建格式变化时完全装不上，违反「.sha256 校验不阻塞」的产品语义。
- 顺带收紧：旧码只要 64 字符就收（非 hex 字符会流向 VerifyChecksum 造成必失败的 purge 循环），正则限定 `[0-9a-fA-F]` 后非 hex token 直接判 null。类改 `partial` 以承载 GeneratedRegex。
- 已正确容错的部分（CRLF、前后空白、大写、长度≠64 拒绝）全部保留并有测试钉住。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/StupidDict.App/Assets/AssetDownloadService.cs`
- How to run/verify: `dotnet test StupidDict.slnx`
- Results: 2026-10-09 全套通过——Core 45/45，App 164/164（最终计数，含审查收尾的降级测试）（含本卡新增 17 条表驱动），build 0 警告 0 错误。
