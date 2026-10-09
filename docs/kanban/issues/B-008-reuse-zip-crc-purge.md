---
id: B-008
title: 留盘 zip 复用链上 CRC 失败无 purge 路径，用户卡死解压失败循环
type: bug
priority: P1
size: S
status: done
created: 2026-10-09
updated: 2026-10-09
blocked: none
---

## Goal

损坏的留盘 zip 在「checksum 不可得」时不能把用户锁死在解压失败循环：CRC 失败即 purge 走重下，其余解压失败维持 6fd870d 的留盘复用语义。

## Background

2026-10-09 扫描确认：

- `MainWindow.axaml.cs:650-654`：`TryReuseDownloadedZipAsync` 在 `expected is null` 时无条件信任留盘 zip 并加入 `_verifiedZips`。
- 解压阶段逐条 CRC 失败只显示错误、zip 留盘（6fd870d 刻意语义：重试免重下）；重试 → `_verifiedZips.Contains` 命中 → 再次解压失败。**重启也不解决**：重启清了集合，但离线/格式不识别时 checksum 仍为 null，依旧直接复用。
- 完整死循环链 = 损坏 zip（B-006 拼接、或手工放置）+ checksum 不可得（B-007 格式不识别/离线）。唯一自救是手动删 `%TEMP%\stupiddict-downloads\` 里的 zip。

关键区分（修法核心）：**CRC 失败 ≠ 解压其他失败**。CRC 意味着字节损坏，同一文件复用不可能第二次成功 → purge 安全；而其他解压失败（如中途取消、非损坏类 IO）维持留盘复用不变。修时勿破坏 6fd870d 的「解压失败重试免重下」特性（AGENTS.md 下载链条目有记载）。

## Acceptance Criteria

- [x] 解压 catch 中识别 CRC 类异常（InvalidData/BadEntry 族）→ `PurgeDownloadArtifacts` → 下次重试走重下
- [x] 非损坏类解压失败仍留盘复用（既有三个复用测试 `ExtractionFailureRetryReusesKeptZipWithoutRedownloading` 等全绿——其中一条的夹具按卡面「损坏族 purge」的新语义做了调整，见 Development Log「对既有测试的影响」）
- [x] 取消（OCE）路径不受影响

## Subtasks

- [x] 解压失败分类：CRC 族 vs 其他
- [x] CRC 族 purge + 状态文案
- [x] 回归 + 新增测试

## Dependencies

- none（与 B-006/B-007 同链路但可独立修）

## Test Cases

### TC-001: CRC 损坏 zip 重试后 purge 重下

Steps:
1. 注入含损坏条目的 zip（CRC 不符）到下载目录
2. 首次解压失败 → 点重试

Expected:
- zip 被 purge，走重下链而非再次解压

Result: 通过（`CrcMismatchZipIsPurgedAndRetriedByDownloadingAgain`）。夹具 `MakeCrcMismatchZip`：NoCompression 存储条目落盘后翻转数据区一个字节（解压可解码、CRC 不符——B-006 焊接产物同族），checksum 返回 null（复用链无条件信任的死循环前提全真）；首次解压失败后断言状态行为新增文案 `ExtractCorruptPurged`，点重试断言 `DestinationExisted[1] == false`（purge 证据）、第 2 次下载好包后安装成功——死循环收敛为一次自愈。

### TC-002: 普通解压失败仍复用

Steps:
1. 既有复用测试回归

Expected:
- 全绿（留盘复用语义未变）

Result: 通过。`ExtractionFailureRetryReusesKeptZipWithoutRedownloading`（夹具从损坏 zip 改为 zip-slip zip，语义仍是「非损坏类解压失败留盘复用」，见 Development Log）、`CrossSessionKeptZipReusedWithoutDownloadWhenChecksumMatches`、`CrossSessionStaleZipFailsChecksumAndIsPurgedAndReplaced`、`AudioPackDownloadCancelledRestoresEnabledActionButton`（OCE 路径不受影响）全部绿。

## Development Log

**卡面前提修正（重要发现）**：扫描认定「解压阶段逐条 CRC 失败只显示错误、zip 留盘」，实测（.NET 10 独立探针）`ZipFile`/`ZipArchive` **读取条目时根本不校验 CRC**——存储条目数据区翻转一个字节后逐字节静默读回、无任何异常；deflate 流损坏里只有**流头部**损坏才抛 `InvalidDataException`，流深处翻字节同样静默解码出错误字节——修法对两类都覆盖（CRC 在解压输出上逐块计算，与解码成败无关：不可解码抛 `InvalidDataException`，可解码但 CRC 不符由手工校验抛）。因此 AGENTS.md「远端无 .sha256 时复用靠逐条解压的 CRC 兜底」此前并不成立：CRC 不符的损坏 zip 会**无报错安装损坏内容**，而卡面描述的解压失败死循环实际由不可解码类损坏触发。修法把两半都补齐：

1. **让 CRC 校验真的存在**（`MainWindow.ExtractEntry`）：.NET 10 新增 `ZipArchiveEntry.Crc32`（中央目录值）；逐条解压的拷贝循环改为手工循环，边写边算 CRC-32，EOF 后与 `entry.Crc32` 比对，不符抛 `InvalidDataException`（`ZipCrcMismatchFormat` 文案带条目名）。CRC-32 手写实现（`Assets/Crc32.cs`，反射表 + 标准多项式 0xEDB88320）：避免为 30 行代码引 System.IO.Hashing 包依赖（NuGetAudit 门禁下的新依赖不值），已知答案向量（`"123456789"` → `0xCBF43926`）+ 分块增量一致性 + 与 ZipArchive 发布值对账三重钉住。零长条目/目录条目天然跳过，好条目零误报（`ExtractZipAcceptsWellFormedEntriesWithoutFalseAlarm` 存储与 deflate 混装回归）。
2. **损坏族 purge**（`StartDictionaryDownload`/`StartAudioPackDownload` 的通用 catch）：`ex is InvalidDataException` → `PurgeDownloadArtifacts(destination)` + `_verifiedZips.Remove` + 状态行 `ExtractCorruptPurged`（zh/en 新增）+ 重试按钮；其他异常维持既有文案与留盘。分类依据即卡面核心区分：损坏字节同一文件复用不可能第二次成功 → purge 安全；zip-slip（InvalidOperationException）、IO/权限、OCE 等非损坏类一律不 purge，6fd870d 的复用语义在非损坏族上原样保留。

**对既有测试的影响（须审查重点确认）**：`ExtractionFailureRetryReusesKeptZipWithoutRedownloading` 原夹具是损坏 zip（deflate 流翻转 → InvalidDataException），恰落入卡面新划定的 purge 族——卡面「既有测试全绿」的前提与「InvalidData 族 purge」的 AC 自相矛盾（扫描时未意识到夹具本身就是损坏族）。按卡面语义取舍：purge 族成立（这正是卡面死循环场景：不可解码焊接 zip + checksum 不可得 → 重试永远再失败），该测试夹具改为 zip-slip zip（内容完好、非损坏类），复用语义的断言原样保留全绿。`ExtractZipFailureLeavesDestinationUntouched`（直调 ExtractZip、不经 purge 逻辑）不受影响。

**审查备注（防未来误判回归）**：中央目录 CRC=0/错值的手工构造 zip 会被本校验判损坏并 purge——对真实资产不可达：真实写入方（Info-ZIP `zip`、`ZipFile.CreateFromDirectory`/`ZipArchive` 写路径）都会回填中央目录 CRC，`.sha256` 侧也另有防线。

**收尾补丁（独立审查 APPROVE 意见）**：两处损坏族 catch 里的 `PurgeDownloadArtifacts` 自身无防护——Windows 杀软/索引器瞬时锁文件会让 `File.Delete` 抛 IOException 逃出 async void 崩进程（与 e129f6b ④ 同类场景）。已照 `TryReuseDownloadedZipAsync` :789 的既有前例给两处包 try/catch：purge 失败降级走既有解压失败文案（真实解压错误不吞，也不谎报「已删除」），测试 `PurgeFailureDegradesToPlainExtractFailedTextAndFlowSurvives` 以 `.part` 路径做成目录做确定性故障注入（`File.Delete` 对目录全平台抛 `UnauthorizedAccessException`），断言降级文案 + `.part` 残留 + 流程在下次重试仍收敛到安装成功。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/StupidDict.App/MainWindow.axaml.cs`、`src/StupidDict.App/Assets/AssetDownloadService.cs`（Checksum 兜底联动）、`src/StupidDict.App/Assets/Crc32.cs`（新增）、`src/StupidDict.App/Localization/Translations.cs`
- How to run/verify: `dotnet test StupidDict.slnx`（DownloadFlowTests）
- Results: 2026-10-09 全套通过——Core 45/45，App 164/164（最终计数，含审查收尾的降级测试）（含本卡新增 4 条：CRC 拒绝与误报回归为「修复前实测红→修复后绿」），build 0 警告 0 错误。
