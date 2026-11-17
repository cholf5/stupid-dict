---
id: B-016
title: 首次发音慢——发音包数据库的冷打开发生在点击回调里（补启动预热）
type: bug
priority: P2
size: S
status: done
created: 2026-11-17
updated: 2026-11-17
blocked: none
---

## Goal

第一次点发音与之后一样快：把 715 MB 发音包数据库的冷打开从点击回调挪到窗口起来的后台线程，不加设置项、不出声、不新增资产。

## Background

用户反馈「第一次点发音很慢，之后就快了，是不是要预热？」。本机实测（macOS 26.5.2，`audio-pack.db` 715 MB）：

- 打开 pack 数据库 + 取一个词：冷 **780 ms**（查询本身 11 ms）／半热 31 ms／热 0.3 ms。
- `afplay` 进程启动 18–34 ms，约 50 ms 出声（CoreAudio IO 线程起）；进程存活比片段长约 1 s 是退出尾部，不影响听感。
- 音频设备无首次激活惩罚：90 s 空闲后 0.925 s 对连续播放 0.858 s。
- 整机刚启动、框架页全冷时 `afplay` 首次 3.56 s 对热态 0.86 s（多出约 2.7 s）——次要因素。

`AudioPackStore` 只在第一次点发音时才第一次打开该库，且跑在 UI 线程的点击回调里；词典侧早有 `WarmupQuietlyAsync` 启动预热，发音包这条路径没有对应机制。

## Acceptance Criteria

- [x] 启动时在后台（非 UI 线程）预热发音包查找路径，静默：不开音频设备、不写临时文件
- [x] 预热 best-effort：无包/坏库/异常一律不外抛，不影响启动
- [x] 预热不改变播放语义，也不让数据库变得不可替换（`Pooling=false` 的连接必须随用随关）
- [x] 不新增设置项、不新增资产

## Subtasks

- [x] `ISpeechPlayer.WarmUp()` + `IAudioPackStore.WarmUp()` 契约
- [x] `AudioPackStore` 抽出共用 `ReadBlob`，播放与预热同一条 SQL
- [x] `MainWindow` 构造期 `Task.Run(WarmUpSpeechQuietlyAsync)`
- [x] 测试与设计文档

## Dependencies

- none

## Test Cases

### TC-001: 预热不发声、不落盘

Steps:
1. 造一个含 `the` 的临时 pack 库
2. `store.WarmUp()`

Expected:
- `$TMPDIR` 下的 `stupiddict-audio-*` 文件数不变
- 预热只到 store，不把文件交给播放器

### TC-002: 预热后数据库仍可替换

Steps:
1. `store.WarmUp()`
2. `File.Move` / `File.Delete` 那个库

Expected:
- 成功（Windows 上若连接残留即失败）

### TC-003: 启动即预热

Steps:
1. 用注入的录制播放器构造 `MainWindow` 并 `Show()`

Expected:
- 观测到恰好一次 `WarmUp`，且 `Played` 为空

## Development Log

2026-11-17 会话，结论与取舍见 `docs/plans/2026-11-17-audio-pack-warmup-design.md`：

- 主因定位：冷打开 780 ms 对热 0.3 ms，且发生在 UI 线程点击回调里。`afplay` 启动/出声只有几十毫秒，音频设备无首次激活惩罚——「预热」要预热的对象是数据库，不是音频设备。
- 做法：`ISpeechPlayer.WarmUp()` 新增静默 best-effort 出口；`AudioPackStore.WarmUp()` 开只读连接查 `the` 丢 blob（把开连接+主键查询抽成 `ReadBlob`，播放与预热共用同一段 SQL）；`AudioPackPlayer`/`CompositeSpeechPlayer` 按 `Stop()` 同构的容错转发；`SystemTtsPlayer.WarmUp()` 空实现（语音枚举故意懒跑，B-011）；`MainWindow` 构造期 `Task.Run` 上跑，异常就地吞掉而不是留给被丢弃任务当 UnobservedTaskException。
- 明确不做：播放器静音预热（只治整机刚启动的 ~2.7 s，代价是每次启动一个短命进程 + 一个静音资产，真机仍有问题再加）；常驻连接（省 0.3 ms 却破坏 `Pooling=false` 的可替换性）；预热时生成临时文件（会在 `$TMPDIR` 留垃圾）；激活/空闲后再预热（用户选择只用启动一次，残留风险已记录在设计文档）。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/StupidDict.App/Speech/{ISpeechPlayer,AudioPackStore,AudioPackPlayer,SystemTtsPlayer}.cs`、`src/StupidDict.App/MainWindow.axaml.cs`
- How to run/verify: `dotnet test StupidDict.slnx`；真机点一次发音（首次 vs 第二次主观对比）
- Results: 2026-11-17 全套 Core 83/83 + App 369/369 绿。新增 `SpeechWarmupTests`（6 条）、`AudioPackLookupTests.WarmUpReachesTheStoreAndPlaysNothing`、`HeadlessTests.StartupWarmsThePronunciationPath`。冷页状态无法在测试里可靠制造，首次到手延迟只做机制测量（780 ms → 0.3 ms）与代码路径保证。
