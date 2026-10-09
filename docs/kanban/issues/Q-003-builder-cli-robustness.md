---
id: Q-003
title: 构建器 CLI 与构建健壮性三小项（参数越界 / journal 半成品库 / --top 解析）
type: chore
priority: P2
size: M
status: todo
created: 2026-10-09
updated: 2026-10-09
blocked: none
---

## Goal

DataBuilder / AudioPackBuilder 的 CLI 报错可读、构建中断不留半成品库。

## Background

2026-10-09 扫描确认：

1. **DataBuilder 位置参数越界**——`DataBuilder/Program.cs:9` 只挡零参数，`:51` `positional[0]` 无 `Count` 检查：`dotnet run -- ... --wordnet /dir`（忘传源文件）时 args.Length≥1 过首检，`positional` 为空 → 未捕获 `IndexOutOfRangeException` 裸堆栈。修法：校验 `positional.Count`，不足时打印用法退出。
2. **`journal_mode=OFF` 半成品库**——`Core/Dictionary/DictionaryDatabase.cs:34` `PRAGMA journal_mode=OFF; synchronous=OFF;`，且无 Rollback（:92-102 只有 Begin/Commit），依赖 Dispose 隐式回滚——journal OFF 时回滚不工作、崩溃后文件可能损坏（SQLite 文档明确）。`Create()` 先删旧文件（:29），350 万行单事务中途异常（磁盘满等）后，默认输出路径 `ApplicationData/StupidDict/dictionary.db`（DataBuilder Program.cs:142-144）**正是应用启动自动查找的词典位置** → 应用下次打开读到无 `built_at` meta 的半截库、无法自检。修法方向：临时文件构建 + 成功后原子改名（更稳，顺带解决「先删旧文件」窗口）。
3. **AudioPackBuilder `--top abc` 静默误报**——`AudioPackBuilder/Program.cs:27-28` `case "--top" when i + 1 < args.Length && int.TryParse(...)` 失败落 default，把参数值当词典路径，最后报「词典不存在： abc」。修法：解析失败显式报「--top 需要整数」退出。

## Acceptance Criteria

- [ ] 三项分别落地；构建器手工冒烟跑通
- [ ] 构建中途异常不再污染应用词典查找路径

## Subtasks

- [ ] DataBuilder 参数校验
- [ ] 临时文件 + 原子改名（或等价方案，留痕）
- [ ] AudioPackBuilder --top 显式报错
- [ ] 各补一条单测/冒烟记录

## Dependencies

- none

## Test Cases

### TC-001: 参数不足给用法

Steps:
1. `dotnet run --project src/StupidDict.DataBuilder -- --wordnet /some/dir`

Expected:
- 打印用法、退出码非 0，无堆栈

### TC-002: 构建中断无半成品

Steps:
1. 注入构建中途异常（如磁盘满模拟）

Expected:
- 输出路径无 dictionary.db 半成品（临时文件被清理）

## Development Log

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/StupidDict.DataBuilder/Program.cs`、`src/StupidDict.AudioPackBuilder/Program.cs`、`src/StupidDict.Core/Dictionary/DictionaryDatabase.cs`
- How to run/verify: `dotnet test StupidDict.slnx` + 构建器手工冒烟
- Results: 未运行（待修复会话）
