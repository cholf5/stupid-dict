---
id: B-010
title: RecentSearchStore 读写共享连接加锁不对称（GetRecent 无锁）
type: bug
priority: P2
size: S
status: todo
created: 2026-10-09
updated: 2026-10-09
blocked: none
---

## Goal

同一 SqliteConnection 上的读写全部串行化，消除违反 Microsoft.Data.Sqlite 单线程约定带来的崩溃风险。

## Background

2026-10-09 扫描确认（两个分区独立报告，交叉印证）：

- `Core/History/RecentSearchStore.cs`：`Add`（:39-63）全程持 `_writeGate`，`GetRecent`（:65-76）**不取锁**，读写共用一个连接对象（`SqliteConnections.Open`，Pooling=false）。
- 调用面交错：UI 线程 `RefreshRecents` → `History.GetRecent()`（MainWindow.axaml.cs:515-517）vs 线程池 `History.Add`（DictionaryService.cs:38/95，`LookupAsync` 的 `Task.Run` 内）。快速搜索 + Esc/后发先至都能造成两线程同时操作连接。
- e_sqlite3 默认 serialized 线程模式通常能串行化，但这层安全是隐含前提、应用层零防护；一旦 `GetRecent` 在 `RefreshRecents` 里抛，`RunSearch` 是 async void → **进程崩溃**。
- 顺带：`Add` 的 INSERT+DELETE 两语句无显式事务，`GetRecent` 可能读到删除前的满表快照（30 条上限内的瞬时脏读，轻微）。

## Acceptance Criteria

- [ ] `GetRecent` 与 `Add` 使用同一把锁，连接访问全串行
- [ ] `Add` 的 INSERT+DELETE 包显式事务（顺带修）
- [ ] 既有历史语义测试全绿（30 条去重/时间戳单调化不受影响）

## Subtasks

- [ ] `GetRecent` 取 `_writeGate`
- [ ] `Add` 加事务
- [ ] 并发压力测试 + 回归

## Dependencies

- none

## Test Cases

### TC-001: 并发 Add/GetRecent 无异常

Steps:
1. Core.Tests 并发压力：多线程并行 `Add`/`GetRecent` 数百次

Expected:
- 无 SqliteException，结果自洽

## Development Log

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/StupidDict.Core/History/RecentSearchStore.cs`
- How to run/verify: `dotnet test StupidDict.slnx`（Core.Tests）
- Results: 未运行（待修复会话）
