---
id: P-002
title: 打包/发布脚本健壮性小项（8 处，2 项可选）
type: chore
priority: P3
size: M
status: todo
created: 2026-10-09
updated: 2026-10-09
blocked: none
---

## Goal

打包/发布链的八个小缺口一次清掉；标「可选」的两项评估后落地或留痕不做。

## Background

2026-10-09 扫描确认：

1. **release.sh:137 `gh release view ... || true`**——`gh` 查询失败（网络抖动/Release 刚建未同步）时 ASSETS 为空，:141-148 把全部资产误判为「Release 产物缺失」，诱导去 Re-run 一个本来成功的 job。修法：区分「查询失败」与「真缺失」（查询失败重试或明确提示无法核对）。
2. **release.sh:71-73 mktemp 在 /tmp**——跨卷 `mv` 非原子且权限 0600。修法：`mktemp "$PROJECT.tmp.XXXXXX"` 同目录，mv 原子 rename。
3. **release.sh:42 版本校验**——接受前导零（01.02.003）、不校验新旧版本单调（降级 tag 可发布）。可选修。
4. **package.sh:57/47 `--rids` 无白名单**——rid 直接拼 `rm -rf dist/stage/$rid` 与输出路径，`--rids "../.."` 越界。修法：rid 必须匹配已知 RID 集合（或 `^[a-z0-9]+-[a-z0-9.]+$` 且不含路径分隔符）。
5. **package.sh:88/100 目标 zip 已存在时 `zip -r` 是增量更新不是重建**——本地重复打包 + 中途换过 dictionary.db 时新旧混合。修法：zip 前置 `rm -f`。
6. **package.sh:77 Info.plist `CFBundleShortVersionString` 恒 1.0**——未接 csproj `<Version>`，Finder「显示简介」永远显示 1.0。修法：从 csproj 提取版本注入 heredoc。
7. **.github/workflows/dotnet-desktop.yml:137-141 choco innosetup 未钉版本**——上游 Inno 7 / 包名变化时 ISCC 硬编码路径断裂。可选修（钉 6.x）。
8. **make-icon.py:143 `--content 0` 除零**——argparse 无范围校验。修法：`0 < content <= 1024` 校验。

## Acceptance Criteria

- [ ] 1/2/4/5/6/8 落地
- [ ] 3/7 评估后落地或留痕不做
- [ ] `release.sh`/`package.sh` 手工冒烟通过（不真发版，可用 dry-run/参数校验路径验证）

## Subtasks

- [ ] release.sh view 失败区分
- [ ] release.sh 同目录 mktemp
- [ ] （可选）版本前导零/单调校验
- [ ] package.sh --rids 白名单
- [ ] package.sh zip 前 rm -f
- [ ] package.sh Info.plist 版本注入
- [ ] （可选）CI choco 钉版本
- [ ] make-icon.py --content 校验

## Dependencies

- none

## Test Cases

- 测试不适用为主（CI shell 脚本）：以手工冒烟 + 关键路径 dry-run 记录代替；Info.plist 修复后验证 .app 在 Finder 显示正确版本。

## Development Log

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `scripts/release.sh`、`scripts/package.sh`、`scripts/make-icon.py`、`.github/workflows/dotnet-desktop.yml`
- How to run/verify: 手工冒烟（参数校验路径、`--rids` 非法值拒绝、重复打包 zip 内容干净）
- Results: 未运行（待修复会话）
