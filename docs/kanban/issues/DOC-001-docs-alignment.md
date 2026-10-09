---
id: DOC-001
title: 文档对齐：AGENTS.md 幽灵方法名 BuildLinkText + README 代理顺序表述
type: docs
priority: P2
size: S
status: done
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

- [x] AGENTS.md 不再引用不存在的 `BuildLinkText`
- [x] README 代理句与代码一致，或显式决定保持简化并留痕

## Subtasks

- [x] 改 AGENTS.md:44
- [x] README:114 评估并修改/留痕

## Dependencies

- none

## Test Cases

- 测试不适用（纯文档）：以 grep 复核（`grep -rn BuildLinkText` 零命中）代替。

## Development Log

2026-10-09 修复会话。行号从卡面漂移（AGENTS.md:44 → 实际 :46），按内容定位。改前先核实代码现状，文档以代码为准。

1. **AGENTS.md 幽灵方法名**——核实：`BuildLinkText` 全仓 grep 仅两处非卡面命中——AGENTS.md:46（本卡目标）与 `docs/pitfalls/2026-10-08-avalonia-inline-links.md:39`（描述「旧机器（LinkSegment/BuildLinkText/PosLineSegments/CreateLinkInline）已删净，别再往回写」的历史性引用，语义正确，保留）。实际方法核实：`CreateLinkSurface`（MainWindow.axaml.cs:1669）、`Text()`（:1688）、`BuildChips`（:1521）均真实存在；卡面建议「改为 CreateLinkSurface 或直接删掉」，采用前者——该括号枚举的是「构建结果页的三类原语」，`CreateLinkSurface`（词链接点击面）与 `Text()`（文本块）、`BuildChips`（词 chips）同级，替换后枚举仍完整且与紧随其后的 :47 行（详述 CreateLinkSurface）自洽，:44/:47 的自相矛盾消除。
2. **README:114 代理探测顺序**——核实 `ProxyDetector.cs`：`DetectFromEnvironmentAndSystem` 顺序为环境变量 → Windows（WinINET 注册表，ProxyServer/ProxyOverride，PAC 刻意不解析——由平台默认代理相位覆盖）→ macOS scutil；`ProbeCommonLocalPorts` 端口表 Clash 7890 / Clash Verge Rev 7897 / V2RayN 1087,10809 / Surge 6152 / Privoxy 8118 / SOCKS 1080；`AssetDownloadService.BuildAttempts` 相位：①平台默认代理过全部源 → ②直连镜像 → ③显式检测代理过前两源 → ④端口探测过前两源。AGENTS.md:32 的描述完整准确。评估：README 面向用户、macOS 视角的简化「环境变量 → scutil → 端口探测」对 macOS 用户完全正确，但句子省略 Windows 注册表一步与代码不一致——选择**补半句**而非保持简化：一个括注「；Windows 上还会另查系统代理设置的注册表」零成本消除不一致，比「保持简化 + 留痕」对读者更有用（该句本来就列了平台名，加一个平台不破坏简化性）。README:112-115 其余条目（GitHub 直连/镜像/手动导入）是下载链相位的用户视角归纳，未在本卡范围，未动。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `AGENTS.md`、`README.md`
- How to run/verify: grep 复核
- Results: 已验证（2026-10-09）。①`grep -rn BuildLinkText` 对 AGENTS.md / README.md / src/ / docs/plans/ 零命中；剩余命中仅三处且均为应保留项：`docs/kanban/board.md`（卡标题，本会话不改板）、本卡文件（题述）、pitfalls 文档（已删旧机器的历史名，刻意保留）；AGENTS.md 现 :46 引用 `CreateLinkSurface()`、:47 详述同一方法，无自相矛盾。②README:114 与 ProxyDetector.cs 源码逐相位比对一致（环境变量 → Windows 注册表 + macOS scutil → 常见端口）。③纯文档改动，`dotnet test` 全套 Core 81/81 + App 195/195 不受影响（CI 的 docs/** paths-ignore 亦不会因此触发无谓测试）。
