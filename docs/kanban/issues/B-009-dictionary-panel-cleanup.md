---
id: B-009
title: 词典下载面板清理两处：删除 zip 失败误报解压失败 + 取消/失败进度条残留
type: bug
priority: P2
size: S
status: done
created: 2026-10-09
updated: 2026-10-09
blocked: none
---

## Goal

词典下载成功路径不再被 zip 删除失败误报；取消/失败后进度条被清理，与发音包面板行为对称。

## Background

2026-10-09 扫描确认（`MainWindow.axaml.cs`，行号为当时时点）：

1. **`:556-557` `File.Delete(zipPath)` 在 `FinishDictionarySetup()` 之前**：解压全部成功后仅删 zip 失败（Windows AV/索引器短暂占用刚写完的文件）→ 落通用 catch（:568-571）显示「解压失败」，事实是解压已完成但 `_dictionaryAvailable` 未置位、服务未重建、下载面板仍挂着。修法：删除包 try-catch best-effort（残留 zip 无害——成功路径的 zip 留盘只影响下次复用校验，会通过），或把删除挪到 `FinishDictionarySetup()` 之后。
2. **`:544` `DictionaryDownloadBar.IsVisible = true` 后无任何路径再置回 false**：finally（:572-579）只恢复三个按钮。取消在 47% 时状态行写「已取消」而进度条停在 47%；失败路径同理残留。发音包侧 :788-798 的取消分支显式 `AudioPackBar.IsVisible = false`，是正确参照。

## Acceptance Criteria

- [x] zip 删除失败不影响「已装好」的最终状态（setup 完成、面板消失、状态文案正确）
- [x] 取消/失败后 `DictionaryDownloadBar` 隐藏
- [x] 发音包面板既有清理行为不受影响
- [x] 发音包流同款问题一并修复（范围扩展，见 Development Log）：删除失败不误报「发音包解压失败」、完成动作（面板隐藏）落地

## Subtasks

- [x] 词典成功路径删除 best-effort 化
- [x] finally 补 `DictionaryDownloadBar.IsVisible = false`
- [x] 补两条 headless 测试
- [x] 发音包流同款修复 + 镜像测试（审查员最小修法，范围扩展并入本卡）

## Dependencies

- none

## Test Cases

### TC-001: 删除失败仍完成 setup

Steps:
1. 注入删除失败缝（占用/只读），走词典下载成功路径

Expected:
- `FinishDictionarySetup` 正常执行，状态非「解压失败」

Result: 通过（`ZipDeleteFailureStillCompletesSetup`）。故障注入沿用 `PurgeFailure…` 的纯文件系统惯例、不上测试缝：新桩 `UndeletableZipDownloader` 供真实词典 zip（含真实 db）后把文件变「删不掉」——Windows 用文件只读属性、unix 用下载目录不可写（`File.SetUnixFileMode`，各自让 `File.Delete` 抛 `UnauthorizedAccessException`）。断言下载面板消失、`dictionary.db` 落盘、状态行停在「解压中…」（成功路径最后一次状态写入；修复前此处是「解压失败：…」且面板不消失）、zip 仍在盘（证明故障确实触发，测试不空转）。

### TC-002: 取消后进度条隐藏

Steps:
1. 下载中取消

Expected:
- `DictionaryDownloadBar.IsVisible == false`，状态行「已取消」

Result: 通过（`DictionaryDownloadCancelledHidesProgressBar`）。新桩 `HangAfterProgressDownloader` 先报一帧确定态进度（47/100 MB）再挂死等取消——进度条先被真实画到百分比上，隐藏断言不可能在从未显示的条上空过；点取消后断言 bar 隐藏、状态行「已取消下载。…」、下载按钮回归且取消按钮消失（终态完整）。

### TC-003: 发音包流删除失败仍完成安装（范围扩展）

Steps:
1. 注入删除失败缝（同 TC-001 机制），走发音包下载成功路径

Expected:
- 面板隐藏（完成动作落地），状态/按钮终态不被误报成「发音包解压失败」

Result: 通过（`AudioPackZipDeleteFailureStillCompletesInstall`）。形状对齐词典侧 TC-001：db 预置使构造器直启发音包流（词典流不运行），同一 `UndeletableZipDownloader` 故障注入（Windows 文件只读属性 / unix 目录不可写）。断言 `AudioPackPanel` 隐藏、uk/us 落盘、状态停在条目级解压进度（`ExtractingFilesFormat` 2/2——`ExtractZip` 对 `done == total` 强制上报，末次 Progress post 按 FIFO 先于隐藏面板的 await 续体处理，精确断言成立）、按钮不转「重试」、zip 仍在盘证明故障触发。

## Development Log

根因（与卡面一致；行号已漂移，以内容定位 `MainWindow.axaml.cs` 词典下载流）：

- ① 成功路径 `File.Delete(zipPath)` 在 `FinishDictionarySetup()` 之前——解压全部成功后仅删 zip 失败（Windows AV/索引器短暂占用刚写完的文件）落通用 catch 显示「解压失败」，此时解压已完成但 `_dictionaryAvailable` 未置位、服务未重建、下载面板仍挂着。
- ② `StartDictionaryDownload` 起手 `DictionaryDownloadBar.IsVisible = true` 后无任何路径置回 false——finally 只恢复三个按钮，取消/失败后进度条冻结在最后百分比。

修法（`MainWindow.axaml.cs` 两处）：

