---
id: B-008
title: 留盘 zip 复用链上 CRC 失败无 purge 路径，用户卡死解压失败循环
type: bug
priority: P1
size: S
status: todo
created: 2026-10-09
updated: 2026-10-09
blocked: none
---

## Goal

损坏的留盘 zip 在「checksum 不可得」时不能把用户锁死在解压失败循环：CRC 失败即 purge 走重下，其余解压失败维持 6fd870d 的留盘复用语义。

## Background

2026-10-09 扫描确认：

- `MainWindow.axaml.cs:650-654`：`TryReuseDownloadedZipAsync` 在 `expected is null` 时无条件信任留盘 zip 并加入 `_verifiedZips`。
- 解压阶段逐条 CRC 失败只显示错误、zip 留盘（6fd870d 刻意语义：重试免重下）；重试 → `_verifiedZips.Contains` 命中 → 再次解压失败。**重启也不解决**：重启清了集合，但离线/格式不识别时 checksum 仍为 null，依旧直接复用。
- 完整死循环链 = 损坏 zip（B-006 拼接、或手工放置）+ checksum 不可得（B-007 格式不识别/离线）。唯一自救是手动删 `%TEMP%\stupiddict-downloads\` 里的 zip。

关键区分（修法核心）：**CRC 失败 ≠ 解压其他失败**。CRC 意味着字节损坏，同一文件复用不可能第二次成功 → purge 安全；而其他解压失败（如中途取消、非损坏类 IO）维持留盘复用不变。修时勿破坏 6fd870d 的「解压失败重试免重下」特性（AGENTS.md 下载链条目有记载）。

## Acceptance Criteria

- [ ] 解压 catch 中识别 CRC 类异常（InvalidData/BadEntry 族）→ `PurgeDownloadArtifacts` → 下次重试走重下
- [ ] 非损坏类解压失败仍留盘复用（既有三个复用测试 `ExtractionFailureRetryReusesKeptZipWithoutRedownloading` 等全绿）
- [ ] 取消（OCE）路径不受影响

## Subtasks

- [ ] 解压失败分类：CRC 族 vs 其他
- [ ] CRC 族 purge + 状态文案
- [ ] 回归 + 新增测试

## Dependencies

- none（与 B-006/B-007 同链路但可独立修）

## Test Cases

### TC-001: CRC 损坏 zip 重试后 purge 重下

Steps:
1. 注入含损坏条目的 zip（CRC 不符）到下载目录
2. 首次解压失败 → 点重试

Expected:
- zip 被 purge，走重下链而非再次解压

### TC-002: 普通解压失败仍复用

Steps:
1. 既有复用测试回归

Expected:
- 全绿（留盘复用语义未变）

## Development Log

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/StupidDict.App/MainWindow.axaml.cs`、`src/StupidDict.App/Assets/AssetDownloadService.cs`
- How to run/verify: `dotnet test StupidDict.slnx`（DownloadFlowTests）
- Results: 未运行（待修复会话）
