# zip 条目最终段是 Windows 保留设备名（us/con.mp3）：GetFullPath 前缀比对误杀 + 解压落不了盘

- **状态**：已修复（zip-slip 校验改纯语法判定 `EntryEscapesDestination`；Windows 分支改 `\\?\` 扩展路径手工逐条解压），回归测试 `AudioPackImportAcceptsReservedDeviceNameEntry`、`ExtractZipRejectsParentTraversalEntry`、`ExtractZipRejectsRootedEntry`、`EntryEscapesDestinationClassifiesEntryNames`（DownloadFlowTests）
- **日期**：2026-10-09
- **背景**：Windows 用户手动导入音频包报「导入失败：压缩包内出现非法路径：us/con.mp3」。条目名干净（官方 audio-pack.zip 里就是 `us/con.mp3`），报错来自我们自己的 zip-slip 校验（`MainWindow.ExtractZip`）。

## 症状与根因

`con` 是真实词头（v. 反对；n. 罪犯/骗子），发音包合法携带 `us/con.mp3`、`uk/con.mp3`；同理 `aux`、`nul`、`prn`、`com1` 类词形迟早出现。这些恰好全是 Windows 的旧式 DOS 设备名。

旧校验是教科书写法：`Path.GetFullPath(Path.Combine(dest, entry.FullName))` 再和 `dest` 前缀比对。但 Windows（Windows 11 之前）把**最终段**为保留设备名的路径当设备处理，且**扩展名被忽略**（`CON.TXT` 就是 CON）——官方文档原话见 [File path formats on Windows](https://learn.microsoft.com/en-us/dotnet/standard/io/file-path-formats) 的 "Handle legacy devices" 段。于是 `Path.GetFullPath("…\audio\us\con.mp3")` 被改写成 `\\.\CON` 形式，目录前缀整个丢掉，`StartsWith(dest)` 必然失败：合法条目被当成 zip-slip 攻击拒绝。非设备名条目全部正常通过，所以只有 `con.mp3` 一类文件报错。

同一机制还埋着第二层雷：就算校验放行，`ZipFile.ExtractToDirectory` 内部走普通 Win32 路径，Windows 11 之前 `CreateFile("…\us\con.mp3")` 会被重定向到 CON 设备——控制台进程里 MP3 字节写进控制台、文件静默落不了盘，GUI 进程没有控制台则直接报错。两种都不可用。

## 修复（`MainWindow.axaml.cs`）

1. **校验改纯语法判定**（`EntryEscapesDestination`）：条目名 rooted（盘符/UNC/前导分隔符，`Path.IsPathRooted`）或含 `..` 段才拒绝，其余一律放行。正确性论证：拒绝 rooted 与 `..` 之后，剩下的段全是字面名，逐段拼到目标目录下只可能落在目标目录内——规范化器的全部改写（分隔符折叠、尾点尾空格剔除、设备名重写）都不会跨出目录。
2. **Windows 解压走 `\\?\` 扩展路径手工逐条解压**（`ExtractEntries` + `ToExtendedPath` + `JoinEntryPath`）：`\\?\` 前缀让 Win32 完全跳过路径规范化，设备名按字面文件名落盘（NTFS 本就允许）；Windows 11 已解除保留名限制，前缀行为一致，单一代码路径覆盖两代系统。Unix 分支保持 `ZipFile.ExtractToDirectory`（Unix 无设备名概念，天然无此坑）。
3. 配套细节：staging 目录从 `Path.GetFullPath` 后的绝对路径拼出（`\\?\` 只吃绝对反斜杠路径）；`JoinEntryPath` 用字符串拼接**不走 GetFullPath**——规范化正是要躲开的东西，`.` 段手动剔除（`\\?\` 不再帮你规范化掉），`\` 按分隔符处理（字面反斜杠在 Windows 文件名里本就非法）。

## 测试边界与残留

- Windows 分支（`\\?\` 解压）在本机 macOS 与 CI ubuntu 都不可达，正确性纯靠推理 + 文档语义；测试覆盖语法校验判定表和 macOS 全链路导入。
- 已知残留（刻意不修）：Windows 11 之前的机器上 `AudioPackPlayer` 的 `File.Exists(…\us\con.mp3)` 探测会被设备重定向吞掉（看不到已安装的文件），`con` 一词回退系统 TTS 发音——降级可接受；修复需把扩展路径贯穿 `IAudioFilePlayer` 各后端，等真实反馈再说。

## 教训

1. **zip-slip 最直观的写法在 Windows 上静默误杀**：拿规范化结果做安全判定前，要想清楚规范化器还会做什么改写。GetFullPath 不是纯字符串运算，它带着 Win32 的路径世界观。
2. **保留设备名的两个隐蔽语义**：扩展名被忽略（`con.mp3` 命中 CON）+ 对路径**最终段**就生效（不用整个路径是 `con`）。普通业务名撞上设备名不是抬杠，词典产品里 `con` 就是词头。
3. **先定位报错出自哪一层**：报错文案是我们自己的 throw，说明解压库没炸、是校验被规范化结果绕懵了——从「字符串比对必然成功却失败」反推，唯一的自由度就是 GetFullPath 的改写行为，根因一查一个准。
