---
id: P-001
title: package.sh 发布步骤注释纠正为数据解耦流程
type: docs
priority: P2
size: S
status: todo
created: 2026-10-09
updated: 2026-10-09
blocked: none
---

## Goal

package.sh 的头注与结尾打印的发布步骤与现行数据解耦流程一致，照做不再破坏「releases/latest 只指向 App 版本」不变量。

## Background

2026-10-09 扫描确认（`scripts/package.sh`）：

- 头注 `:9`：`# Upload with: gh release create v1.x dist/* — see the summary the script prints.`
- 结尾 NEXT 块 `:125-135` 步骤 1：`gh release create v1.x dist/* --title "..." --notes "..."`
- 现实：`dist/*` 现在同时含 `dictionary.zip`/`audio-pack.zip`（+`.sha256`）。照注释把全部产物传进正式 App Release 会违反数据解耦设计——`UpdateChecker` 读 `releases/latest`（网页端 302），数据 Release 若先创建或挤占 latest 语义，更新检查错乱；正确流程（README「打包与发布」:175-185、release.sh:133-151）：**应用包进 vX Release；dictionary.zip/audio-pack.zip+.sha256 进独立 prerelease `data-N`（prerelease 永不参与 releases/latest）**。
- NEXT 块自己的步骤 2 还写着「应用内下载依赖 Release 的 latest 语义」，与步骤 1 自相矛盾。

## Acceptance Criteria

- [ ] 头注与 NEXT 块改为：应用包 `gh release create vX`；数据资产 `gh release create data-N … --prerelease`
- [ ] 与 README「打包与发布」、release.sh 尾部说明三处口径一致

## Subtasks

- [ ] 改头注 :9
- [ ] 改 NEXT 块 :125-135

## Dependencies

- none

## Test Cases

- 测试不适用（纯注释）：以三处文档人工比对代替。

## Development Log

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `scripts/package.sh`、`README.md`、`scripts/release.sh`
- How to run/verify: 人工比对三处口径
- Results: 未运行（待修复会话）
