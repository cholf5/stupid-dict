---
id: B-014
title: 单实例监听端 ReadLine 无超时，异常本地客户端可废 activate 通道
type: bug
priority: P3
size: S
status: todo
created: 2026-10-09
updated: 2026-10-09
blocked: none
---

## Goal

监听端对连上后不发数据的本地连接限时断开，本会话内后续 `activate` 信号不失效。

## Background

2026-10-09 扫描确认（`SingleInstance.cs:173-179`）：

- `WaitForConnectionAsync` 返回后 `reader.ReadLine()` 无限等待——本地某进程连上管道后既不写也不关，listener 线程永久阻塞在该连接，本会话内后续所有二次启动的 activate 信号全部落空（二次启动静默退出、窗口不前置）。
- 单实例互斥本身不受影响（靠锁文件）；仅本地进程可触发，正常双击连上即写一行。

## Acceptance Criteria

- [ ] 连接后限时（如 5s）不发送 `activate` 行即断开，listener 继续服务下一连接
- [ ] 正常二次启动 activate 路径不变；macOS 管道名长度约束（Id 23 字符）不动

## Subtasks

- [ ] `ReadLine` 换 `ReadLineAsync` + CTS 超时（或等价）
- [ ] 超时连接 Dispose 后循环继续
- [ ] 测试：用既有 internal 重载注入随机锁路径/短管道名

## Dependencies

- none

## Test Cases

### TC-001: 静默连接不废通道

Steps:
1. 客户端连接管道但不发送任何数据
2. 等超时（测试注入短超时）后再发一次正常 `activate`

Expected:
- 第二次 activate 成功送达（窗口前置信号触发）

## Development Log

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/StupidDict.App/SingleInstance.cs`
- How to run/verify: `dotnet test StupidDict.slnx`（单实例测试用短管道名，macOS socket 路径预算 44 字符）
- Results: 未运行（待修复会话）
