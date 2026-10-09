---
id: B-010
title: RecentSearchStore 读写共享连接加锁不对称（GetRecent 无锁）
type: bug
priority: P2
size: S
status: done
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

- [x] `GetRecent` 与 `Add` 使用同一把锁，连接访问全串行
- [x] `Add` 的 INSERT+DELETE 包显式事务（顺带修）
- [x] 既有历史语义测试全绿（30 条去重/时间戳单调化不受影响）

## Subtasks

- [x] `GetRecent` 取 `_writeGate`
- [x] `Add` 加事务
- [x] 并发压力测试 + 回归

## Dependencies

- none

## Test Cases

### TC-001: 并发 Add/GetRecent 无异常

Steps:
1. Core.Tests 并发压力：多线程并行 `Add`/`GetRecent` 数百次

Expected:
- 无 SqliteException，结果自洽

## Development Log

- 修法（2026-10-09）：`GetRecent` 整体包进 `lock (_writeGate)`（字段名沿用未改——锁改为读写共用，改名属顺手重构未做）；`Add` 的 INSERT+DELETE 用 `_connection.BeginTransaction()` 包裹并显式指派 `cmd.Transaction`（Microsoft.Data.Sqlite 不自动入事务），`Commit()` 正常提交、异常时 `using` Dispose 回滚。连接仍走 `SqliteConnections.Open`（Pooling=false）。
- 锁序评估：全类只有 `_writeGate` 一把锁、构造器建表先于并发可见，无死锁面；`GetRecent` 锁内工作是 30 行上限的 SELECT + 列表构造（微秒级），UI `RefreshRecents` 与线程池 `Add` 的交错从零防护变串行，无性能顾虑。
- 事务收益：锁已串行化后，卡面提到的「读到删除前满表快照」被锁覆盖；事务主要兜崩溃原子性——进程死在 INSERT 与 DELETE 之间时回滚到 pre-Add 状态，不留未裁剪表。
- 测试（RecentSearchStoreTests +1）：`ConcurrentAddAndGetRecentStayConsistent`——400 轮 `Parallel.For` 交错 `Add`/`GetRecent`，断言无 SqliteException 逃出、≤30 条、query_norm 去重自洽、queried_at 单调不增。红检：临时摘掉 `GetRecent` 的锁 3/3 立即红——e_sqlite3 的 serialized 线程模式没有兜住共享连接的并发命令使用，实证卡面「隐含前提不可依赖」的判断；恢复后连跑 3 轮绿。既有 4 条语义测试（排序/去重/置顶/30 条上限）原样绿。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/StupidDict.Core/History/RecentSearchStore.cs`、`tests/StupidDict.Core.Tests/RecentSearchStoreTests.cs`
- How to run/verify: `dotnet test StupidDict.slnx`（Core.Tests）
- Results: 2026-10-09 全套 Core 46/46 + App 195/195 全绿；并发测试修复前摘锁 3/3 红（判别力实证）、修复后连跑 3 轮绿；双 TFM（net10.0 / net10.0-windows）构建 0 警告 0 错误。
