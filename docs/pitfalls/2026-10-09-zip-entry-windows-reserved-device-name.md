# zip 条目最终段是 Windows 保留设备名（us/con.mp3）：GetFullPath 前缀比对误杀 + 解压落不了盘

- **状态**：已修复（zip-slip 校验改纯语法判定 `EntryEscapesDestination`；Windows 分支改 `\\?\` 扩展路径手工逐条解压），回归测试 `AudioPackImportMapsReservedDeviceNameEntries`（2026-10-10 起断言映射后文件名；原名 `AudioPackImportAcceptsReservedDeviceNameEntry`）、`ExtractZipRejectsParentTraversalEntry`、`ExtractZipRejectsRootedEntry`、`EntryEscapesDestinationClassifiesEntryNames`（DownloadFlowTests）。2026-10-10 追加保留名映射收尾（见文末「收尾」节）：保留名不再落盘，pre-Win11 的 con 回退 TTS 残留一并关闭。
- **日期**：2026-10-09
- **背景**：Windows 用户手动导入音频包报「导入失败：压缩包内出现非法路径：us/con.mp3」。条目名干净（官方 audio-pack.zip 里就是 `us/con.mp3`），报错来自我们自己的 zip-slip 校验（`MainWindow.ExtractZip`）。

## 症状与根因

`con` 是真实词头（v. 反对；n. 罪犯/骗子），发音包合法携带 `us/con.mp3`、`uk/con.mp3`；同理 `aux`、`nul`、`prn`、`com1` 类词形迟早出现。这些恰好全是 Windows 的旧式 DOS 设备名。

旧校验是教科书写法：`Path.GetFullPath(Path.Combine(dest, entry.FullName))` 再和 `dest` 前缀比对。但 Windows（Windows 11 之前）把**最终段**为保留设备名的路径当设备处理，且**扩展名被忽略**（`CON.TXT` 就是 CON）——官方文档原话见 [File path formats on Windows](https://learn.microsoft.com/en-us/dotnet/standard/io/file-path-formats) 的 "Handle legacy devices" 段。于是 `Path.GetFullPath("…\audio\us\con.mp3")` 被改写成 `\\.\CON` 形式，目录前缀整个丢掉，`StartsWith(dest)` 必然失败：合法条目被当成 zip-slip 攻击拒绝。非设备名条目全部正常通过，所以只有 `con.mp3` 一类文件报错。

同一机制还埋着第二层雷：就算校验放行，`ZipFile.ExtractToDirectory` 内部走普通 Win32 路径，Windows 11 之前 `CreateFile("…\us\con.mp3")` 会被重定向到 CON 设备——控制台进程里 MP3 字节写进控制台、文件静默落不了盘，GUI 进程没有控制台则直接报错。两种都不可用。

## 修复（`MainWindow.axaml.cs`）

1. **校验改纯语法判定**（`EntryEscapesDestination`）：条目名 rooted（盘符/UNC/前导分隔符，`Path.IsPathRooted`）或含 `..` 段才拒绝，其余一律放行。正确性论证：拒绝 rooted 与 `..` 之后，剩下的段全是字面名，逐段拼到目标目录下只可能落在目标目录内——规范化器的全部改写（分隔符折叠、尾点尾空格剔除、设备名重写）都不会跨出目录。
2. **Windows 解压走 `\\?\` 扩展路径手工逐条解压**（`ToExtendedPath` + `JoinEntryPath`）：`\\?\` 前缀让 Win32 完全跳过路径规范化，设备名按字面文件名落盘（NTFS 本就允许）；Windows 11 已解除保留名限制，前缀行为一致，单一代码路径覆盖两代系统。Unix 无设备名概念，天然无此坑。

   **后续补记（同日）**：发音包导入要逐文件进度条（十几万条目），逐条解压成了三平台统一路径——Unix 并入同一条循环（现名 `ExtractEntry`），`ToExtendedPath` 仅在 Windows 加前缀，`ExtractToDirectory` 不再使用；逐条解压同时是条目数进度回调的载体。
3. 配套细节：staging 目录从 `Path.GetFullPath` 后的绝对路径拼出（`\\?\` 只吃绝对反斜杠路径）；`JoinEntryPath` 用字符串拼接**不走 GetFullPath**——规范化正是要躲开的东西，`.` 段手动剔除（`\\?\` 不再帮你规范化掉），`\` 按分隔符处理（字面反斜杠在 Windows 文件名里本就非法）。

## 测试边界与残留

- Windows 分支（`\\?\` 解压）在 CI（ubuntu）不可达，只有 Windows 本机能真跑；`AudioPackImportAcceptsReservedDeviceNameEntry` 现在真正覆盖了它（2026-10-09 在 Windows 10 上跑通）。
- **读回保留名文件同样必须走 `\\?\`**：该测试原先用普通路径 `File.ReadAllText(…\us\con.mp3)` 校验内容，Windows 把这条路重定向到 CON 设备——`File.Exists` 返回 false、`File.ReadAllText` **永久阻塞**（本机探针实测 35 秒未返回），整套 `dotnet test` 因此挂死，还顺带堵死排在其后的非 Avalonia 测试（`ZipSlipGateTests` 一个都跑不完）。修复是测试助手 `DownloadFlowTests.ReadExtracted` → `MainWindow.ToExtendedPath`（该方法为此从 private 放开到 internal，让解压端与读回端共用同一份前缀知识）。
- 已知残留（刻意不修）：Windows 11 之前的机器上 `AudioPackPlayer` 的 `File.Exists(…\us\con.mp3)` 探测会被设备重定向吞掉（看不到已安装的文件），`con` 一词回退系统 TTS 发音——降级可接受；`ToExtendedPath` 内部化之后修复只剩一行（探测改走扩展路径），仍按原判断不动，等真实反馈再说。**（2026-10-10：已由保留名映射以更彻底的方式解决，见文末「收尾」节。）**

## 教训

1. **zip-slip 最直观的写法在 Windows 上静默误杀**：拿规范化结果做安全判定前，要想清楚规范化器还会做什么改写。GetFullPath 不是纯字符串运算，它带着 Win32 的路径世界观。
2. **保留设备名的两个隐蔽语义**：扩展名被忽略（`con.mp3` 命中 CON）+ 对路径**最终段**就生效（不用整个路径是 `con`）。普通业务名撞上设备名不是抬杠，词典产品里 `con` 就是词头。
3. **先定位报错出自哪一层**：报错文案是我们自己的 throw，说明解压库没炸、是校验被规范化结果绕懵了——从「字符串比对必然成功却失败」反推，唯一的自由度就是 GetFullPath 的改写行为，根因一查一个准。

## 收尾（2026-10-10）：保留名映射，整类问题关闭

- **背景**：评估「音频包解压后十几万小文件是否学游戏打包成单个资源包」时确认：散文件的后果是真实且成类的——本 pitfall 的 GetFullPath 误杀、`\\?\` 解压、staging 删除被吞、测试读回挂死、pre-Win11 的 con→TTS 残留，全是「把词头当文件名落盘」这一个决定引出来的。但打包不是对症药（见下），对症的是**让保留名根本不出现在磁盘上**。
- **为何不打包**：三平台文件播放器（afplay / MCI winmm / mpg123-mpv-ffplay）全部只认文件路径、全部进程外。pak 里的 `con` 要发声必须先解到临时文件——保留名问题在播放时刻原样请回来（pre-Win11 上 MCI open 临时 `con.mp3` 照样被设备重定向），等于两头都付成本。打包真正成立的前提是进程内播放（pak 只是字节区间，`con` 永远不成为文件名），但那是 Linux 无保底进程内音频栈的大项目（捆原生库，与「傻瓜零依赖」冲突），只有当安装/卸载速度也成为实测痛点时才值得整体立项。内存不是障碍（mmap/随机访问每次只读一条），播放后端才是。
- **修法**（`Assets/ReservedDeviceNames` 纯函数 + 两个应用点）：
  1. **解压落盘映射**：`JoinEntryPath` 对每个条目段做 `MapSegment`——点前词干（pre-Win11 连 `CON.THING` 都按 CON 匹配）、大小写不敏感地命中保留名集合（CON/PRN/AUX/NUL/COM0-9/LPT0-9 及上标变体）即加 `_` 前缀（`us/con.mp3` → `us/_con.mp3`）。落盘唯一入口统一应用，词典 zip 同走此路（非保留名逐字节原样，天然无感）。映射单射：词头正则 `[a-z' -]+`，没有以 `_` 开头的真词。data-2 的 zip 不用重发——映射只在解压端做，zip 条目名不动，zip-slip 校验照旧先看原始条目名。
  2. **查找映射 + 裸名回退**：`AudioPackPlayer.Play` 先探映射名，未命中再探裸名——老构建解压的包里裸名在 Unix/Win11 仍可达（Win11 已解除限制），继续可播；pre-Win11 上对裸名的 `File.Exists` 是文档化的安全 no（重定向返回 false、不阻塞），回落 TTS，与旧行为一致。
- **效果**：新解压的包磁盘上不存在保留名，本 pitfall 的全部表现一次性关闭——zip-slip 校验的 GetFullPath 陷阱失去触发对象、测试读回不再需要 `\\?\`（`DownloadFlowTests.ReadExtracted` 助手删除）、staging 删除不再踩设备重定向（扩展路径保留，兜底老构建遗留 staging）、**pre-Win11 的 con 回退 TTS 残留消除**（`_con.mp3` 是普通名，探测可达）。`ExtractEntry` 的 `\\?\` 写入与 staging 清扫/进位删除的扩展路径**保留不动**：兜底老构建遗留 staging 与手动导入 zip 里的其他怪名。
- **测试**：`ReservedDeviceNamesTests`（19 例：保留干映射、大小写、点前词干、`com10`/`constant` 等负例）+ `AudioPackLookupTests`（4 例：映射名可播、普通词原名、裸名回退按平台断言、映射名优先于裸名——裸名文件在 Windows 上造数据须走 `ToExtendedPath`）+ `AudioPackImportMapsReservedDeviceNameEntries`（断言落盘为 `_con.mp3` 且裸名不存在，普通路径直读）。TDD 全程：编译红（CS0103）→ 24 例绿 → 红检变异两轮（解压端去掉映射恰 1 红；查找端退回裸名恰 2 红）→ 还原复绿。全套 Core 83/83 + App 227/227。
- **顺手清理**：`MainWindow.ToExtendedPath` 注释与 `ExtractZip`/`ExtractEntry` 相关注释同步改写（staging 不再含保留名，扩展路径改为兜底语义）；AGENTS.md 三处（Windows 测试约束②、zip 铁律、发音段新增「保留设备名永不落盘」条目）。
