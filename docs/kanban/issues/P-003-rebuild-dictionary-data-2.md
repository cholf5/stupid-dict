---
id: P-003
title: 修复 WordNetThesaurus 后重建 dictionary.db 并发 data-2
type: release
priority: P2
size: M
status: todo
created: 2026-10-09
updated: 2026-10-09
blocked: none
---

## Goal

B-001 修复构建器后，重建词典数据并发 `data-2`，让用户侧近/反义词缺陷随数据更新消除。

## Background

- B-001 修的是 `src/StupidDict.DataBuilder`，但**已发布的 data-1 数据带着四处缺陷**（大量词缺近/反义词行、部分 synset 词性错乱、卫星形容词与专有名词断链）——缺陷已随 `dictionary.zip` 发出，必须走数据更新链。
- 数据与 App 版本解耦（AGENTS.md/README）：`dictionary.zip`+`.sha256` 放独立 prerelease `data-2`；应用端 `ReleaseAssets.DataTag` 常量（现 `data-1`，ReleaseAssets.cs:14）bump 为 `data-2`，随下个 App 版生效。跨会话旧 zip 校验不过 → purge → 重下的自愈路径已有（`CrossSessionStaleZipFailsChecksumAndIsPurgedAndReplaced` 钉住时序）。

## Acceptance Criteria

- [ ] 用修复后 DataBuilder 全量重建 `dictionary.zip` + `.sha256`
- [ ] 抽样验证四类缺陷消除：短行词（如 a.k.a. 类）、卫星形容词、专有名词（Rome）、跨词性串行不再出现
- [ ] `gh release create data-2 … --prerelease`（流程照 README「打包与发布」）
- [ ] `ReleaseAssets.DataTag` bump `data-2`，随下个 App 版发版
- [ ] App 端从 data-1 升级路径冒烟（校验不过→purge→重下→装上）

## Subtasks

- [ ] 重建数据并核对产物
- [ ] 抽样验收四类缺陷
- [ ] 发 data-2 prerelease
- [ ] DataTag bump + 提交

## Dependencies

- B-001（构建器修复必须先落地）

## Test Cases

### TC-001: 升级自愈链

Steps:
1. 本地注入 data-1 时代的旧 zip（跨会话语义）后触发下载

Expected:
- 校验不过 → purge → 重下 data-2 → 装上

## Development Log

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/StupidDict.App/Assets/ReleaseAssets.cs`、`docs/plans/2026-10-08-data-release-decoupling-design.md`
- How to run/verify: 重建产物校验和核对 + App 下载冒烟
- Results: 未运行（待修复会话）
