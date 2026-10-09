---
id: B-015
title: MacDockIcon 的 NSImage alloc 后未 release（一次性泄漏）
type: bug
priority: P3
size: S
status: todo
created: 2026-10-09
updated: 2026-10-09
blocked: none
---

## Goal

Dock 图标设置完成后释放 `NSImage`，消除一次性原生内存泄漏。

## Background

2026-10-09 扫描确认（`MacDockIcon.cs:50-56`）：

- `MsgSendObject(MsgSend(NSImage, alloc), initWithData:, data)` 取得 retainCount=1 的 `NSImage`，`setApplicationIconImage:` 内部 retain/copy 之后无人发 `release`，也无 `@autoreleasepool`。一次性泄漏一个 NSImage 及其位图数据，无行为影响。
- 已核实无问题的部分（勿动）：`byte[]` 指针经 `dataWithBytes:length:` 同步拷贝，`fixed` 生命周期正确；arm64 无 stret 顾虑。
- 验证约束（AGENTS.md）：`NSRunningApplication.icon` 只读 LaunchServices 层、对运行时设置全盲，**验证只能人眼看 Dock**。

## Acceptance Criteria

- [ ] 设置完成后发送 `release`（配 `@autoreleasepool` 或直接事后 MsgSend）
- [ ] Dock 图标功能不变（裸 `dotnet run` 时 Dock 显示内嵌图标）

## Subtasks

- [ ] 补 release 消息发送
- [ ] macOS 手工 Dock 目检

## Dependencies

- none

## Test Cases

- 内存释放无法 headless 断言；以编译 + 既有测试回归 + macOS 手工目检 Dock 图标代替（一行原因：objc 运行时行为不可 headless 验证）。

## Development Log

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/StupidDict.App/MacDockIcon.cs`
- How to run/verify: `dotnet build StupidDict.slnx`；macOS `dotnet run --project src/StupidDict.App -f net10.0` 人眼看 Dock
- Results: 未运行（待修复会话）