- ① 卡面给了两个等价方向（删除包 try-catch best-effort，或把删除挪到 `FinishDictionarySetup()` 之后），两件都做：`FinishDictionarySetup()` 提前，删除降级为 `try { File.Delete(zipPath); } catch { /* the zip stays behind for the reuse path */ }`。setup 先落地保证「已装好」的用户状态先于任何清理工作出现，删除异常从此绝无可能染指状态文案；残留 zip 无害——「完整 zip 留盘复用」是既有语义，下次下载先走 `TryReuseDownloadedZipAsync` 重验校验，而本 zip 的字节已知通过校验（成功路径=下载+校验+逐条 CRC 三关全过）。
- ② finally 补 `DictionaryDownloadBar.IsVisible = false`，单点覆盖取消/失败/成功三族终态（成功时面板已被 `FinishDictionarySetup` 整体隐藏，这行只是把 bar 自身标志归位，无视觉差异；取消后重开下载由 `StartDictionaryDownload` 起手重新置 true，无回归）。发音包侧按分支显式隐藏的既有写法一字未动（AC-3），两条流殊途同归。

测试（`tests/StupidDict.App.Tests/DownloadFlowTests.cs`：+2 用例、+2 下载桩、既有失败路径用例补 1 断言）：

- `ZipDeleteFailureStillCompletesSetup`（TC-001）如上。平台守卫形状 `if (OperatingSystem.IsWindows()) File.SetAttributes(…ReadOnly) else File.SetUnixFileMode(…UserRead|UserExecute)` 经独立探针项目实证：net10.0 中立 TFM + `TreatWarningsAsErrors` 下 0 警告（分析器认 `IsWindows()` 的 else 分支），且 macOS 实测目录不可写时 `File.Delete` 确抛 `UnauthorizedAccessException`。CI（ubuntu，非 root）与 Windows（只读属性与特权无关）机制同样成立；若某环境删除意外成功，`Assert.True(File.Exists(destination))` 会以明确信号暴露前提失效。
- `DictionaryDownloadCancelledHidesProgressBar`（TC-002）如上。无 db 注入 → 词典面板接管窗口、发音包流永不启动，两条进度条无交互。
- 既有 `ChecksumMismatchTwiceReportsChecksumFailureNotDownloadFailure` 补 `Assert.False(bar.IsVisible)`：失败族与取消族同走 finally 一行，但 AC 的「失败后隐藏」后半句值得独立钉住。
- 无新增用户可见文案、无新增颜色：本卡是去掉一次误报 + 清理终态 UI，Translations/Palette 零改动。

范围扩展（独立审查 APPROVE + 邻接问题 ADJACENT: CONFIRMED 后，PM 决定同根因同修法并入本卡，不另立卡）：

- 确认链：审查员核实发音包流 `StartAudioPackDownload` 的 `File.Delete(zipPath)` 同样在完成动作（`AudioPackPanel.IsVisible = false`）之前，且触发概率高于词典侧——发音包十几万小文件解压完删 zip，正是 AV/索引器最易咬住刚写完文件的时刻；误报「发音包解压失败」+ 按钮转「重试」，而重试被 `AudioPackInstalled()`（uk/ 已存在）短路成 no-op，面板/文案冻结到重启，UI 零恢复。
- 修法（审查员最小修法，与词典侧同款）：`AudioPackPanel.IsVisible = false` 提到删除之前，删除包 `try { File.Delete(zipPath); } catch { /* the zip stays behind for the reuse path */ }`。审查员核过的小心事项逐条对照：完成动作只有隐藏面板一行、无连带；`ExtractCorruptPurged` 分支在解压阶段先于删除、未动（其 purge 已是 best-effort）；成功路径 `AudioPackBar.IsVisible` 维持现状（面板已藏、起手会重置）；`AudioPackInstalled()` 短路守卫语义正确、未改。
- 镜像测试 `AudioPackZipDeleteFailureStillCompletesInstall`：形状对齐词典侧 TC-001——db 预置使构造器直启发音包流（词典流不运行），同一 `UndeletableZipDownloader` 故障注入（Windows 只读属性 / unix 目录不可写），断言面板隐藏、uk/us 落盘、状态停在条目级解压进度（`ExtractingFilesFormat` 2/2——`ExtractZip` 对 `done == total` 强制上报，末次 Progress post 按 FIFO 先于隐藏面板的 await 续体处理，精确断言成立）、按钮不转「重试」、zip 仍在盘证明故障触发。
- 桩顺手改（审查备注 1，非阻塞项）：`UndeletableZipDownloader` 参数化 `expectedAsset`，非本资产请求从 `Assert.Equal`（xunit 异常被流的 catch 链吞掉后与通过的流无法区分，正是困惑来源）改为明确拒绝的普通异常——词典侧 TC-001 中 `FinishDictionarySetup` 自动排队的发音包下载骑这条拒绝走自己的错误路径，行为平台无关（拒绝发生在触碰只读目录之前）；`ScriptedDownloader`/`HangAfterProgressDownloader` 的断言守卫属既有惯例且各自测试无跨资产命中，不动。

评估过 / 顺带发现（未修）：

- 取消后再次下载时进度条以旧 Value 起步、直到下一帧进度覆盖（两条流皆然的既有行为，卡面未要求，未动）。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/StupidDict.App/MainWindow.axaml.cs`、`tests/StupidDict.App.Tests/DownloadFlowTests.cs`
- How to run/verify: `dotnet test StupidDict.slnx`
- Results: 2026-10-09 全套通过（范围扩展并入后复跑）——Core 45/45，App 167/167（含本卡新增 3 条 + 既有用例补断言；其中 DownloadFlowTests 22/22），build 0 警告 0 错误（TreatWarningsAsErrors），net10.0-windows TFM 编译通过。
