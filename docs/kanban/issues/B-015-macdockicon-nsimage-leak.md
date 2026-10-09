---
id: B-015
title: MacDockIcon 的 NSImage alloc 后未 release（一次性泄漏）
type: bug
priority: P3
size: S
status: done
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

- [x] 设置完成后发送 `release`（配 `@autoreleasepool` 或直接事后 MsgSend）
- [ ] Dock 图标功能不变（裸 `dotnet run` 时 Dock 显示内嵌图标）——代码仅新增一行 set 之后的 release、无任何行为分支，但「人眼看 Dock」按 AGENTS 约束必须真机人工目检，本会话 shell 无 WindowServer 会话做不了，待人工

## Subtasks

- [x] 补 release 消息发送
- [ ] macOS 手工 Dock 目检（待人工：本会话 shell 起 GUI 即报 Avalonia.Native RenderTimer -6661，基线代码同崩，属环境限制非本改动）

## Dependencies

- none

## Test Cases

- 内存释放无法 headless 断言；以编译 + 既有测试回归 + macOS 手工目检 Dock 图标代替（一行原因：objc 运行时行为不可 headless 验证）。

## Development Log

2026-10-09 修复会话：

- **修法（直接事后 MsgSend，不另配 @autoreleasepool）**：`SetApplicationIcon` 在 `setApplicationIconImage:` 之后无条件 `MsgSend(image, Selector("release"))`——NSImage 来自 `alloc`（create 侧 retainCount 1），`setApplicationIconImage:` 内部 retain 它自己需要的份，create 侧引用必须由我们平衡；放 if 外无条件发，`sharedApplication` 拿不到（app == Zero）的失败分支同样要放手里的引用。选直接 release 而非再造 NSAutoreleasePool：一处一次性调用，pool 只为它服务属过度结构；本线程是 Avalonia UI 线程（经 `Dispatcher.UIThread.Post` 落在 run loop 内），autorelease 对象有 run-loop pool 兜底。
- **`data` 刻意不 release（留痕防「顺手补」）**：`dataWithBytes:length:` 是类工厂方法产物，按 Cocoa 内存规则是 autoreleased 而非 owned——给它发 release 是 over-release，等 run-loop pool drain 时会被正确回收；卡面「已核实无问题的部分勿动」涵盖此项。
- 与卡面核对的其余不变量：`fixed` 下 `byte[]` 指针喂 `dataWithBytes:length:`（NSData 同步拷贝）生命周期正确；arm64 无 stret；失败静默（整段仍在 `TrySetFromEmbeddedIcon` 的 catch 内）、只在桌面生命周期分支调用（调用点 `App.OnFrameworkInitializationCompleted` 的 desktop 分支未动）。
- 若 release 误放（提前于 set / 双发），NSApplication 持有的对象变 use-after-free、启动即崩——此失败模式人眼目检（Dock 图标正常显示且 app 不崩）即覆盖。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/StupidDict.App/MacDockIcon.cs`
- How to run/verify: `dotnet build StupidDict.slnx`；macOS `dotnet run --project src/StupidDict.App -f net10.0` 人眼看 Dock
- Results: 2026-10-09 会话实测。`dotnet build StupidDict.slnx` 0 警告 0 错误；全套 `dotnet test StupidDict.slnx` Core 45/45 + App 191/191 绿（MacDockIcon 无 headless 可断言面，按卡面以编译 + 回归代替；objc 运行时行为不可 headless 验证）。GUI 目检环境受限记录：本会话 shell 直接 `dotnet run` 报 Avalonia.Native RenderTimer -6661（够不着 WindowServer），`git stash` 后基线代码同崩——证实属会话环境限制、崩点在平台初始化（早于 MacDockIcon 被调度），非本改动引入；**裸 `dotnet run` 人眼看 Dock 一项待真机人工**（改动仅一行 set 之后的 release，无行为分支）。
