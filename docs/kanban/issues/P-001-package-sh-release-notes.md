---
id: P-001
title: package.sh 发布步骤注释纠正为数据解耦流程
type: docs
priority: P2
size: S
status: done
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

- [x] 头注与 NEXT 块改为：应用包 `gh release create vX`；数据资产 `gh release create data-N … --prerelease`
- [x] 与 README「打包与发布」、release.sh 尾部说明三处口径一致

## Subtasks

- [x] 改头注 :9
- [x] 改 NEXT 块 :125-135

## Dependencies

- none

## Test Cases

- 测试不适用（纯注释）：以三处文档人工比对代替。

## Development Log

2026-10-09 修复会话。行号已从卡面漂移（HEAD 前进），按内容定位：头注原 :9、NEXT 块原 :128-135。

- 头注：`# Upload with: gh release create v1.x dist/* …` 整句替换为数据解耦表述——应用包进版本 Release；dictionary.zip / audio-pack.zip（+ .sha256）进独立 data prerelease（data-N，见 ReleaseAssets.DataTag）；明确写了反向禁令「never one shared gh release create vX dist/*」+ 理由（数据 Release 不得挤占 releases/latest，应用内检查更新读的就是那个页面），并指回 README「打包与发布」。
- NEXT 块步骤 1 拆成两条：①应用包 `gh release create vX dist/StupidDict-*.zip`（注明正常发版走 release.sh + CI 无需本步、仅本地手动兜底时用——对齐 AGENTS.md 发布构建必须从 tag 干净 worktree 出的兜底链路），并保留原步骤 2 的资产名清单约束移入步骤 2；②数据资产 `gh release create data-N … --prerelease`（仅首次发布或数据更新时）。原步骤 2 自相矛盾的「应用内下载依赖 Release 的 latest 语义」删除，改为「应用内下载钉在 data-N（ReleaseAssets.DataTag）」。步骤 3（Gatekeeper xattr）不动。
- 三处口径比对：README「打包与发布」段落（App 包 vX + 数据资产 data-1 prerelease + prerelease 不参与 releases/latest 的理由）与 release.sh 尾部注释/警告（"not part of app releases: … pins to the data prerelease"、DATA_TAG=data-1 数据 Release 缺失警告）均已与新文案一致，无需改动这两处。
- 注意：`gh release create vX dist/StupidDict-*.zip` 有意不带 setup.exe 通配——setup 只在 CI windows job 出（卡面背景已知），本地兜底场景 dist/ 里没有它。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `scripts/package.sh`、`README.md`、`scripts/release.sh`
- How to run/verify: 人工比对三处口径
- Results: 已验证（2026-10-09）。①三处人工比对一致（README「打包与发布」、release.sh :133-152 区段注释+数据 Release 警告、package.sh 头注+NEXT 块，四处均表述「应用包 vX / 数据资产独立 data-N prerelease / prerelease 不参与 releases/latest」）；②实跑 `scripts/package.sh --rids osx-arm64 --dictionary /nonexistent` exit 0，结尾 NEXT 块新文案逐行目检正确（禁令句与 data-N 步骤齐全）；③`/bin/bash -n`（bash 3.2.57）语法通过，NEXT 为引用 heredoc 无展开面。
