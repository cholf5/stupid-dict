---
id: DOC-001
title: 文档对齐：AGENTS.md 幽灵方法名 BuildLinkText + README 代理顺序表述
type: docs
priority: P2
size: S
status: todo
created: 2026-10-09
updated: 2026-10-09
blocked: none
---

## Goal

消除文档与代码不一致的两处：AGENTS.md 引用不存在的方法名；README 代理探测顺序缺一步（评估）。

## Background

2026-10-09 扫描确认：

1. **AGENTS.md:44 引用 `BuildLinkText()`**——全仓库 grep 零命中，词链接的实际构建方法是 `CreateLinkSurface`（MainWindow.axaml.cs:1420），且 AGENTS.md 紧随其后的 :45 行自己就引用了 `CreateLinkSurface`——第 44/45 行自相矛盾，`BuildLinkText` 是重构前的旧名。改为 `CreateLinkSurface()` 或直接删掉（:45 已详述）。
2. **README:114 代理探测顺序省略 Windows 注册表**——文档写「环境变量 → macOS scutil --proxy → 端口探测」；代码现实（ProxyDetector.cs:28/58-68/137-164）是「环境变量 → **Windows WinINET 注册表** → macOS scutil → 端口探测」（端口表含 Clash Verge Rev 7897、V2RayN 1087、Privoxy 8118）。AGENTS.md:32 的描述完整准确。README 面向用户的简化对 macOS 用户完全正确；评估：补半句「（Windows 另查系统代理设置）」或保持简化并留痕。

## Acceptance Criteria

- [ ] AGENTS.md 不再引用不存在的 `BuildLinkText`
- [ ] README 代理句与代码一致，或显式决定保持简化并留痕

## Subtasks

- [ ] 改 AGENTS.md:44
- [ ] README:114 评估并修改/留痕

## Dependencies

- none

## Test Cases

- 测试不适用（纯文档）：以 grep 复核（`grep -rn BuildLinkText` 零命中）代替。

## Development Log

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `AGENTS.md`、`README.md`
- How to run/verify: grep 复核
- Results: 未运行（待修复会话）
