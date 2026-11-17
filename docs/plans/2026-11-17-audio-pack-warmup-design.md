# 首次发音预热（B-016）设计与实测

日期：2026-11-17 · 机器：macOS 26.5.2 / arm64（M2 Mac mini，外置耳机输出）

## 背景

用户反馈「第一次点发音很慢，之后就快了」。实测（本机 `~/Library/Application Support/StupidDict/audio-pack.db`，715 MB）后确认慢在哪：

| 环节 | 冷（本进程首次读、页缓存也没有） | 半热（进程新、文件页在） | 热 |
|---|---|---|---|
| 打开 pack 数据库 + 取一个词 | **780 ms**（其中查询 11 ms） | 31 ms | 0.1–0.7 ms |
| `afplay` 进程启动 | — | — | 18–34 ms |
| 真正出声（CoreAudio IO 线程起来） | — | — | ~50 ms |

结论：

1. **主因是发音包数据库的冷打开**。`AudioPackStore` 只在第一次点发音时才第一次打开 `audio-pack.db`，冷页读取实测约 0.8 s，且发生在 UI 线程的点击回调里；之后文件页进缓存，同一动作 0.3 ms。这与「第一次慢、之后快」完全一致。
2. 词典侧早就有启动预热（`WarmupQuietlyAsync` → `DictionaryService.WarmupAsync`），发音包这条路径从来没有对应机制，属遗漏。
3. `afplay` 不是主因：启动 20–30 ms、约 50 ms 出声；它进程存活比片段长约 1 s 是退出清理的尾部，不影响听感。只有整机刚启动、框架页全冷时，第一次实测 3.56 s 对热态 0.86 s（多出约 2.7 s），那才轮到播放器自身的冷启动。
4. 音频设备本身不需要「预热」：90 s 空闲后 afplay 的耗时与连续播放无差别（0.925 s 对 0.858 s），CoreAudio 没有可观测的首次激活惩罚。

## 目标

把发音包数据库的冷打开从「第一次点击」挪到「窗口起来的后台线程」，首次发音与后续一样快；不加设置项、不出声、不新增资产、不常驻内存。

## 做了什么

- `ISpeechPlayer.WarmUp()`：播放链新增的预热出口，契约是**静默**（不开音频设备、不写临时文件）与 **best-effort**（不外抛）。
- `IAudioPackStore.WarmUp()` / `AudioPackStore.WarmUp()`：开一次只读连接、查一个常用词（`the`）、丢掉 blob。为此把「开连接 + 主键查询」抽成 `ReadBlob(word, column)`，播放路径与预热共用同一段代码，避免两条会各自漂移的 SQL。
- `AudioPackPlayer.WarmUp()` → 转发 store（try/catch 收编）；`CompositeSpeechPlayer.WarmUp()` → 逐播放器转发（与 `Stop()` 同构的容错：一个坏掉的播放器不影响其余，也不让异常逃进被丢弃的启动任务）。
- `SystemTtsPlayer.WarmUp()`：空实现。TTS 的语音枚举**故意**保持懒跑（B-011），启动时替它起探针进程等于给每一次启动都付一个多数点击用不到的成本。
- `MainWindow` 构造期在 `Task.Run` 上跑 `WarmUpSpeechQuietlyAsync(_speech)`，与词典预热并列、但独立于词典是否就绪（发音包可以没有词典就存在）。

## 评估过什么、为何不做

- **播放器静音预热**（用一个极小静音 mp3 让 `afplay -v 0` 跑一次）：治的只是「整机刚启动」的 ~2.7 s，代价是每次启动都起一个存活约 1 s 的短命进程、并且要新引入一个静音资产。按最小改动先只做数据库这条；真机上如果开机后第一次点击仍然慢，再加这一层（Windows 上对应 MCI `open` + `close`，DirectShow 建图才是慢的那步，open 完立刻 close 完全静音）。
- **常驻连接**：保留一个只读连接省下的是热态 0.3 ms，而 `Pooling=false` 是替换数据库的前提（见 Core/SqliteConnections 与 Windows 文件锁的坑）。用常驻连接换 0.3 ms 不划算。
- **预热时顺手生成临时文件**：会让每次启动都在 `$TMPDIR` 留一个 mp3（`Cleanup` 只在 `Stop` 时跑，从没播放过的进程永远不 Stop）。预热只读 blob，不落盘。
- **激活时再预热 / 长时间空闲后再预热**（覆盖睡眠唤醒后页缓存被挤掉）：用户明确选择「仅启动时预热一次」。这是已知残留：应用常驻数小时或机器睡过一觉后，第一次发音可能又遇到冷打开。真要处理时应按「窗口重新激活且距上次预热超过 N 分钟」重跑同一动作，而不是改这里的预热内容。
- **换掉 afplay 用进程内 CoreAudio 播放**：能省掉那 ~1 s 的退出尾部（听感无收益）和 ~50 ms 出声延迟，但要把三平台的播放后端都重做，与本问题（首次慢）不对应。

## 验证

- `dotnet test StupidDict.slnx`：Core 83 + App 369 全绿。
- 新增测试：
  - `SpeechWarmupTests`（6 条）：复合链转发、坏播放器不影响其余、无包时不抛也不产生文件、**预热不写临时文件**、**预热后数据库仍可移动/删除**（`Pooling=false` 的可替换性，Windows 上这个断言才有牙齿）、预热不影响随后的播放。
  - `AudioPackLookupTests.WarmUpReachesTheStoreAndPlaysNothing`：预热只到 store，绝不把文件交给播放器（否则启动即发声）。
  - `HeadlessTests.StartupWarmsThePronunciationPath`：`MainWindow` 构造后经注入播放器观测到预热恰好发生、且没有触发任何播放。
- 未覆盖：真机上「第一次点击的到手延迟」只能人耳/人手主观确认——页缓存无法在测试里可靠制造冷态。
